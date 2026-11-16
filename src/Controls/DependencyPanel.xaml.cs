using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>Dependency list row: a tool or a speech model.</summary>
public sealed partial class DependencyRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsModel { get; init; }

    /// <summary>Automation name for the row's status text.</summary>
    public string StatusLabel => $"{Name} Status";

    public DependencyState State { get; set; }

    /// <summary>Latest GitHub release asset, once queried.</summary>
    public RemoteAsset? Latest { get; set; }

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isActive;

    // Models only: selected for download; selection disabled once installed.
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _canSelect = true;
}

/// <summary>
/// Lists every downloadable dependency with its installed state, plus one button that installs missing and updates outdated ones.
/// Hosted by the first-run window and Settings.
///
/// Lightweight: versions come from small install-time stamp files (or a brief tool invocation), and availability from one small
/// GitHub request per tool.
/// </summary>
public partial class DependencyPanel : UserControl
{
    /// <summary>Install button label (the first-run window overrides it).</summary>
    public string InstallLabel
    {
        get => _installLabel;
        set => InstallButton.Content = _installLabel = value;
    }

    private string _installLabel = "Install / Update All Dependencies";

    /// <summary>Label when nothing needs downloading; null keeps <see cref="InstallLabel"/>.</summary>
    public string? ReadyLabel { get; set; }

    /// <summary>Label of the use-existing-FFmpeg button.</summary>
    public string UseExistingLabel
    {
        get => UseExistingButton.Content as string ?? "";
        set => UseExistingButton.Content = value;
    }

    private readonly ObservableCollection<DependencyRow> _tools = [];
    private readonly ObservableCollection<DependencyRow> _models = [];

    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _refreshCancellation;
    private bool _isRunning;
    private bool _appIsBusy;

    public DependencyPanel()
    {
        InitializeComponent();

        foreach (var tool in DependencyUpdater.Tools)
            _tools.Add(new DependencyRow { Id = tool, Name = DependencyUpdater.DisplayName(tool) });
        // The VAD model is tiny and improves captions, so it is preselected.
        foreach (var model in DependencyUpdater.WhisperModels.Append(DependencyUpdater.VadModel))
        {
            var isVad = model == DependencyUpdater.VadModel;
            var row = new DependencyRow { Id = model, Name = isVad ? "Silence detection (VAD)" : model, IsModel = true, IsSelected = isVad };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DependencyRow.IsSelected))
                    UpdateSummary();
            };
            _models.Add(row);
        }

        ToolList.ItemsSource = _tools;
        ModelList.ItemsSource = _models;

        Loaded += (_, _) =>
        {
            _ = RefreshAsync();

            // Cancel on window close (not on Unloaded): a running download would otherwise keep swapping tools after its host closed.
            // Tab switches unload the panel while an update started elsewhere (General tab's Update Dependencies) must continue.
            if (!_watchingWindow && Window.GetWindow(this) is { } window)
            {
                _watchingWindow = true;
                window.Closed += (_, _) => CancelAll();
            }
        };
        OverallBar.ValueChanged += (_, e) => ProgressChanged?.Invoke(e.NewValue);
    }

    private bool _watchingWindow;

    /// <summary>Cancels any running download and check (for hosts closing before the panel ever loaded).</summary>
    public void CancelAll()
    {
        _cancellation?.Cancel();
        _refreshCancellation?.Cancel();
    }

    /// <summary>Raised with the panel's status message and an error flag, for hosts that display it elsewhere.</summary>
    public event Action<string, bool>? MessageShown;

    /// <summary>Raised with install progress 0..1.</summary>
    public event Action<double>? ProgressChanged;

    // True after the first disk + network refresh.
    private bool _refreshed;

    /// <summary>Raised when an install pass changed installed dependencies.</summary>
    public event Action? Changed;

    /// <summary>Raised to use an existing FFmpeg; the host decides how the path is stored.</summary>
    public event Action? UseExistingFfmpegRequested;

    /// <summary>Raised when an install pass starts or ends.</summary>
    public event Action? RunningChanged;

    /// <summary>True during an install pass.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Shows the use-existing-FFmpeg button.</summary>
    public bool OffersExistingFfmpeg
    {
        get => UseExistingButton.Visibility == Visibility.Visible;
        set => UseExistingButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// True during an encode, preview render or transcription; installs are blocked because swapping a tool under a running job would
    /// break it.
    /// </summary>
    public bool AppIsBusy
    {
        get => _appIsBusy;
        set
        {
            _appIsBusy = value;
            UpdateButtons();
        }
    }

    // ----- Tiers: a selected payload set tracked by one progress bar -----

    /// <summary>Raised after the list is refreshed from disk and network.</summary>
    public event Action? Refreshed;

    // Tools outside the selected set (skipped by installs).
    private readonly HashSet<string> _excludedTools = [];

    /// <summary>
    /// Compact mode: a single progress bar and status line without per-tool/model rows (first-run window, where a tier is chosen).
    /// </summary>
    public bool IsUnified
    {
        get => ToolList.Visibility != Visibility.Visible;
        set => ToolList.Visibility = ModelList.Visibility = ModelsTitle.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Restricts Install to exactly these tools and models.</summary>
    public void SelectPayloads(IEnumerable<string> tools, IEnumerable<string> models)
    {
        var (wantedTools, wantedModels) = (tools.ToHashSet(), models.ToHashSet());
        _excludedTools.Clear();
        _excludedTools.UnionWith(_tools.Select(t => t.Id).Where(id => !wantedTools.Contains(id)));
        foreach (var row in _models)
            row.IsSelected = row.CanSelect && wantedModels.Contains(row.Id);
        UpdateSummary();
    }

    /// <summary>
    /// Overall progress weighted by bytes across all payloads, so a 1.4 GB model is not one equal step beside a 20 MB tool.
    /// </summary>
    private sealed class OverallProgress(ProgressBar bar) : IProgress<double>
    {
        public void Report(double value) => bar.Value = Math.Clamp(value, 0, 1);
    }

    private IProgress<double> Overall => _overall ??= new OverallProgress(OverallBar);

    private IProgress<double>? _overall;

    /// <summary>Preselects the caption settings' model if not yet downloaded.</summary>
    public void PreferModel(string model)
    {
        foreach (var row in _models.Where(m => m.Id != DependencyUpdater.VadModel))
            row.IsSelected = row.CanSelect && row.Id == model;
        UpdateSummary();
    }

    // ----- Installed state -----

    /// <summary>
    /// Populates the list from disk (immediate), then installed versions and GitHub availability. Offline, installed state still
    /// shows.
    /// </summary>
    public async Task RefreshAsync()
    {
        _refreshed = true;
        _refreshCancellation?.Cancel();
        var cancellation = _refreshCancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        foreach (var row in _tools)
        {
            row.State = DependencyUpdater.GetLocalState(row.Id);
            row.Status = Describe(row, version: "");
        }

        foreach (var row in _models)
        {
            var file = new FileInfo(DependencyUpdater.GetWhisperModelPath(row.Id));
            row.State = file.Exists ? DependencyState.Installed : DependencyState.Missing;
            row.CanSelect = !file.Exists;
            if (file.Exists)
                row.IsSelected = false;
            row.Status = file.Exists ? $"Installed ({Megabytes(file.Length)})" : $"{Megabytes(DependencyUpdater.ApproximateModelBytes(row.Id))} download";
            if (row.Id == DependencyUpdater.VadModel)
                row.Status = file.Exists ? "Installed" : "Under 1 MB";
        }

        if (!_models.Any(m => m.IsSelected && m.Id != DependencyUpdater.VadModel) && _models.FirstOrDefault(m => m.Id == AppSettings.Current.WhisperModel) is { CanSelect: true } preferred)
            preferred.IsSelected = true;
        UpdateSummary();
        UpdateButtons();

        string? problem = null;
        try
        {
            foreach (var row in _tools.Where(r => r.State != DependencyState.CustomPath))
            {
                var version = row.State == DependencyState.Installed
                    ? await Task.Run(() => DependencyUpdater.GetInstalledVersionAsync(row.Id, token), token)
                    : "";
                row.Status = Describe(row, version);

                try
                {
                    row.Latest = await DependencyUpdater.GetLatestAsync(row.Id, token);
                    if (row.State == DependencyState.Installed && DependencyUpdater.IsOutdated(row.Id, row.Latest))
                        row.State = DependencyState.UpdateAvailable;
                    row.Status = Describe(row, version);
                }
                catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex))
                {
                    AppLog.Write($"Dependencies: {row.Id} could not be checked", ex);
                    problem ??= ex.Message;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
            return;

        UpdateSummary();
        if (problem is not null && !_isRunning)
        {
            InstallButton.Content = "Retry";
            if (_appIsBusy)
                return;

            ShowMessage($"Could not check for newer versions ({problem}). Check the connection and press Retry. What is installed keeps working"
                        + (OffersExistingFfmpeg ? ", and an FFmpeg you already have can be used instead." : "."), isProblem: true);
            InstallButton.Content = "Retry";
        }

        Refreshed?.Invoke();
    }

    /// <summary>
    /// Row status text: installed version and, for pinned tools, whether it is the tested build and what Install / Update would install
    /// (the pinned build or a labelled stand-in).
    /// </summary>
    private static string Describe(DependencyRow row, string version)
    {
        if (row.State == DependencyState.CustomPath)
            return $"Using custom path: {DependencyUpdater.CustomPath(row.Id)}";

        var size = row.Latest is { Size: > 0 } latest ? $" ({Megabytes(latest.Size)} download)" : "";
        var installed = version.Length > 0 ? $" ({version})" : "";
        if (DependencyUpdater.TestedBuild(row.Id) is not { } tested)
        {
            // Unpinned: yt-dlp always; others when untested updates are allowed.
            var newest = row.Id == DependencyUpdater.YtDlp ? "" : ", following the newest releases (untested)";
            return row.State switch
            {
                DependencyState.Installed => $"Installed{installed}{newest}",
                DependencyState.UpdateAvailable => $"Update available{size}: installed{installed.Replace("(", "").Replace(")", "")}{newest}",
                _ => $"Missing{size}{newest}",
            };
        }

        var coming = row.Latest is { Pin: PinState.StandIn } standIn
            ? $"{standIn.Label}, a stand-in ({standIn.Note})"
            : $"the pinned {tested.Label}";
        return row.State switch
        {
            DependencyState.Installed => DependencyUpdater.InstalledPinRank(row.Id, row.Latest?.Variant ?? tested.Variant) switch
            {
                0 => $"Installed{installed}: the pinned build",
                null => $"Installed{installed}: not the pinned build ({tested.Label})",
                _ => $"Installed{installed}: a stand-in for the pinned {tested.Label}, which is no longer published",
            },
            DependencyState.UpdateAvailable => $"Not the pinned build: installed{installed.Replace("(", "").Replace(")", "")}. Install / Update puts in {coming}{size}",
            _ => $"Missing{size}: installs {coming}",
        };
    }

    private static string Megabytes(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824.0:0.0} GB" : $"{bytes / 1048576.0:0} MB";

    /// <summary>Updates the pending-download summary and total size.</summary>
    private void UpdateSummary()
    {
        if (_isRunning)
            return;

        var parts = new List<string>();
        long total = 0;
        // Tools excluded by SelectPayloads are not downloaded or counted.
        foreach (var row in _tools.Where(r => r.State is DependencyState.Missing or DependencyState.UpdateAvailable && !_excludedTools.Contains(r.Id)))
        {
            parts.Add(row.Latest is { Size: > 0 } latest ? $"{row.Name} ({Megabytes(latest.Size)})" : row.Name);
            total += row.Latest?.Size ?? 0;
        }

        foreach (var row in _models.Where(m => m.IsSelected && m.CanSelect))
        {
            var bytes = DependencyUpdater.ApproximateModelBytes(row.Id);
            parts.Add($"{row.Name} ({Megabytes(bytes)})");
            total += bytes;
        }

        InstallButton.Content = parts.Count == 0 && ReadyLabel is { } ready ? ready : InstallLabel;
        SummaryText.Text = parts.Count == 0
            ? "Everything is installed and up to date."
            : _tools.Any(r => r.State is DependencyState.Missing or DependencyState.UpdateAvailable && r.Latest is null && !_excludedTools.Contains(r.Id))
                ? $"To download: {string.Join(", ", parts)}. The sizes of the tools show once GitHub has been reached. Kept in {AppPaths.Deps}"
                : $"To download: {string.Join(", ", parts)}. About {Megabytes(total)} in all, kept in {AppPaths.Deps}";
    }

    private void UpdateButtons()
    {
        InstallButton.IsEnabled = ForceLatestButton.IsEnabled = RestorePinnedButton.IsEnabled = !_isRunning && !_appIsBusy;
        CancelButton.IsEnabled = _isRunning;
        UseExistingButton.IsEnabled = !_isRunning;
        if (_appIsBusy && !_isRunning)
            ShowMessage("An encode, preview or transcription is running. Dependencies can be installed or updated once it has finished.", isProblem: false);
        else if (!_appIsBusy && MessageText.Text.StartsWith("An encode, preview", StringComparison.Ordinal))
            MessageText.Text = "";
    }

    private void ShowMessage(string text, bool isProblem)
    {
        MessageText.Text = text;
        MessageText.SetResourceReference(ForegroundProperty, isProblem ? "WarningTextBrush" : "MutedTextBrush");
        MessageShown?.Invoke(text, isProblem);
    }

    // ----- Install -----

    private void Install_Click(object sender, RoutedEventArgs e) => Faults.Forget(Install_ClickAsync(sender, e), nameof(Install_Click));

    private async Task Install_ClickAsync(object sender, RoutedEventArgs e) => await InstallAsync();

    /// <summary>
    /// Install / Update All Dependencies: installs missing tools and replaces non-target builds (pinned, or newest when unpinned) plus
    /// selected models; custom-path tools are skipped. Reports via the message line and <see cref="MessageShown"/>; no-op while busy.
    /// </summary>
    /// <param name="installedToolsOnly">Replace installed tools only (no new tools or models), for switching pinned/newest builds.</param>
    public async Task InstallAsync(bool installedToolsOnly = false)
    {
        if (_isRunning)
            return;

        if (_appIsBusy)
        {
            ShowMessage("An encode, preview or transcription is running. Dependencies can be installed or updated once it has finished.", isProblem: false);
            return;
        }

        // Invoked before the panel ever loaded: refresh installed state first.
        if (!_refreshed)
        {
            ShowMessage("Checking what is installed...", isProblem: false);
            await RefreshAsync();
        }

        _refreshCancellation?.Cancel();
        _isRunning = true;
        RunningChanged?.Invoke();
        InstallButton.Content = InstallLabel;
        OverallBar.Value = 0;
        ShowMessage("Checking what is available...", isProblem: false);
        UpdateButtons();

        var cancellation = _cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var (installed, failed, cancelled) = (new List<string>(), new List<string>(), false);
        try
        {
            // Build the work list, re-querying each tool so a check that failed during refresh (offline) is retried.
            var work = new List<DependencyRow>();
            foreach (var row in _tools.Where(r => !_excludedTools.Contains(r.Id) && DependencyUpdater.GetLocalState(r.Id) is var state
                                                  && (installedToolsOnly ? state == DependencyState.Installed : state != DependencyState.CustomPath)))
            {
                try
                {
                    row.Latest = await DependencyUpdater.GetLatestAsync(row.Id, token);
                    if (DependencyUpdater.GetLocalState(row.Id) == DependencyState.Missing || DependencyUpdater.IsOutdated(row.Id, row.Latest))
                        work.Add(row);
                }
                catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex))
                {
                    AppLog.Write($"Dependencies: {row.Id} could not be checked", ex);
                    row.Status = $"Could not be checked: {ex.Message}";
                    failed.Add(row.Name);
                }
            }

            if (!installedToolsOnly)
                work.AddRange(_models.Where(m => m.IsSelected && m.CanSelect));

            // Per-payload byte weights (min 1 MB) so the overall bar tracks bytes, not item count.
            var sizes = work.Select(r => (double)Math.Max(r.IsModel ? DependencyUpdater.ApproximateModelBytes(r.Id) : r.Latest?.Size ?? 0, 1048576)).ToList();
            var (total, arrived) = (sizes.Sum(), 0.0);

            for (var index = 0; index < work.Count; index++)
            {
                var row = work[index];
                var (size, before) = (sizes[index], arrived);
                var progress = new Progress<InstallProgress>(p =>
                {
                    row.IsIndeterminate = p.Fraction < 0;
                    row.Progress = Math.Max(0, p.Fraction);
                    row.Status = p.Text;
                    Overall.Report((before + Math.Clamp(p.Fraction, 0, 1) * size) / total);
                    if (IsUnified)
                        ShowMessage($"{row.Name}: {p.Text}", isProblem: false);
                });

                (row.IsActive, row.IsIndeterminate, row.Progress) = (true, true, 0);
                ShowMessage($"Installing {row.Name} ({index + 1} of {work.Count})...", isProblem: false);
                try
                {
                    CrashReporter.Note("deps", "installing " + row.Id);

                    // Thread pool: downloads are hashed as they stream, which must not run on the UI thread.
                    if (row.IsModel)
                        await Task.Run(() => DependencyUpdater.InstallModelAsync(row.Id, progress, token), token);
                    else
                        await Task.Run(() => DependencyUpdater.InstallToolAsync(row.Id, row.Latest!, progress, token), token);
                    installed.Add(row.Name);
                    CrashReporter.Note("deps", "installed " + row.Id);
                }
                catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex))
                {
                    CrashReporter.Note("deps", $"{row.Id} failed: {ex.GetType().Name}");
                    AppLog.Write($"Installing {row.Name} failed", ex);
                    row.Status = $"Failed: {ex.Message}";
                    failed.Add(row.Name);
                }
                finally
                {
                    row.IsActive = false;
                }

                arrived += size;
                Overall.Report(arrived / total);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            _isRunning = false;
            if (ReferenceEquals(_cancellation, cancellation))
                _cancellation = null;
            cancellation.Dispose();
            foreach (var row in _tools.Concat(_models))
                row.IsActive = false;
        }

        // Failed rows keep their error text; others refresh from disk.
        var failedStatuses = _tools.Concat(_models).Where(r => failed.Contains(r.Name)).ToDictionary(r => r, r => r.Status);
        await RefreshAsync();
        foreach (var (row, status) in failedStatuses)
            row.Status = status;

        var summary = installed.Count > 0 ? $"Installed or updated: {string.Join(", ", installed)}. " : "";
        if (cancelled)
        {
            ShowMessage(summary + "Cancelled. Whatever was not finished was left exactly as it was.", isProblem: false);
        }
        else if (failed.Count > 0)
        {
            ShowMessage(summary + $"Not done: {string.Join(", ", failed)} (see the list above). Nothing that was working has been changed. "
                        + "Check the connection and press Retry" + (OffersExistingFfmpeg ? ", or use an FFmpeg you already have." : "."), isProblem: true);
            InstallButton.Content = "Retry";
        }
        else
        {
            OverallBar.Value = installed.Count > 0 ? 1 : 0;
            ShowMessage(installed.Count > 0 ? summary.TrimEnd() : "Everything was already installed and up to date.", isProblem: false);
        }

        UpdateButtons();
        RunningChanged?.Invoke();
        if (installed.Count > 0)
            Changed?.Invoke();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void UseExisting_Click(object sender, RoutedEventArgs e) => UseExistingFfmpegRequested?.Invoke();

    // ----- Pinned vs newest builds (Settings) -----
    // FFmpeg and whisper.cpp install from the builds this version was tested with (DependencyUpdater.PinnedBuilds). Force Update
    // (after a warning) installs and keeps following newest upstream; Restore Pinned reverts to tested builds. Both persist
    // AllowUnpinnedDependencyUpdates immediately and raise UnpinnedChanged for hosts editing a settings copy.

    /// <summary>Shows the pinned/newest build buttons (Settings only, not the first-run window).</summary>
    public bool OffersPinnedBuilds
    {
        get => PinRow.Visibility == Visibility.Visible;
        set => PinRow.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Raised with the new <see cref="AppSettings.AllowUnpinnedDependencyUpdates"/> value after a button here changes it.</summary>
    public event Action<bool>? UnpinnedChanged;

    /// <summary>
    /// Untested-builds warning shared with the Settings check box; returns true on confirm. Cancel is the default button.
    /// </summary>
    /// <param name="installNow">True for Force Update (install now); false only enables later updates.</param>
    public static bool ConfirmUnpinned(Window? owner, bool installNow)
    {
        var message = "HandPeg installs the builds of FFmpeg and whisper.cpp that this version was tested with. "
                      + "The newest upstream builds are untested with it: a new FFmpeg can change filter syntax, so that exports "
                      + "fail, and a new whisper.cpp can bring caption instability.\n\n"
                      + (installNow
                          ? "The newest builds are installed now, and every update after that installs the newest again."
                          : "From now on, Install / Update and the background updates install the newest builds.")
                      + "\n\nTo go back at any time: Settings, Tools & Backup, Restore Pinned Recommended Versions turns this off and puts the "
                      + "tested builds back in one click. yt-dlp follows its newest release either way.";
        return new ConfirmDialog("Untested Dependency Builds", message, installNow ? "Install the Newest Builds" : "Allow Untested Builds")
        {
            Owner = owner,
            DefaultsToCancel = true,
        }.ShowDialog() == true;
    }

    private void ForceLatest_Click(object sender, RoutedEventArgs e) => Faults.Forget(ForceLatest_ClickAsync(sender, e), nameof(ForceLatest_Click));

    private async Task ForceLatest_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (_isRunning || ShowBusy() || !ConfirmUnpinned(Window.GetWindow(this), installNow: true))
            return;

        SetUnpinned(true);
        await InstallAsync(installedToolsOnly: true);
    }

    private void RestorePinned_Click(object sender, RoutedEventArgs e) => Faults.Forget(RestorePinned_ClickAsync(sender, e), nameof(RestorePinned_Click));

    private async Task RestorePinned_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (_isRunning || ShowBusy())
            return;

        SetUnpinned(false);
        await InstallAsync(installedToolsOnly: true);
    }

    // Blocks pin changes while tools are in use, with a message; the setting is untouched.
    private bool ShowBusy()
    {
        if (_appIsBusy)
            ShowMessage("An encode, preview or transcription is running. Dependencies can be installed or updated once it has finished.", isProblem: false);
        return _appIsBusy;
    }

    /// <summary>Saves the setting and clears cached GitHub answers, since the target builds changed.</summary>
    private void SetUnpinned(bool allowed)
    {
        if (AppSettings.Current.AllowUnpinnedDependencyUpdates != allowed)
        {
            try
            {
                AppSettings.Update(settings => settings.AllowUnpinnedDependencyUpdates = allowed, saveNow: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In-memory choice still applies for this session.
                AppLog.Write("The dependency setting could not be saved", ex);
            }

            AppLog.Write(allowed
                ? "Untested dependency updates allowed: the newest FFmpeg and whisper.cpp are installed from now on."
                : "Dependencies pinned again: FFmpeg and whisper.cpp go back to the builds this version was tested with.");
        }

        DependencyUpdater.ForgetAnswers();
        UnpinnedChanged?.Invoke(allowed);
    }
}
