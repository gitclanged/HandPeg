using System.IO;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>Missing dependencies for an action that was just invoked.</summary>
/// <param name="Purpose">Action name shown in the prompt, e.g. "Generate Captions".</param>
/// <param name="Tools">Missing tools by <see cref="DependencyUpdater"/> name.</param>
/// <param name="Models">Missing speech models by file name.</param>
public sealed record DependencyRequest(string Purpose, IReadOnlyList<string> Tools, IReadOnlyList<string> Models);

// On-demand dependencies. First run installs only essentials by default, so features needing more ask here before starting: the
// window offers the download, fetches only what is missing, and the feature then proceeds normally.
public partial class MainViewModel
{
    /// <summary>Window-injected download prompt; returns whether everything is present afterwards. Null in background VMs (never prompt).</summary>
    public Func<DependencyRequest, Task<bool>>? DependenciesNeeded { get; set; }

    /// <summary>
    /// Pre-flight check before a dependent action: true when present (immediately or after download); false if declined (reason in
    /// status bar).
    /// </summary>
    private async Task<bool> RequireAsync(string purpose, IEnumerable<string> tools, IEnumerable<string> models)
    {
        // Custom tool paths are not managed; only HandPeg-installed dependencies are checked.
        var missingTools = tools.Where(t => DependencyUpdater.GetLocalState(t) == DependencyState.Missing).ToList();
        var missingModels = models.Where(m => m.Length > 0 && !File.Exists(DependencyUpdater.GetWhisperModelPath(m))).ToList();
        if (missingTools.Count + missingModels.Count == 0)
            return true;

        // No prompt available (headless): the action reports its own missing dependency.
        if (_isBackgroundWorker || DependenciesNeeded is null)
            return true;

        // Never install while an encode, preview or transcription holds the tools.
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

    // ----- Auto-Update Dependencies -----
    // Delayed Idle work (paused during playback/export): each HandPeg-installed tool is compared with its target build and replaced
    // if different, with chip progress. FFmpeg and whisper.cpp target tested pins (latest only when untested updates are allowed);
    // yt-dlp targets latest. Custom-path and never-installed tools are skipped. A tool in use is left unchanged and retried next start.

    /// <summary>Startup delay before the dependency check (window, project and thumbnails first).</summary>
    private static TimeSpan AutoUpdateDelay => TimeSpan.FromSeconds(30);

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

    /// <summary>Startup delay before sending pending crash reports (after window, project and thumbnails).</summary>
    private static TimeSpan CrashUploadDelay => TimeSpan.FromSeconds(15);

    /// <summary>
    /// Sends pending crash reports in the Idle lane (unlisted), only when enabled and present. Silent on network failure; retried next
    /// start.
    /// </summary>
    private async Task UploadCrashReportsLaterAsync()
    {
        if (_isBackgroundWorker || !AppSettings.Current.SendCrashReports)
            return;

        try
        {
            await Task.Delay(CrashUploadDelay, _shutdown.Token);
            var (folder, endpoint) = (AppPaths.Crashes, AppSettings.Current.CrashReportEndpoint);
            if (!AppSettings.Current.SendCrashReports || !CrashReports.IsUsableEndpoint(endpoint, out _)
                || !await Task.Run(() => CrashReports.Pending(folder).Count > 0, _shutdown.Token))
                return;

            var sent = await WorkScheduler.Run(new WorkSpec("Sending crash reports", WorkLane.Idle) { Key = "crash-upload", Visible = false },
                work => CrashReports.UploadPendingAsync(folder, endpoint, work.Token), _shutdown.Token);
            CrashReporter.Note("crash", $"{sent} report(s) sent");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Replaces HandPeg-installed tools not at their target build (pinned or latest); returns replaced names.</summary>
    private static async Task<List<string>> UpdateInstalledToolsAsync(WorkContext work)
    {
        var due = new List<(string Tool, RemoteAsset Latest)>();
        work.Report(-1, "Checking the installed builds");
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
                AppLog.Write($"Auto-update: {DependencyUpdater.DisplayName(tool)} could not be checked", ex);
            }
        }

        AppLog.Write($"Auto-update: {tools.Count} tool(s) checked ({string.Join(", ", tools.Select(DependencyUpdater.DisplayName))}); "
            + (due.Count == 0 ? "all as they should be." : $"to install: {string.Join(", ", due.Select(d => $"{DependencyUpdater.DisplayName(d.Tool)} {d.Latest.Label} ({d.Latest.Pin})"))}."));
        var updated = new List<string>();
        for (var i = 0; i < due.Count; i++)
        {
            var (tool, latest) = due[i];
            var (name, step) = (DependencyUpdater.DisplayName(tool), i);
            var progress = new Progress<InstallProgress>(p => work.Report((step + Math.Clamp(p.Fraction, 0, 1)) / due.Count, $"{name}: {p.Text}"));
            try
            {
                CrashReporter.Note("deps", $"auto-updating {tool} to {latest.Tag}");
                await Task.Run(() => DependencyUpdater.InstallToolAsync(tool, latest, progress, work.Token), work.Token);
                updated.Add(latest.Pin == PinState.StandIn ? $"{name} {latest.Label} (a stand-in: {latest.Note})" : $"{name} {latest.Label}");
                CrashReporter.Note("deps", "auto-updated " + tool);
            }
            catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex))
            {
                CrashReporter.Note("deps", $"auto-update of {tool} failed: {ex.GetType().Name}");
                AppLog.Write($"Auto-update: {name} was not updated", ex);
            }
        }

        return updated;
    }

    /// <summary>Transcription dependencies: whisper.cpp, FFmpeg (audio extraction), the active speech model, and the VAD model if enabled.</summary>
    private Task<bool> RequireCaptionToolsAsync(string purpose) => RequireAsync(purpose,
        [DependencyUpdater.Whisper, DependencyUpdater.Ffmpeg],
        AppSettings.Current.UseVad ? [AppSettings.Current.WhisperModel, DependencyUpdater.VadModel] : [AppSettings.Current.WhisperModel]);

    /// <summary>Command Preview run dependencies: FFmpeg, plus caption tools when untranscribed captions are burned in.</summary>
    private async Task<bool> RequireForCommandAsync(string purpose)
    {
        var transcribes = CommandUsesCaptions(CommandPreview) && !HasTextCues;
        return transcribes ? await RequireCaptionToolsAsync(purpose) : await RequireAsync(purpose, [DependencyUpdater.Ffmpeg], []);
    }
}
