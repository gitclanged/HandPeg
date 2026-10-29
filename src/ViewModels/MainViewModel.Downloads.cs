using System.IO;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Downloads used by projects (see YtDlpDownloader): the download cache keeps only its most recent files, so a
// project that uses one of them could open one day with its media gone. Downloads a project uses are kept when the
// cache is trimmed, and saving such a project offers to copy them somewhere permanent (both Settings, on).
public partial class MainViewModel
{
    /// <summary>Every media file the open project uses: the main video, the layers' files, the voiceover, the captions' own sound.</summary>
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
        return files;
    }

    /// <summary>The download folders that must not be trimmed now: used by a project in the projects folder, or by the open one.</summary>
    public HashSet<string> PinnedDownloads() => AppSettings.Current.KeepDownloadsUsedByProjects
        ? YtDlpDownloader.FoldersUsedByProjects([ProjectStore.Folder, .. CurrentProjectPath.Length > 0 && Path.GetDirectoryName(CurrentProjectPath) is { } folder ? [folder] : Array.Empty<string>()], MediaFilesInUse())
        : [];

    /// <summary>
    /// After a project is saved: when it uses files from the download cache, offers to copy them into the download
    /// folder (Settings; the Videos folder's "HandPeg Downloads" when none is set) and to have the project use the
    /// copies, which nothing ever deletes. The copying is interactive work (see WorkScheduler); the project file is
    /// then written again with the copies in it.
    /// </summary>
    private async Task OfferToKeepDownloadsAsync()
    {
        if (_isBackgroundWorker || !AppSettings.Current.AskToCopyDownloadsOnSave || Confirm is null)
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
        var them = cached.Count == 1 ? "it" : "them";
        if (!Confirm("Keep the Downloaded Videos",
                $"This project uses {(cached.Count == 1 ? "a video" : $"{cached.Count} videos")} from the download cache, where the oldest downloads are deleted to make room for new ones. "
                + $"Copy {them} into {folder}, and have the project use the copies? (Settings, Downloads, can stop this question.)", "Copy"))
        {
            return;
        }

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
            StatusText = $"The downloaded videos could not all be copied: {ex.Message}";
            if (copies.Count == 0)
                return;
        }

        // The saved project uses the copies from now on. (This session goes on with the files it has open, which are
        // kept while it is: see PinnedDownloads.)
        if (CurrentProjectPath.Length == 0)
            return;

        try
        {
            var state = CaptureState(Path.GetFileNameWithoutExtension(CurrentProjectPath));
            string Moved(string path) => copies.TryGetValue(path.Trim().Trim('"'), out var copy) ? copy : path;
            state.LocalMediaPath = Moved(state.LocalMediaPath);
            state.CaptionAudioPath = Moved(state.CaptionAudioPath);

            // Every clip's file, on the state's own copy of the timeline (its looks are its own: CaptureState).
            foreach (var clip in state.Timeline.Tracks.SelectMany(t => t.Clips))
            {
                if (clip.SourcePath is { } path)
                    clip.SourcePath = Moved(path);
                clip.State.ImagePath = Moved(clip.State.ImagePath);
            }

            ProjectStore.SaveTo(state, CurrentProjectPath);
            Log($"Copied {copies.Count} downloaded video{(copies.Count == 1 ? "" : "s")} into {folder}; the project now uses the copies");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StatusText = $"The videos were copied, but the project could not be saved with them: {ex.Message}";
        }
    }
}
