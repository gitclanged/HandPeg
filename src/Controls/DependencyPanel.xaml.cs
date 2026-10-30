using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>One line of the dependency list: a tool, or a speech model.</summary>
public sealed partial class DependencyRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsModel { get; init; }

    /// <summary>The automation id of this row's status text.</summary>
    public string StatusLabel => $"{Name} Status";

    public DependencyState State { get; set; }

    /// <summary>The newest build GitHub has, once it has been asked.</summary>
    public RemoteAsset? Latest { get; set; }

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isActive;

    // Models only: ticked to be downloaded, and not offered once it is there.
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _canSelect = true;
}

/// <summary>
/// The list of everything HandPeg downloads, with what is installed, and the one button that installs what is
/// missing and updates what is outdated. Shown in the first-run window and in the settings.
///
/// It holds nothing heavy: versions come from small files written at install time (or from running a tool
/// once for a moment), and what is available comes from one small request to GitHub per tool.
/// </summary>
public partial class DependencyPanel : UserControl
{
    /// <summary>What the install button says (the first-run window words it for a new user).</summary>
    public string InstallLabel
    {
        get => _installLabel;
        set => InstallButton.Content = _installLabel = value;
    }

    private string _installLabel = "Install / Update All Dependencies";

    /// <summary>What it says instead when there is nothing to download; null to keep <see cref="InstallLabel"/>.</summary>
    public string? ReadyLabel { get; set; }

    /// <summary>What the button for using an FFmpeg already on the machine says.</summary>
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
        // The silence detection model is tiny and makes captions better, so it starts out ticked.
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

            // Its window closing stops a download: left running, it would go on swapping tools in behind the main
            // window. Not merely being hidden: a tab switched away from unloads the panel, and an update started
            // from elsewhere in the window (the General tab's Update Dependencies) carries on meanwhile.
            if (!_watchingWindow && Window.GetWindow(this) is { } window)
            {
                _watchingWindow = true;
                window.Closed += (_, _) => CancelAll();
            }
        };
        OverallBar.ValueChanged += (_, e) => ProgressChanged?.Invoke(e.NewValue);
    }

    private bool _watchingWindow;

    /// <summary>Stops a download and a check that are running. For a host whose window closes before the panel was ever shown.</summary>
    public void CancelAll()
    {
        _cancellation?.Cancel();
        _refreshCancellation?.Cancel();
    }

    /// <summary>Raised with what the panel says under its list (and whether it is a problem): for a host that shows it elsewhere.</summary>
    public event Action<string, bool>? MessageShown;

    /// <summary>Raised with how far an install pass has got, 0 to 1.</summary>
    public event Action<double>? ProgressChanged;

    // Whether the list has been read from the disk and the network at least once.
    private bool _refreshed;

    /// <summary>Raised when an install pass has changed what is installed.</summary>
    public event Action? Changed;

    /// <summary>Raised when the user asks to point HandPeg at an FFmpeg of their own; the host decides how that is stored.</summary>
    public event Action? UseExistingFfmpegRequested;

    /// <summary>Raised when an install pass starts or ends.</summary>
    public event Action? RunningChanged;

    /// <summary>True while tools are being downloaded and installed.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Whether the button for using an FFmpeg already on the machine is offered here.</summary>
    public bool OffersExistingFfmpeg
    {
        get => UseExistingButton.Visibility == Visibility.Visible;
        set => UseExistingButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// True while an encode, a preview render or a transcription is running. Nothing is installed meanwhile:
    /// the tools are in use, and swapping one out from under a running job is exactly what must not happen.
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

    // ----- Tiers: a chosen set of payloads, installed behind one progress bar -----

    /// <summary>Raised when the list has been read again from the disk and the network.</summary>
    public event Action? Refreshed;

    // The tools that are not part of the chosen set, and so are left alone.
    private readonly HashSet<string> _excludedTools = [];

    /// <summary>
    /// Shows the panel as one progress bar and a line of text, without the row for each tool and model: for
    /// the first-run window, where what is fetched is chosen as a tier.
    /// </summary>
    public bool IsUnified
    {
        get => ToolList.Visibility != Visibility.Visible;
        set => ToolList.Visibility = ModelList.Visibility = ModelsTitle.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Chooses what the Install button fetches: these tools and these models, and nothing else.</summary>
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
    /// The one progress bar: how much of everything being installed has arrived, counted in bytes across all
    /// of it, so that a 1.4 GB model is not one step of five beside a 20 MB program.
    /// </summary>
    private sealed class OverallProgress(ProgressBar bar) : IProgress<double>
    {
        public void Report(double value) => bar.Value = Math.Clamp(value, 0, 1);
    }

    private IProgress<double> Overall => _overall ??= new OverallProgress(OverallBar);

    private IProgress<double>? _overall;

    /// <summary>The model the caption settings name: ticked by default when it has not been downloaded yet.</summary>
    public void PreferModel(string model)
    {
        foreach (var row in _models.Where(m => m.Id != DependencyUpdater.VadModel))
            row.IsSelected = row.CanSelect && row.Id == model;
        UpdateSummary();
    }

    // ----- What is there -----

    /// <summary>
    /// Fills the list: first from the disk, which is immediate, then with the installed versions and with
    /// what GitHub has. Without a connection the list still says what is installed.
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
    /// What the row says: what is installed and, for a pinned tool, whether that is the build this version of HandPeg was
    /// tested with, and what Install / Update would put in (the pinned build, or a stand-in for it, named as one).
    /// </summary>
    private static string Describe(DependencyRow row, string version)
    {
        if (row.State == DependencyState.CustomPath)
            return $"Using custom path: {DependencyUpdater.CustomPath(row.Id)}";

        var size = row.Latest is { Size: > 0 } latest ? $" ({Megabytes(latest.Size)} download)" : "";
        var installed = version.Length > 0 ? $" ({version})" : "";
        if (DependencyUpdater.TestedBuild(row.Id) is not { } tested)
        {
            // Not pinned: yt-dlp always, the others when untested updates are allowed.
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

    /// <summary>What pressing the button would download, and how much that is.</summary>
    private void UpdateSummary()
    {
        if (_isRunning)
            return;

        var parts = new List<string>();
        long total = 0;
        // A tool the chosen set leaves out (SelectPayloads) is not downloaded, so it is not counted.
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

    // ----- Installing -----

    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallAsync();

    /// <summary>
    /// Install / Update All Dependencies: installs what is missing and replaces what is not the build to have (the pinned
    /// one, or the newest release where builds are not pinned), with the speech models that are ticked; a tool with a path
    /// of the user's own is left alone. What happens is said in the panel's message line (and through <see cref="MessageShown"/>).
    /// Nothing while an encode is running.
    /// </summary>
    /// <param name="installedToolsOnly">Only replace the tools that are installed, fetching nothing new and no models: for switching between pinned and newest builds.</param>
    public async Task InstallAsync(bool installedToolsOnly = false)
    {
        if (_isRunning)
            return;

        if (_appIsBusy)
        {
            ShowMessage("An encode, preview or transcription is running. Dependencies can be installed or updated once it has finished.", isProblem: false);
            return;
        }

        // Asked for before the panel was ever shown: what is there, and what each tool is, is read first.
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
            // What there is to do. A tool is asked about again here, so that a check that failed when the
            // list was filled (no connection at the time) is simply made again.
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
                    row.Status = $"Could not be checked: {ex.Message}";
                    failed.Add(row.Name);
                }
            }

            if (!installedToolsOnly)
                work.AddRange(_models.Where(m => m.IsSelected && m.CanSelect));

            // How much each payload weighs, so that the one bar counts bytes and not payloads.
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

                    // On the thread pool: the bytes are hashed as they arrive, which is not work for the UI thread.
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

        // Rows that failed keep saying why; the others are read again from what is now on disk.
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

    // ----- Pinned builds or the newest ones (the settings) -----
    // FFmpeg and whisper.cpp are installed from the builds this version of HandPeg was tested with (see
    // DependencyUpdater.PinnedBuilds). Force Update puts the newest upstream builds in their place, after a warning, and
    // keeps them coming; Restore Pinned turns that off again and puts the tested builds back. Both change the setting at
    // once (it is what every later update goes by), and say so through UnpinnedChanged to a host editing a copy of the settings.

    /// <summary>Whether the two buttons for choosing between pinned and newest builds are offered: in the settings, not the first-run window.</summary>
    public bool OffersPinnedBuilds
    {
        get => PinRow.Visibility == Visibility.Visible;
        set => PinRow.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Raised when a button here has changed <see cref="AppSettings.AllowUnpinnedDependencyUpdates"/>, with its new value.</summary>
    public event Action<bool>? UnpinnedChanged;

    /// <summary>
    /// The warning given before untested builds are allowed, here and by the settings' check box; true when the user went ahead.
    /// Cancel is its default button.
    /// </summary>
    /// <param name="installNow">Whether going ahead installs them at once (Force Update), or only lets later updates do so.</param>
    public static bool ConfirmUnpinned(Window? owner, bool installNow)
    {
        var message = "HandPeg installs the builds of FFmpeg and whisper.cpp that this version was tested with. "
                      + "The newest upstream builds are untested with it: a new FFmpeg can change filter syntax, so that exports or Render Preview "
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

    private async void ForceLatest_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || ShowBusy() || !ConfirmUnpinned(Window.GetWindow(this), installNow: true))
            return;

        SetUnpinned(true);
        await InstallAsync(installedToolsOnly: true);
    }

    private async void RestorePinned_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || ShowBusy())
            return;

        SetUnpinned(false);
        await InstallAsync(installedToolsOnly: true);
    }

    // Nothing is installed while the tools are in use: said, and the setting is left as it was.
    private bool ShowBusy()
    {
        if (_appIsBusy)
            ShowMessage("An encode, preview or transcription is running. Dependencies can be installed or updated once it has finished.", isProblem: false);
        return _appIsBusy;
    }

    /// <summary>Changes and saves the setting, and asks GitHub afresh: the builds wanted are other ones now.</summary>
    private void SetUnpinned(bool allowed)
    {
        var settings = AppSettings.Current;
        if (settings.AllowUnpinnedDependencyUpdates != allowed)
        {
            settings.AllowUnpinnedDependencyUpdates = allowed;
            try
            {
                settings.SaveAsCurrent();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The choice still holds for this session.
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
