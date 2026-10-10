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
    private static UpdateManager? _manager;

    // The release that has been downloaded and is waiting to be put in.
    private static VelopackAsset? _ready;
    private static bool _handedOver;

    /// <summary>
    /// Looks for a newer release and downloads it, off the UI thread. Returns its version once it is ready
    /// to be applied, or null when there is nothing to do or the check could not be made.
    /// </summary>
    public static Task<string?> CheckAndDownloadAsync() => Task.Run(async () =>
    {
        // A self-contained copy is replaced by hand: the updater keeps its packages outside the program's folder.
        if (AppPaths.IsSelfContained)
            return (string?)null;

        try
        {
            var manager = new UpdateManager(new GithubSource(AppSettings.HandPegRepositoryUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
                return null;

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
                return null;

            await manager.DownloadUpdatesAsync(update);
            (_manager, _ready) = (manager, update.TargetFullRelease);
            AppLog.Write($"Update {update.TargetFullRelease.Version} downloaded.");
            return update.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Update check failed", ex);
            return (string?)null;
        }
    });

    /// <summary>
    /// Hands the downloaded release to the updater, which waits for this process to end, puts the new
    /// version in and starts it. The caller then closes the application in the ordinary way.
    /// </summary>
    public static bool BeginRestartIntoUpdate() => HandOver(restart: true);

    /// <summary>At exit: an update the user did not restart into is put in quietly, ready for the next start.</summary>
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
