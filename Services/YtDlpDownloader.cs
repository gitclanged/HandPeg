using System.IO;
using CliWrap;

namespace HandPegApp.Services;

public static class YtDlpDownloader
{
    // Written into each download's folder: what was asked for, so the same request can reuse the file.
    private const string RequestFileName = "request.txt";

    /// <summary>Downloads land in their own folder each, so the result is easy to find afterwards.</summary>
    public static string DownloadRoot { get; } = Path.Combine(Path.GetTempPath(), "Handpeg", "downloads");

    /// <summary>Maps a resolution label such as "1080p" or "Best" to a yt-dlp format selector.</summary>
    public static string BuildFormat(string resolution) =>
        int.TryParse(resolution.TrimEnd('p', 'P'), out var height)
            ? $"bestvideo[height<={height}]+bestaudio/best"
            : "bestvideo+bestaudio/best";

    /// <summary>
    /// Downloads the URL with the local yt-dlp and returns the path of the resulting file. A download
    /// made earlier with the same options, and still in the cache, is returned without downloading again.
    /// </summary>
    public static async Task<string> DownloadAsync(
        string url, string resolution, bool downloadSubtitles, string sponsorBlockCategories,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        // Everything that changes what yt-dlp would produce.
        var request = $"{url}\n{resolution}\n{downloadSubtitles}\n{sponsorBlockCategories}";
        if (FindCached(request) is { } cached)
        {
            progress.Report("Using the copy downloaded earlier.");
            return cached;
        }

        var directory = Path.Combine(DownloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        // Embedded rather than left as loose files, so the subtitles show up as tracks of the download.
        string[] subtitleArguments = downloadSubtitles ? ["--write-subs", "--write-auto-subs", "--embed-subs"] : [];

        // yt-dlp cuts the chosen SponsorBlock segments out of the file after downloading it.
        string[] sponsorBlockArguments = sponsorBlockCategories.Length > 0 ? ["--sponsorblock-remove", sponsorBlockCategories] : [];

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
                .Add("-P").Add(directory)
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

        var file = FindMediaFile(directory)
                   ?? throw new InvalidOperationException("yt-dlp finished without producing a file.");

        // yt-dlp has exited, so the file should be complete. Make sure of it before anything opens it:
        // a player given a file that is missing, empty or still being written has nothing to draw on.
        await WaitUntilReadableAsync(file, cancellationToken);

        File.WriteAllText(Path.Combine(directory, RequestFileName), request);
        return file;
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
                if (File.Exists(requestFile) && File.ReadAllText(requestFile) == request && FindMediaFile(directory.FullName) is { } file)
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
    /// Keeps the most recently used downloads, up to <paramref name="keep"/>, and deletes the rest.
    /// </summary>
    public static void TrimDownloads(int keep)
    {
        if (!Directory.Exists(DownloadRoot))
            return;

        try
        {
            var stale = new DirectoryInfo(DownloadRoot).EnumerateDirectories()
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
