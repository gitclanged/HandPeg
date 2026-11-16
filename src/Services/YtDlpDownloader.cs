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
    /// Downloads the URL with the local yt-dlp and returns the file path; a cached download with the same options is reused.
    /// </summary>
    /// <param name="saveFolder">
    /// Permanent destination folder; blank keeps the file in the cache. The cache records the request either way so the same URL resolves
    /// to the saved file.
    /// </param>
    public static Task<string> DownloadAsync(
        string url, string resolution, bool downloadSubtitles, string sponsorBlockCategories, bool markSponsorBlock, string saveFolder,
        IProgress<string> progress, CancellationToken cancellationToken) =>
        WorkScheduler.Run(new WorkSpec($"Downloading {url}", WorkLane.Interactive), work =>
            DownloadNowAsync(url, resolution, downloadSubtitles, sponsorBlockCategories, markSponsorBlock, saveFolder, new Progress<string>(line =>
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
        string url, string resolution, bool downloadSubtitles, string sponsorBlockCategories, bool markSponsorBlock, string saveFolder,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        // Everything that changes what yt-dlp would produce.
        var request = $"{url}\n{resolution}\n{downloadSubtitles}\n{sponsorBlockCategories}{(markSponsorBlock ? "\nmark" : "")}";
        if (FindCached(request) is { } cached)
        {
            progress.Report("Using the copy downloaded earlier.");
            return cached;
        }

        var name = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(DownloadRoot, name);
        Directory.CreateDirectory(directory);

        // Permanent save: yt-dlp works in a private subfolder (isolating leftovers); finished files are moved out (instant on one volume).
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

        // SponsorBlock segments are removed after download, or marked as named chapters (listed by Copy YouTube Timestamps).
        string[] sponsorBlockArguments = sponsorBlockCategories.Length == 0 ? []
            : markSponsorBlock ? ["--sponsorblock-mark", sponsorBlockCategories, "--embed-chapters"]
            : ["--sponsorblock-remove", sponsorBlockCategories];

        // One run of yt-dlp, with cookies or without; what it last said and whether it was a sign-in wall are kept.
        string? lastError = null;
        var walled = false;
        async Task<int> RunAsync(string[] cookieArguments)
        {
            (lastError, walled) = (null, false);
            var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.YtDlpPath)
                .WithArguments(args => args
                    .Add("-f").Add(BuildFormat(resolution))
                    .Add(subtitleArguments)
                    .Add(sponsorBlockArguments)
                    .Add(cookieArguments)
                    .Add("--no-playlist")
                    .Add("--newline")
                    .Add("--encoding").Add("utf-8")
                    .Add("--ffmpeg-location").Add(DependencyUpdater.FfmpegPath)
                    .Add("-P").Add(work)
                    .Add("-o").Add("%(title).80B [%(id)s].%(ext)s")
                    .Add("--")
                    .Add(url))
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(ProcessPipes.Lines(progress.Report))
                .WithStandardErrorPipe(ProcessPipes.Lines(line =>
                {
                    lastError = line;
                    walled |= SignInWall().IsMatch(line);
                    progress.Report(line);
                })),
                cancellationToken);
            return result.ExitCode;
        }

        string file;
        try
        {
            var exitCode = await RunAsync([]);

            // Gated content (age check, sign-in, 403): retry with each allowed cookie source until one succeeds or the error changes.
            // Browsers whose cookies are unreadable (e.g. Chrome/Edge running) are skipped.
            if (exitCode != 0 && walled)
            {
                var (wall, tried) = (lastError, new List<string>());
                foreach (var (label, cookieArguments) in CookieSources())
                {
                    progress.Report($"The site wants a signed-in visitor: trying again with {label}...");
                    tried.Add(label);
                    exitCode = await RunAsync(cookieArguments);
                    if (exitCode == 0 || !walled && !CookieTrouble().IsMatch(lastError ?? ""))
                        break;
                }

                if (exitCode != 0 && (walled || CookieTrouble().IsMatch(lastError ?? "")))
                {
                    lastError = $"{wall} {(tried.Count > 0 ? $"Tried again with {string.Join(", ", tried)}, without success. " : "")}"
                        + "Sign in to the site in a browser, or name a cookies file, under Settings, Preview & Cache, Downloads.";
                }
            }

            if (exitCode != 0)
                throw new InvalidOperationException(lastError ?? $"yt-dlp exited with code {exitCode}.");

            file = FindMediaFile(work)
                   ?? throw new InvalidOperationException("yt-dlp finished without producing a file.");

            // Verify the file is present, non-empty and unlocked before anything opens it.
            await WaitUntilReadableAsync(file, cancellationToken);
        }
        catch when (!ReferenceEquals(work, directory))
        {
            // Failed or cancelled downloads leave nothing in the destination folder.
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

    // yt-dlp messages indicating authentication or age gating ("Sign in to confirm your age", "login required", "HTTP Error 403",
    // "members-only", "Private video", ...).
    [System.Text.RegularExpressions.GeneratedRegex(@"sign[ -]?in|log[ -]?in|login|age[- ]?(restrict|gate|verif)|confirm your age|registered users|--cookies|cookies-from-browser|authenticat|\b403\b|forbidden|members[- ]only|private video", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SignInWall();

    // A browser's cookies that could not be read: its database locked or not found, or not decrypted.
    [System.Text.RegularExpressions.GeneratedRegex(@"cookie|dpapi|decrypt", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CookieTrouble();

    /// <summary>
    /// Ordered cookie sources for gated retries per <see cref="AppSettings.YtDlpCookieSource"/>: Automatic (or unknown) tries each
    /// installed browser then an existing cookies file; otherwise the chosen source; none when Off. Each has a display label, never a path.
    /// </summary>
    private static List<(string Label, string[] Arguments)> CookieSources()
    {
        var settings = AppSettings.Current;
        var source = settings.YtDlpCookieSource.Trim().ToLowerInvariant();
        var sources = new List<(string, string[])>();
        if (source == AppSettings.CookiesOff)
            return sources;

        var automatic = source == AppSettings.CookiesAuto || !AppSettings.YtDlpCookieSources.Any(s => s.Key == source);
        foreach (var (name, label, find) in Browsers)
        {
            if ((automatic || source == name) && find() is { } argument)
                sources.Add(($"{label}'s cookies", ["--cookies-from-browser", argument]));
        }

        var cookiesFile = settings.YtDlpCookiesFilePath.Trim().Trim('"');
        if ((automatic || source == AppSettings.CookiesFile) && cookiesFile.Length > 0 && File.Exists(cookiesFile))
            sources.Add(("the cookies file", ["--cookies", cookiesFile]));

        return sources;
    }

    /// <summary>
    /// Cookie-capable browsers in Automatic order with their --cookies-from-browser value (null when not installed). Waterfox has no
    /// yt-dlp name and is passed as Firefox with its profile folder.
    /// </summary>
    private static readonly (string Name, string Label, Func<string?> Find)[] Browsers =
    [
        ("waterfox", "Waterfox", () => LatestFirefoxProfile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Waterfox", "Profiles")) is { } profile ? $"firefox:{profile}" : null),
        ("firefox", "Firefox", () => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles")) ? "firefox" : null),
        ("edge", "Edge", () => LocalBrowser("edge", "Microsoft", "Edge", "User Data")),
        ("chrome", "Chrome", () => LocalBrowser("chrome", "Google", "Chrome", "User Data")),
        ("brave", "Brave", () => LocalBrowser("brave", "BraveSoftware", "Brave-Browser", "User Data")),
    ];

    /// <summary>yt-dlp browser name when its user data folder exists under LocalAppData.</summary>
    private static string? LocalBrowser(string name, params string[] folder) =>
        Directory.Exists(Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), .. folder])) ? name : null;

    /// <summary>Profile under <paramref name="profiles"/> with the newest cookies file; null if none.</summary>
    private static string? LatestFirefoxProfile(string profiles)
    {
        try
        {
            return Directory.Exists(profiles)
                ? new DirectoryInfo(profiles).EnumerateDirectories()
                    .Select(d => new FileInfo(Path.Combine(d.FullName, "cookies.sqlite")))
                    .Where(f => f.Exists)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.DirectoryName
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves yt-dlp output from its working folder to the destination under a unique name, removes the working folder, and returns the
    /// media file path.
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
            // A leftover folder with a partial file is harmless and user-deletable.
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
    /// Download folder names referenced by saved projects and the given files (open project); exempt from cache trimming (Settings:
    /// Keep downloads used by projects). Projects are text-searched, not parsed.
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
    /// Keeps the <paramref name="keep"/> most recently used downloads and deletes the rest, except folders in <paramref name="pinned"/>.
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
