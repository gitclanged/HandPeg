using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace HandPegApp.Services;

/// <summary>
/// Velopack self-update from the public GitHub releases. Only Velopack installs (or their portable build) self-update; build folders
/// skip it. Never shows messages; all failures (including offline) are logged.
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

        /// <summary>A self-installable newer release was found; the status bar Update button downloads it.</summary>
        Found,

        /// <summary>A newer release exists, but this copy cannot put it in by itself: it is fetched by hand.</summary>
        Available,

        /// <summary>The check could not be made.</summary>
        Failed,
    }

    /// <summary>Current version in release numbering (major.minor.patch).</summary>
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
    /// Settings "Check for Updates": checks now and reports. Installed copies get the release via the Update button (or immediately when
    /// background download is enabled); self-contained or build copies cannot self-replace and only learn whether GitHub has a newer one.
    /// </summary>
    /// <returns>Outcome plus the version found or the failure reason.</returns>
    public static Task<(CheckResult Result, string Detail)> CheckNowAsync() => Task.Run(async () =>
    {
        // Velopack bootstrap runs alongside window startup (Program.VelopackStarted) and completes before any use here.
        await Program.VelopackStarted;
        await Checking.WaitAsync();
        try
        {
            if (ReadyVersion is { } waiting)
                return (CheckResult.Ready, waiting);

            if (!AppPaths.IsSelfContained)
            {
                var manager = NewManager();
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

    /// <summary>
    /// Velopack channel for this build: "win-arm64" (releases.win-arm64.json) or "win" for x64 (releases.win.json, kept by an emulated x64
    /// copy on ARM64). Set explicitly so architectures never receive each other's releases.
    /// </summary>
    public static string Channel => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win";

    private static UpdateManager NewManager() =>
        new(new GithubSource(AppSettings.HandPegRepositoryUrl, accessToken: null, prerelease: false), new UpdateOptions { ExplicitChannel = Channel });

    // A newer release that has been found and not downloaded yet; and the one downloaded, waiting to be put in.
    private static UpdateInfo? _found;
    private static VelopackAsset? _ready;
    private static bool _handedOver;

    /// <summary>The newer release found and not downloaded yet (startup checks only look), or null.</summary>
    public static string? FoundVersion => _found is null || _ready is not null ? null : _found.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Startup update check off the UI thread (Settings: Check for App Updates on Startup). Downloads only with Auto-Download App Updates
    /// in Background (off by default); otherwise the Update button does, so restarts never apply unrequested updates. Returns version and
    /// download state, or null when none or the check failed.
    /// </summary>
    public static Task<(string Version, bool Downloaded)?> CheckAtStartupAsync() => Task.Run(async () =>
    {
        // A self-contained copy is replaced by hand: the updater keeps its packages outside the program's folder.
        if (AppPaths.IsSelfContained)
            return ((string, bool)?)null;

        // Velopack bootstrap runs alongside window startup (Program.VelopackStarted) and completes before any use here.
        await Program.VelopackStarted;
        await Checking.WaitAsync();
        try
        {
            // Already found, by a check asked for in the settings.
            if (ReadyVersion is { } waiting)
                return (waiting, true);

            var manager = NewManager();
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
    /// Downloads the found release (Update button), reporting 0..1. True when ready; applied on restart or exit (explicitly requested).
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
    /// Hands the download to the updater, which waits for exit, installs and relaunches; the caller then closes normally.
    /// </summary>
    public static bool BeginRestartIntoUpdate() => HandOver(restart: true);

    /// <summary>
    /// At exit, silently applies a downloaded (not merely found) update for the next start.
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
