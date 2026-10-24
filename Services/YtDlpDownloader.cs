using System.IO;
using CliWrap;

namespace HandPegApp.Services;

public static partial class YtDlpDownloader
{
    // Written into each download's folder: what was asked for, so the same request can reuse the file.
    private const string RequestFileName = "request.txt";

    // Written instead of the file, when it was saved to the download folder: where it went.
    private const string SavedFileName = "saved.txt";

    /// <summary>Downloads land in their own folder each, so the result is easy to find afterwards.</summary>
    public static string DownloadRoot => AppPaths.Downloads;

    /// <summary>Maps a resolution label such as "1080p" or "Best" to a yt-dlp format selector.</summary>
    public static string BuildFormat(string resolution) =>
        int.TryParse(resolution.TrimEnd('p', 'P'), out var height)
            ? $"bestvideo[height<={height}]+bestaudio/best"
            : "bestvideo+bestaudio/best";

    /// <summary>
    /// Downloads the URL with the local yt-dlp and returns the path of the resulting file. A download
    /// made earlier with the same options, and still in the cache, is returned without downloading again.
    /// </summary>
    /// <param name="saveFolder">
    /// A folder of the user's to save the file in, to keep; blank keeps it in the cache. Either way the cache
    /// remembers the request, so loading the same URL again finds the file wherever it was saved.
    /// </param>
    public static Task<string> DownloadAsync(
        string url, string resolution, bool downloadSubtitles, string sponsorBlockCategories, string saveFolder,
        IProgress<string> progress, CancellationToken cancellationToken) =>
        WorkScheduler.Run(new WorkSpec($"Downloading {url}", WorkLane.Interactive), work =>
            DownloadNowAsync(url, resolution, downloadSubtitles, sponsorBlockCategories, saveFolder, new Progress<string>(line =>
            {
                progress.Report(line);
                work.Report(line);
                if (DownloadPercent().Match(line) is { Success: true } match
                    && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
                {
                    work.Report(percent / 100);
                }
            }), work.Token), cancellationToken);

    // yt-dlp's progress lines: "[download]  42.3% of ...".
    [System.Text.RegularExpressions.GeneratedRegex(@"\[download\]\s+(\d+(?:\.\d+)?)%")]
    private static partial System.Text.RegularExpressions.Regex DownloadPercent();

    private static async Task<string> DownloadNowAsync(
        string url, string resolution, bool downloadSubtitles, string sponsorBlockCategories, string saveFolder,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        // Everything that changes what yt-dlp would produce.
        var request = $"{url}\n{resolution}\n{downloadSubtitles}\n{sponsorBlockCategories}";
        if (FindCached(request) is { } cached)
        {
            progress.Report("Using the copy downloaded earlier.");
            return cached;
        }

        var name = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(DownloadRoot, name);
        Directory.CreateDirectory(directory);

        // Saved for keeps: yt-dlp works in a folder of its own inside the chosen one (so what it leaves behind
        // is easy to tell apart), and the finished files are moved out of it, which on one drive is instant.
        saveFolder = saveFolder.Trim().Trim('"');
        var work = directory;
        if (saveFolder.Length > 0)
        {
            Directory.CreateDirectory(saveFolder);
            work = Path.Combine(saveFolder, $".handpeg-{name}");
            Directory.CreateDirectory(work);
        }

        // Embedded rather than left as loose files, so the subtitles show up as tracks of the download.
        string[] subtitleArguments = downloadSubtitles ? ["--write-subs", "--write-auto-subs", "--embed-subs"] : [];

        // yt-dlp cuts the chosen SponsorBlock segments out of the file after downloading it.
        string[] sponsorBlockArguments = sponsorBlockCategories.Length > 0 ? ["--sponsorblock-remove", sponsorBlockCategories] : [];

        string file;
        try
        {
            string? lastError = null;
            var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.YtDlpPath)
                .WithArguments(args => args
                    .Add("-f").Add(BuildFormat(resolution))
                    .Add(subtitleArguments)
                    .Add(sponsorBlockArguments)
                    .Add("--no-playlist")
                    .Add("--newline")
                    .Add("--encoding").Add("utf-8")
                    .Add("--ffmpeg-location").Add(DependencyUpdater.FfmpegPath)
                    .Add("-P").Add(work)
                    .Add("-o").Add("%(title).80B [%(id)s].%(ext)s")
                    .Add(url))
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(ProcessPipes.Lines(progress.Report))
                .WithStandardErrorPipe(ProcessPipes.Lines(line =>
                {
                    lastError = line;
                    progress.Report(line);
                })),
                cancellationToken);

            if (result.ExitCode != 0)
                throw new InvalidOperationException(lastError ?? $"yt-dlp exited with code {result.ExitCode}.");

            file = FindMediaFile(work)
                   ?? throw new InvalidOperationException("yt-dlp finished without producing a file.");

            // yt-dlp has exited, so the file should be complete. Make sure of it before anything opens it:
            // a player given a file that is missing, empty or still being written has nothing to draw on.
            await WaitUntilReadableAsync(file, cancellationToken);
        }
        catch when (!ReferenceEquals(work, directory))
        {
            // A failed or cancelled download leaves nothing behind in the user's folder.
            TryDelete(work);
            throw;
        }

        if (!ReferenceEquals(work, directory))
        {
            file = MoveOut(work, file, saveFolder);
            File.WriteAllText(Path.Combine(directory, SavedFileName), file);
        }

        File.WriteAllText(Path.Combine(directory, RequestFileName), request);
        return file;
    }

    /// <summary>
    /// Moves what yt-dlp left in its working folder into the folder it was saved to, under a name no file there
    /// has yet, and removes the working folder. Returns where the media file went.
    /// </summary>
    private static string MoveOut(string work, string file, string folder)
    {
        var moved = file;
        foreach (var part in new DirectoryInfo(work).GetFiles().Where(f => f.Extension is not (".part" or ".ytdl")))
        {
            var target = Path.Combine(folder, part.Name);
            for (var n = 2; File.Exists(target); n++)
                target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(part.Name)} ({n}){part.Extension}");

            var isMedia = string.Equals(part.FullName, file, StringComparison.OrdinalIgnoreCase);
            part.MoveTo(target);
            if (isMedia)
                moved = target;
        }

        TryDelete(work);
        return moved;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind with a partial file in it: harmless, and in the user's own folder to delete.
        }
    }

    /// <summary>
    /// Returns once the file exists, has content and can be opened without anyone else still writing to it.
    /// </summary>
    private static async Task WaitUntilReadableAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    // Opening for reading while refusing writers fails for as long as a writer has it open.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return;
                }
            }
            catch (IOException)
            {
                // Still held by the post-processing step (merging, embedding subtitles); try again shortly.
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException($"The download did not finish writing: {Path.GetFileName(path)}");
    }

    /// <summary>The finished download in a folder, or null when there is none.</summary>
    private static string? FindMediaFile(string directory) =>
        // Leftover .part/.ytdl files mean an incomplete download; the finished file is the big one.
        new DirectoryInfo(directory).EnumerateFiles()
            .Where(f => f.Name != RequestFileName && f.Extension is not (".part" or ".ytdl" or ".vtt" or ".srt"))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault()?.FullName;

    private static string? FindCached(string request)
    {
        if (!Directory.Exists(DownloadRoot))
            return null;

        foreach (var directory in new DirectoryInfo(DownloadRoot).EnumerateDirectories())
        {
            try
            {
                var requestFile = Path.Combine(directory.FullName, RequestFileName);
                if (!File.Exists(requestFile) || File.ReadAllText(requestFile) != request)
                    continue;

                // Saved to the download folder: found there, unless it has been moved or deleted since.
                var savedFile = Path.Combine(directory.FullName, SavedFileName);
                var file = File.Exists(savedFile) ? File.ReadAllText(savedFile).Trim() : FindMediaFile(directory.FullName);
                if (file is not null && File.Exists(file))
                {
                    // Used again, so it counts as recent when the cache is trimmed.
                    directory.LastWriteTime = DateTime.Now;
                    return file;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder is simply not a cache hit.
            }
        }

        return null;
    }

    /// <summary>
    /// The download folders (by name) that projects use: those saved in the projects folder, and the files given
    /// (what the open project uses). Their downloads are kept whatever the cache's size (Settings: Keep downloads
    /// used by projects). Projects are not opened, only searched for the download folder's path.
    /// </summary>
    public static HashSet<string> FoldersUsedByProjects(IEnumerable<string> projectFolders, IEnumerable<string> filesInUse)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root;
        try
        {
            root = Path.GetFullPath(DownloadRoot).TrimEnd('\\') + "\\";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return used;
        }

        void Note(string path)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length..].Split('\\')[0] is { Length: > 0 } folder)
                used.Add(folder);
        }

        foreach (var file in filesInUse)
        {
            try
            {
                Note(Path.GetFullPath(file.Trim().Trim('"')));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        // In a project's JSON the path is written with its backslashes doubled.
        var written = root.Replace("\\", "\\\\");
        foreach (var folder in projectFolders.Where(Directory.Exists))
        {
            try
            {
                foreach (var project in Directory.EnumerateFiles(folder, "*.hproj"))
                {
                    var text = File.ReadAllText(project);
                    for (var at = text.IndexOf(written, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(written, at + written.Length, StringComparison.OrdinalIgnoreCase))
                    {
                        var start = at + written.Length;
                        var end = text.IndexOf("\\\\", start, StringComparison.Ordinal);
                        if (end > start)
                            used.Add(text[start..end]);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return used;
    }

    /// <summary>
    /// Keeps the most recently used downloads, up to <paramref name="keep"/>, and deletes the rest; never those whose
    /// folder is in <paramref name="pinned"/> (used by a project).
    /// </summary>
    public static void TrimDownloads(int keep, IReadOnlySet<string>? pinned = null)
    {
        if (!Directory.Exists(DownloadRoot))
            return;

        try
        {
            var stale = new DirectoryInfo(DownloadRoot).EnumerateDirectories()
                .Where(d => pinned?.Contains(d.Name) != true)
                .OrderByDescending(d => d.LastWriteTime)
                .Skip(Math.Max(keep, 0))
                .ToList();

            foreach (var directory in stale)
            {
                try
                {
                    directory.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file still in use is left for the next run to clean up.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
