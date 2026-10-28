using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace HandPegApp.Services;

/// <summary>
/// HandPeg's own updates, through Velopack, from the releases of its public GitHub repository. Only a copy
/// that Velopack installed (or its portable build) can update itself: one run from a build folder skips all
/// of this. Nothing here shows a message. Whatever goes wrong, offline or otherwise, goes to the log.
/// </summary>
public static class AppUpdater
{
    /// <summary>How a check asked for by hand came out.</summary>
    public enum CheckResult
    {
        /// <summary>This is the newest release.</summary>
        UpToDate,

        /// <summary>A newer release has been downloaded and goes in at the next restart.</summary>
        Ready,

        /// <summary>A newer release that this copy can put in by itself has been found; the Update button in the status bar downloads it.</summary>
        Found,

        /// <summary>A newer release exists, but this copy cannot put it in by itself: it is fetched by hand.</summary>
        Available,

        /// <summary>The check could not be made.</summary>
        Failed,
    }

    /// <summary>The version this copy is, as its releases are numbered: 2.6.3.</summary>
    public static Version CurrentVersion { get; } = typeof(AppUpdater).Assembly.GetName().Version is { } version
        ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
        : new Version(0, 0, 0);

    /// <summary>The release that has been downloaded and is waiting to be put in, or null.</summary>
    public static string? ReadyVersion => _ready is null || _handedOver ? null : _ready.Version.ToString();

    /// <summary>Where the releases are listed for people: the page a copy that cannot update itself is sent to.</summary>
    public const string ReleasesPage = AppSettings.HandPegRepositoryUrl + "/releases/latest";

    // One check at a time: the one at startup and one asked for by hand would otherwise download the same release twice.
    private static readonly SemaphoreSlim Checking = new(1, 1);

    /// <summary>
    /// Check for Updates, in the settings: looks now, and says what it found. A copy the installer put in is told
    /// of a newer release, which the Update button in the status bar downloads (or which is downloaded at once when
    /// Settings say to download in the background). A self-contained copy, or one run from a build folder, cannot
    /// replace itself, so it is only told whether GitHub has a newer release than it is.
    /// </summary>
    /// <returns>The outcome, and with it the version found, or why the check failed.</returns>
    public static Task<(CheckResult Result, string Detail)> CheckNowAsync() => Task.Run(async () =>
    {
        // Velopack's bootstrap runs beside the window's start (Program.VelopackStarted): it is done before anything here asks Velopack.
        await Program.VelopackStarted;
        await Checking.WaitAsync();
        try
        {
            if (ReadyVersion is { } waiting)
                return (CheckResult.Ready, waiting);

            if (!AppPaths.IsSelfContained)
            {
                var manager = new UpdateManager(new GithubSource(AppSettings.HandPegRepositoryUrl, accessToken: null, prerelease: false));
                if (manager.IsInstalled)
                {
                    var update = await manager.CheckForUpdatesAsync();
                    if (update is null)
                        return (CheckResult.UpToDate, CurrentVersion.ToString());

                    // Found, and left to the Update button, unless Settings say to download in the background.
                    (_manager, _found) = (manager, update);
                    if (!AppSettings.Current.AutoDownloadAppUpdates)
                        return (CheckResult.Found, update.TargetFullRelease.Version.ToString());

                    await manager.DownloadUpdatesAsync(update);
                    _ready = update.TargetFullRelease;
                    AppLog.Write($"Update {update.TargetFullRelease.Version} downloaded.");
                    return (CheckResult.Ready, update.TargetFullRelease.Version.ToString());
                }
            }

            // Not a copy that updates itself: the newest release's number is read from GitHub and compared with this one's.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HandPeg/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var api = AppSettings.HandPegRepositoryUrl.Replace("https://github.com/", "https://api.github.com/repos/", StringComparison.Ordinal) + "/releases/latest";
            using var release = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(api));
            var tag = release.RootElement.TryGetProperty("tag_name", out var name) ? name.GetString() ?? "" : "";
            if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var latest))
                return (CheckResult.Failed, $"the newest release is named \"{tag}\", which is not a version number");

            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            return latest > CurrentVersion ? (CheckResult.Available, latest.ToString()) : (CheckResult.UpToDate, CurrentVersion.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Update check failed", ex);
            return (CheckResult.Failed, ex.Message);
        }
        finally
        {
            Checking.Release();
        }
    });

    private static UpdateManager? _manager;

    // A newer release that has been found and not downloaded yet; and the one downloaded, waiting to be put in.
    private static UpdateInfo? _found;
    private static VelopackAsset? _ready;
    private static bool _handedOver;

    /// <summary>The newer release found and not downloaded yet (startup checks only look), or null.</summary>
    public static string? FoundVersion => _found is null || _ready is not null ? null : _found.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Looks for a newer release, off the UI thread (Settings: Check for App Updates on Startup). It is only downloaded
    /// when Settings say so (Auto-Download App Updates in Background, off by default): otherwise the Update button
    /// in the status bar downloads it, so closing and opening HandPeg never puts in an update nobody asked for.
    /// Returns its version and whether it is downloaded, or null when there is nothing or the check could not be made.
    /// </summary>
    public static Task<(string Version, bool Downloaded)?> CheckAtStartupAsync() => Task.Run(async () =>
    {
        // A self-contained copy is replaced by hand: the updater keeps its packages outside the program's folder.
        if (AppPaths.IsSelfContained)
            return ((string, bool)?)null;

        // Velopack's bootstrap runs beside the window's start (Program.VelopackStarted): it is done before anything here asks Velopack.
        await Program.VelopackStarted;
        await Checking.WaitAsync();
        try
        {
            // Already found, by a check asked for in the settings.
            if (ReadyVersion is { } waiting)
                return (waiting, true);

            var manager = new UpdateManager(new GithubSource(AppSettings.HandPegRepositoryUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
                return null;

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
                return null;

            (_manager, _found) = (manager, update);
            var version = update.TargetFullRelease.Version.ToString();
            AppLog.Write($"Update {version} found.");
            if (!AppSettings.Current.AutoDownloadAppUpdates)
                return (version, false);

            await manager.DownloadUpdatesAsync(update);
            _ready = update.TargetFullRelease;
            AppLog.Write($"Update {version} downloaded.");
            return (version, true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Update check failed", ex);
            return null;
        }
        finally
        {
            Checking.Release();
        }
    });

    /// <summary>
    /// Downloads the release found (the Update button), reporting 0 to 1. True once it is ready to be put in; it is put
    /// in at the next restart, or when HandPeg closes: it was asked for.
    /// </summary>
    public static async Task<bool> DownloadFoundAsync(Action<double> progress, CancellationToken token)
    {
        await Checking.WaitAsync(token);
        try
        {
            if (_ready is not null)
                return true;
            if (_manager is not { } manager || _found is not { } update)
                return false;

            await manager.DownloadUpdatesAsync(update, percent => progress(percent / 100.0), token);
            _ready = update.TargetFullRelease;
            AppLog.Write($"Update {update.TargetFullRelease.Version} downloaded.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Update download failed", ex);
            return false;
        }
        finally
        {
            Checking.Release();
        }
    }

    /// <summary>
    /// Hands the downloaded release to the updater, which waits for this process to end, puts the new
    /// version in and starts it. The caller then closes the application in the ordinary way.
    /// </summary>
    public static bool BeginRestartIntoUpdate() => HandOver(restart: true);

    /// <summary>
    /// At exit: an update that was downloaded (by the Update button, or in the background when Settings say so) and not
    /// restarted into is put in quietly, ready for the next start. One only found is left alone.
    /// </summary>
    public static void ApplyOnExit() => HandOver(restart: false);

    private static bool HandOver(bool restart)
    {
        if (_manager is null || _ready is null || _handedOver)
            return false;

        try
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: !restart, restart: restart);
            _handedOver = true;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("The update could not be handed to the updater", ex);
            return false;
        }
    }
}
