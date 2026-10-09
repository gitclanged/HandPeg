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
    private const string InstallText = "Install / Update All Dependencies";

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

        Loaded += (_, _) => _ = RefreshAsync();

        // Its window is closing: a download left running would go on swapping tools in behind the main window.
        Unloaded += (_, _) =>
        {
            _cancellation?.Cancel();
            _refreshCancellation?.Cancel();
        };
    }

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

    private static string Describe(DependencyRow row, string version)
    {
        var size = row.Latest is { Size: > 0 } latest ? $" ({Megabytes(latest.Size)} download)" : "";
        var installed = version.Length > 0 ? $" ({version})" : "";
        return row.State switch
        {
            DependencyState.CustomPath => $"Using custom path: {DependencyUpdater.CustomPath(row.Id)}",
            DependencyState.Installed => $"Installed{installed}",
            DependencyState.UpdateAvailable => $"Update available{size}: installed{installed.Replace("(", "").Replace(")", "")}",
            _ => $"Missing{size}",
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
        foreach (var row in _tools.Where(r => r.State is DependencyState.Missing or DependencyState.UpdateAvailable))
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

        SummaryText.Text = parts.Count == 0
            ? "Everything is installed and up to date."
            : _tools.Any(r => r.State is DependencyState.Missing or DependencyState.UpdateAvailable && r.Latest is null)
                ? $"To download: {string.Join(", ", parts)}. The sizes of the tools show once GitHub has been reached. Kept in {AppPaths.Deps}"
                : $"To download: {string.Join(", ", parts)}. About {Megabytes(total)} in all, kept in {AppPaths.Deps}";
    }

    private void UpdateButtons()
    {
        InstallButton.IsEnabled = !_isRunning && !_appIsBusy;
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
    }

    // ----- Installing -----

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _appIsBusy)
            return;

        _refreshCancellation?.Cancel();
        _isRunning = true;
        RunningChanged?.Invoke();
        InstallButton.Content = InstallText;
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
            foreach (var row in _tools.Where(r => !_excludedTools.Contains(r.Id) && DependencyUpdater.GetLocalState(r.Id) != DependencyState.CustomPath))
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
                    // On the thread pool: the bytes are hashed as they arrive, which is not work for the UI thread.
                    if (row.IsModel)
                        await Task.Run(() => DependencyUpdater.InstallModelAsync(row.Id, progress, token), token);
                    else
                        await Task.Run(() => DependencyUpdater.InstallToolAsync(row.Id, row.Latest!, progress, token), token);
                    installed.Add(row.Name);
                }
                catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex))
                {
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
}
