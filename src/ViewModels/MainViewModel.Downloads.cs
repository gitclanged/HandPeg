using System.IO;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Project-referenced downloads (YtDlpDownloader): the download cache keeps only recent files, so project-used downloads are pinned
// during trims, and saving such a project offers (or, once chosen, performs) a copy to permanent storage (Settings > Preview &
// Cache > Downloads, "Downloaded videos in saved projects").
public partial class MainViewModel
{
    /// <summary>
    /// All media files the open project references: source video, layer files, voiceover, caption audio and Project Media entries
    /// (bin-only downloads are pinned and copied like clip-used ones).
    /// </summary>
    public List<string> MediaFilesInUse()
    {
        var files = new List<string>();
        void Add(string? path)
        {
            var file = (path ?? "").Trim().Trim('"');
            if (file.Length > 0 && !files.Contains(file, StringComparer.OrdinalIgnoreCase))
                files.Add(file);
        }

        if (HasSource)
            Add(LocalMediaPath);
        foreach (var layer in Layers.Where(l => !l.IsDeleted && (l.IsVideoFile || l.IsImage || l.IsAudio)))
            Add(layer.ImagePath);
        Add(VoiceoverPath);
        Add(CaptionAudioPath);
        foreach (var pooled in Document.MediaPool)
            Add(pooled);
        return files;
    }

    /// <summary>Download folders exempt from trimming: used by a project in the projects folder or the open one.</summary>
    public HashSet<string> PinnedDownloads() => AppSettings.Current.KeepDownloadsUsedByProjects
        ? YtDlpDownloader.FoldersUsedByProjects([ProjectStore.Folder, .. CurrentProjectPath.Length > 0 && Path.GetDirectoryName(CurrentProjectPath) is { } folder ? [folder] : Array.Empty<string>()], MediaFilesInUse())
        : [];

    /// <summary>
    /// Post-save: if the project uses cached downloads, offers (or, per "Remember my choice"/Settings, performs) a copy to the download
    /// folder (Settings; default Videos\"HandPeg Downloads"), never auto-deleted, and repoints the project. Copying runs as interactive
    /// work (WorkScheduler); the project file is then rewritten.
    /// </summary>
    private async Task OfferToKeepDownloadsAsync()
    {
        var settings = AppSettings.Current;
        var asks = settings.AskToCopyDownloadsOnSave;
        if (_isBackgroundWorker || !asks && !settings.AlwaysCopyDownloadsOnSave || asks && Confirm is null && ConfirmOrRemember is null)
            return;

        string root;
        try
        {
            root = Path.GetFullPath(YtDlpDownloader.DownloadRoot).TrimEnd('\\') + "\\";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        var cached = MediaFilesInUse().Where(f => File.Exists(f) && Path.GetFullPath(f).StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cached.Count == 0)
            return;

        var folder = AppSettings.Current.DownloadFolder.Trim().Trim('"') is { Length: > 0 } chosen
            ? chosen
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "HandPeg Downloads");
        if (asks && !AskToKeepDownloads(cached.Count, folder))
            return;

        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await WorkScheduler.Run(new WorkSpec("Copying downloaded videos", WorkLane.Interactive), async work =>
            {
                Directory.CreateDirectory(folder);
                for (var i = 0; i < cached.Count; i++)
                {
                    work.Report((double)i / cached.Count, Path.GetFileName(cached[i]));
                    var target = Path.Combine(folder, Path.GetFileName(cached[i]));
                    for (var n = 2; File.Exists(target); n++)
                        target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(cached[i])} ({n}){Path.GetExtension(cached[i])}");
                    var (from, to) = (cached[i], target);
                    await Task.Run(() => File.Copy(from, to), work.Token);
                    copies[from] = to;
                }
            }, _shutdown.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Fail($"The downloaded videos could not all be copied: {ex.Message}", "Copy the downloaded videos", ex);
            if (copies.Count == 0)
                return;
        }

        // The saved project now references the copies; this session keeps using the open files (pinned via PinnedDownloads).
        if (CurrentProjectPath.Length == 0)
            return;

        try
        {
            // Repoint undo history steps too, so undoing after reopen restores the copies rather than possibly-evicted cache files.
            string Moved(string path) => copies.TryGetValue(path.Trim().Trim('"'), out var copy) ? copy : path;
            var state = CaptureStateForSave(Path.GetFileNameWithoutExtension(CurrentProjectPath), Moved);
            state.LocalMediaPath = Moved(state.LocalMediaPath);
            state.CaptionAudioPath = Moved(state.CaptionAudioPath);

            // Repoint every clip file in the state's own timeline copy (CaptureState).
            foreach (var clip in state.Timeline.Tracks.SelectMany(t => t.Clips))
            {
                if (clip.SourcePath is { } path)
                    clip.SourcePath = Moved(path);
                clip.State.ImagePath = Moved(clip.State.ImagePath);
            }

            // And Project Media entries, which would otherwise lose their file on the next cache trim.
            state.Timeline.MediaPool = [.. state.Timeline.MediaPool.Select(Moved)];

            ProjectStore.SaveTo(state, CurrentProjectPath);
            Log($"Copied {copies.Count} downloaded video{(copies.Count == 1 ? "" : "s")} into {folder}; the project now uses the copies");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Fail($"The videos were copied, but the project could not be saved with them: {ex.Message}", "Save the project with its copied videos", ex);
        }
    }

    /// <summary>
    /// Prompts to copy a saved project's downloads (naming the Settings location); "Remember my choice" persists the answer (always
    /// copy or never) to settings.
    /// </summary>
    private bool AskToKeepDownloads(int count, string folder)
    {
        const string title = "Keep the Downloaded Videos";
        var message = $"This project uses {(count == 1 ? "a video" : $"{count} videos")} from the download cache, where the oldest downloads are deleted to make room for new ones. "
                      + $"Copy {(count == 1 ? "it" : "them")} into {folder}, and have the project use {(count == 1 ? "the copy" : "the copies")}?"
                      + "\n\nYou can change this in Settings > Preview & Cache > Downloads (\"Downloaded videos in saved projects\").";

        if (ConfirmOrRemember is not { } askOnce)
            return Confirm?.Invoke(title, message, "Copy") == true;

        var (confirmed, remember) = askOnce(title, message, "Copy", "Don't Copy");
        if (remember)
        {
            SaveView(s => (s.AskToCopyDownloadsOnSave, s.AlwaysCopyDownloadsOnSave) = (false, confirmed));
            Log(confirmed
                ? "Downloaded videos in saved projects are copied from now on (Settings > Preview & Cache > Downloads)"
                : "Downloaded videos in saved projects are left in the download cache from now on (Settings > Preview & Cache > Downloads)");
        }

        return confirmed;
    }
}
