using System.IO;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>What something that was just pressed cannot do without, and has not got.</summary>
/// <param name="Purpose">What was pressed, as the prompt names it: "Generate Captions".</param>
/// <param name="Tools">The missing tools, by the names <see cref="DependencyUpdater"/> knows them by.</param>
/// <param name="Models">The missing speech models, by file name.</param>
public sealed record DependencyRequest(string Purpose, IReadOnlyList<string> Tools, IReadOnlyList<string> Models);

// Dependencies fetched at the moment they are first needed. The first-run window installs only the essentials
// unless it is told otherwise, so a tool that needs more than that asks here, before it starts: the window
// offers the download, fetches just what is missing, and the tool then goes on as if it had always been there.
public partial class MainViewModel
{
    /// <summary>
    /// Set by the window: shows the download prompt for what is missing, and says whether it is all there
    /// afterwards. Not set in a background instance, which never asks.
    /// </summary>
    public Func<DependencyRequest, Task<bool>>? DependenciesNeeded { get; set; }

    /// <summary>
    /// Call before starting something that needs these. True when they are all there, at once or after the
    /// user had them downloaded; false when the user declined, with the reason in the status bar.
    /// </summary>
    private async Task<bool> RequireAsync(string purpose, IEnumerable<string> tools, IEnumerable<string> models)
    {
        // A path of the user's own is theirs to keep right: only what HandPeg would download itself is asked about.
        var missingTools = tools.Where(t => DependencyUpdater.GetLocalState(t) == DependencyState.Missing).ToList();
        var missingModels = models.Where(m => m.Length > 0 && !File.Exists(DependencyUpdater.GetWhisperModelPath(m))).ToList();
        if (missingTools.Count + missingModels.Count == 0)
            return true;

        // Nobody to ask: what was pressed says for itself what it lacks, as it did before there was a prompt.
        if (_isBackgroundWorker || DependenciesNeeded is null)
            return true;

        // Nothing is installed while an encode, a preview or a transcription is using the tools.
        if (IsBusy)
        {
            StatusText = $"{purpose} needs a download first, and that has to wait for the running operation to finish.";
            return false;
        }

        var ready = await DependenciesNeeded(new DependencyRequest(purpose, missingTools, missingModels));
        OnDependenciesChanged();
        if (!ready)
            StatusText = $"{purpose} was not started: what it needs was not downloaded.";
        return ready;
    }

    // ----- Keeping the tools up to date by themselves (Settings: Auto-Update Dependencies) -----
    // A while after startup, as Idle work (it waits while playing or exporting), every tool HandPeg installed itself
    // is compared with its newest release and replaced when there is a newer one, with its progress on the chip. A
    // tool of the user's own (a path in the settings) and a tool never installed are left alone. A tool in use cannot
    // be replaced: nothing about it changes, and it is tried again at the next start.

    /// <summary>How long after startup the tools are looked at: the window, the project and its pictures come first.</summary>
    private static readonly TimeSpan AutoUpdateDelay = TimeSpan.FromSeconds(30);

    private async Task AutoUpdateDependenciesLaterAsync()
    {
        if (_isBackgroundWorker || !AppSettings.Current.AutoUpdateDependencies)
            return;

        try
        {
            await Task.Delay(AutoUpdateDelay, _shutdown.Token);
            if (!AppSettings.Current.AutoUpdateDependencies)
                return;

            var updated = await WorkScheduler.Run(new WorkSpec("Updating dependencies", WorkLane.Idle) { Key = "auto-update-dependencies" }, UpdateInstalledToolsAsync, _shutdown.Token);
            if (updated.Count > 0)
            {
                StatusText = $"Updated in the background: {string.Join(", ", updated)}.";
                OnDependenciesChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Replaces every tool HandPeg installed that has a newer release; returns the names of those replaced.</summary>
    private static async Task<List<string>> UpdateInstalledToolsAsync(WorkContext work)
    {
        var due = new List<(string Tool, RemoteAsset Latest)>();
        work.Report(-1, "Looking for newer releases");
        var tools = DependencyUpdater.Tools.Where(t => DependencyUpdater.GetLocalState(t) is DependencyState.Installed or DependencyState.UpdateAvailable).ToList();
        foreach (var tool in tools)
        {
            try
            {
                var latest = await DependencyUpdater.GetLatestAsync(tool, work.Token);
                if (DependencyUpdater.IsOutdated(tool, latest))
                    due.Add((tool, latest));
            }
            catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex))
            {
                AppLog.Write($"Auto-update: {DependencyUpdater.DisplayName(tool)} could not be checked: {ex.Message}");
            }
        }

        AppLog.Write($"Auto-update: {tools.Count} tool(s) checked ({string.Join(", ", tools.Select(DependencyUpdater.DisplayName))}); "
            + (due.Count == 0 ? "all up to date." : $"newer: {string.Join(", ", due.Select(d => $"{DependencyUpdater.DisplayName(d.Tool)} {d.Latest.Tag}"))}."));
        var updated = new List<string>();
        for (var i = 0; i < due.Count; i++)
        {
            var (tool, latest) = due[i];
            var (name, step) = (DependencyUpdater.DisplayName(tool), i);
            var progress = new Progress<InstallProgress>(p => work.Report((step + Math.Clamp(p.Fraction, 0, 1)) / due.Count, $"{name}: {p.Text}"));
            try
            {
                await Task.Run(() => DependencyUpdater.InstallToolAsync(tool, latest, progress, work.Token), work.Token);
                updated.Add($"{name} {latest.Tag}");
            }
            catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex))
            {
                AppLog.Write($"Auto-update: {name} was not updated: {ex.Message}");
            }
        }

        return updated;
    }

    /// <summary>What transcribing takes: whisper.cpp, FFmpeg to cut the sound out, the speech model in use, and the silence model when that is switched on.</summary>
    private Task<bool> RequireCaptionToolsAsync(string purpose) => RequireAsync(purpose,
        [DependencyUpdater.Whisper, DependencyUpdater.Ffmpeg],
        AppSettings.Current.UseVad ? [AppSettings.Current.WhisperModel, DependencyUpdater.VadModel] : [AppSettings.Current.WhisperModel]);

    /// <summary>What running the command in the Command Preview box takes: FFmpeg, and the caption tools when it draws captions that still have to be heard.</summary>
    private async Task<bool> RequireForCommandAsync(string purpose)
    {
        var transcribes = CommandUsesCaptions(CommandPreview) && !HasTextCues;
        return transcribes ? await RequireCaptionToolsAsync(purpose) : await RequireAsync(purpose, [DependencyUpdater.Ffmpeg], []);
    }
}
