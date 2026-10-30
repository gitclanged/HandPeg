using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Shown before the main window on a first start (and again from Settings): the theme, the workspace the window starts
/// laid out as, the required components and their download, the encoders HandPeg starts with (the hardware ones always
/// looked for before it closes), an optional hardware test that sets the GPU roles (5.0-0), what happens on closing and after a failed hardware encode, and whether crash reports
/// are written (with the Privacy Policy beside it). Closing it without pressing Start leaves the settings alone, and
/// it is shown again next time.
/// </summary>
public partial class FirstRunWindow : Window
{
    // The theme in effect when the window opened, put back if it is closed without confirming.
    private readonly string _originalTheme = AppSettings.Current.Theme;
    private bool _confirmed;

    // The roles the hardware test recommended: written into the settings only by Start (Finish), as every other choice here.
    private HardwareBenchmarkRoles? _benchmarkRoles;
    private bool _closed;

    // Whether the workspace was chosen here by hand: opened again from the settings, the window keeps the arrangement in use unless it was.
    private bool _workspaceChosen;
    private readonly bool _initialized;

    public FirstRunWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;

        ThemeBox.ItemsSource = ThemeManager.Themes;
        ThemeBox.SelectedItem = ThemeManager.Normalize(_originalTheme);
        FallbackBox.IsChecked = AppSettings.Current.AutoFallbackToSoftware;
        PromptBox.IsChecked = AppSettings.Current.PromptToSaveOnExit;
        CrashReportsBox.IsChecked = AppSettings.Current.SendCrashReports;

        // The audio settings an editor starts with: encoded, so that edits to the sound never meet a copied track.
        AudioEncoderBox.ItemsSource = AudioTrack.AllCodecs;
        AudioEncoderBox.SelectedItem = AudioTrack.AllCodecs.Contains(AppSettings.Current.DefaultAudioEncoder) ? AppSettings.Current.DefaultAudioEncoder : "aac";
        AudioBitrateBox.ItemsSource = AudioTrack.AllBitrates;
        AudioBitrateBox.SelectedItem = AudioTrack.AllBitrates.Contains(AppSettings.Current.DefaultAudioBitrate) ? AppSettings.Current.DefaultAudioBitrate : "192k";

        // The components in plain words: what the button fetches, and a folder already on the machine for those who have one.
        Dependencies.InstallLabel = "Download Required Components";
        Dependencies.ReadyLabel = "All required components are installed";
        Dependencies.UseExistingLabel = "Use Existing Local Folder...";
        Dependencies.UseExistingFfmpegRequested += UseExistingFfmpeg;

        // The encoders: what is always there at once, the hardware ones as soon as there is an FFmpeg to ask (now, when
        // it is here already). A component installed from this window may be the FFmpeg that makes the asking possible,
        // so then it is asked again, and Start waits for the asking to end (Start_Click).
        _wantedEncoder = AppSettings.Current.DefaultVideoEncoder;
        FillEncoders([]);
        _ = ProbeEncodersAsync();
        Dependencies.Changed += () => _ = ProbeEncodersAsync();

        // And whenever an install pass ends, however it ended: one that was cancelled or failed part of the way
        // may still have left an FFmpeg behind, and the encoders are asked of whichever FFmpeg is there now.
        Dependencies.RunningChanged += () =>
        {
            if (!Dependencies.IsRunning)
                _ = ProbeEncodersAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _probeCancellation.Cancel();
        };

        // Closing the window stops an install, so Start waits for it (or for Cancel).
        Dependencies.RunningChanged += () => StartButton.IsEnabled = !Dependencies.IsRunning && !_finishing;

        // Here the list is two cards and one progress bar; the rows behind them are not shown.
        Dependencies.IsUnified = true;
        Dependencies.Refreshed += ApplyTier;
        ApplyTier();

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closed += (_, _) =>
        {
            if (!_confirmed)
            {
                AppSettings.Current.Theme = _originalTheme;
                ThemeManager.Apply();
            }
        };
        _initialized = true;
    }

    // ----- The starting workspace -----

    private void Workspace_Checked(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            _workspaceChosen = true;
    }

    /// <summary>The built-in layout whose card is chosen, by the name the Layout button lists it under.</summary>
    private string ChosenWorkspace() =>
        new[] { StandardLayoutCard, ColorLayoutCard, AssemblyLayoutCard, ShortsLayoutCard }.FirstOrDefault(c => c.IsChecked == true)?.Tag as string
        ?? "Standard Editing";

    // ----- The required components: two packages -----

    // The speech models the captions package fetches; the second is the one captions are then set to use.
    private const string TierBaseModel = "ggml-base.en.bin";
    private const string TierDefaultModel = "ggml-medium.en.bin";

    private void Tier_Checked(object sender, RoutedEventArgs e) => ApplyTier();

    /// <summary>Tells the component list what the chosen package is made of.</summary>
    private void ApplyTier()
    {
        if (Dependencies is null || EditorTier is null)
            return;

        if (EditorTier.IsChecked == true)
            Dependencies.SelectPayloads(DependencyUpdater.Tools, [TierBaseModel, TierDefaultModel, DependencyUpdater.VadModel]);
        else
            Dependencies.SelectPayloads([DependencyUpdater.Ffmpeg, DependencyUpdater.YtDlp], []);
    }

    // ----- The default encoders -----

    // The best hardware encoder is the first of these that works here: NVENC, then QuickSync, then AMF, then Media
    // Foundation (the ARM64 path: Snapdragon / Adreno machines have none of the other three).
    private static readonly string[] PreferredHardware = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"];

    private readonly CancellationTokenSource _probeCancellation = new();

    // The encoder to select when the list is (re)filled: the one saved before, or blank for "the best there is".
    private string _wantedEncoder;
    private bool _fillingEncoders;

    private void EncoderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A choice made by hand is kept when the list is filled again.
        if (!_fillingEncoders && EncoderBox.SelectedItem is EncoderOption chosen)
            _wantedEncoder = chosen.Name;
        UpdateCopyWarning();
    }

    private void UpdateCopyWarning()
    {
        // Raised while the window is still being built.
        if (CopyWarningText is not null)
            CopyWarningText.Visibility = EncoderBox.SelectedItem is EncoderOption { Family: EncoderFamily.Copy } ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Hardware encoders that work here first, best vendor first; then the software ones; Copy last.</summary>
    private void FillEncoders(IReadOnlyList<string> hardware)
    {
        var rank = (string name) => name.EndsWith("_nvenc", StringComparison.Ordinal) ? 0 : name.EndsWith("_qsv", StringComparison.Ordinal) ? 1
            : MediaFoundationEncoder.Is(name) ? 3 : 2;
        var encoders = hardware.OrderBy(rank).Select(EncoderOption.FromHardwareName).Concat(EncoderOption.Software).Append(EncoderOption.Copy).ToList();

        var best = PreferredHardware.FirstOrDefault(hardware.Contains) ?? EncoderOption.Software[0].Name;
        var select = encoders.FirstOrDefault(e => e.Name == _wantedEncoder) ?? encoders.First(e => e.Name == best);

        _fillingEncoders = true;
        EncoderBox.ItemsSource = encoders;
        EncoderBox.SelectedItem = select;
        _fillingEncoders = false;
        UpdateCopyWarning();
    }

    // The FFmpeg the encoders were last asked of, and which asking is the latest: it is asked again only when
    // the FFmpeg is another one, and an answer that a later asking has overtaken is dropped.
    private string _probedFfmpeg = "";
    private int _probeRun;

    // The latest asking still running (or the last one, ended): what Start waits for.
    private Task _probeTask = Task.CompletedTask;

    /// <summary>Asks the FFmpeg that is here now for its hardware encoders, unless that one was asked already.</summary>
    private Task ProbeEncodersAsync()
    {
        var task = ProbeOnceAsync();
        if (!task.IsCompleted)
            _probeTask = task;
        return task;
    }

    private async Task ProbeOnceAsync()
    {
        var ffmpeg = DependencyUpdater.FfmpegPath;
        if (!File.Exists(ffmpeg))
        {
            (_probedFfmpeg, _probeRun) = ("", _probeRun + 1);
            FillEncoders([]);
            ProbeBar.Visibility = Visibility.Collapsed;
            EncoderStatusText.Text = "Hardware encoders are detected once the required components are downloaded.";
            return;
        }

        // The same file as last time (its path, size and date): what was found then still holds.
        var file = new FileInfo(ffmpeg);
        var identity = FormattableString.Invariant($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        if (identity == _probedFfmpeg)
            return;

        _probedFfmpeg = identity;
        var run = ++_probeRun;
        ProbeBar.Visibility = Visibility.Visible;
        EncoderStatusText.Text = "Detecting GPU hardware encoders...";
        try
        {
            // ProbeAsync keeps its answer (encoder-probe.json), so the main window starts with it.
            var (hardware, _) = await EncoderProber.ProbeAsync(_probeCancellation.Token);
            if (run != _probeRun)
                return;

            FillEncoders(hardware);
            EncoderStatusText.Text = hardware.Count switch
            {
                0 => "No hardware encoders found on this machine.",
                1 => "1 hardware encoder found.",
                _ => $"{hardware.Count} hardware encoders found.",
            };
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Not kept as asked: the next change of components tries again.
            if (run == _probeRun)
                (_probedFfmpeg, EncoderStatusText.Text) = ("", "Hardware encoders could not be checked.");
        }
        finally
        {
            if (run == _probeRun)
                ProbeBar.Visibility = Visibility.Collapsed;
        }
    }

    // ----- Test & Auto-Tune Hardware (5.0-0): the four GPU roles from a few seconds' measuring -----

    // The benchmark running from here, while it runs: pressing the button again, or closing the window, cancels it.
    private CancellationTokenSource? _benchmarkRun;

    /// <summary>
    /// Runs the hardware benchmark and, when it finishes, puts the roles it recommends into the settings in effect: unsaved,
    /// like every choice here, so Start keeps them (Finish saves a copy of those settings) and closing the window does not.
    /// </summary>
    private async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkRun is { } running)
        {
            running.Cancel();
            BenchmarkStatusText.Text = "Stopping...";
            return;
        }

        using var run = _benchmarkRun = new CancellationTokenSource();
        Closed += Cancel;
        BenchmarkButtonText.Text = "Stop the Test";
        BenchmarkStatusText.Text = "Starting the test...";
        BenchmarkStatusText.ToolTip = null;
        var progress = new Progress<HardwareBenchmarkProgress>(step =>
        {
            if (ReferenceEquals(_benchmarkRun, run))
                BenchmarkStatusText.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{step.Message}  {Math.Clamp(step.Fraction, 0, 1) * 100:0}%");
        });

        HardwareBenchmarkResult? result;
        try
        {
            result = await HardwareBenchmarkService.RunAsync(progress, run.Token);
        }
        finally
        {
            Closed -= Cancel;
            _benchmarkRun = null;
            BenchmarkButtonText.Text = "Test & Auto-Tune Hardware";
        }

        if (_closed)
            return;

        if (result is null)
        {
            BenchmarkStatusText.Text = HardwareBenchmarkService.LastFailure is { Length: > 0 } why ? why : "The test could not be run.";
            return;
        }

        if (result.Error.Length > 0 || result.Adapters.Count == 0)
        {
            BenchmarkStatusText.Text = result.Error.Length > 0 ? $"Stopped short ({result.Error}): the GPU roles stay on Auto." : "No graphics card to test: the GPU roles stay on Auto.";
            return;
        }

        _benchmarkRoles = result.Recommended;
        // A card Windows lists twice (SameCardAs) is one card.
        var cards = result.Adapters.Count(a => a.SameCardAs < 0);
        BenchmarkStatusText.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Done in {result.TotalSeconds:0.0} s: {cards} graphics card{(cards == 1 ? "" : "s")} measured, the four GPU roles set.");
        BenchmarkStatusText.ToolTip = result.Recommended.Reasons.Count > 0 ? string.Join(Environment.NewLine, result.Recommended.Reasons) : null;

        void Cancel(object? window, EventArgs closed) => run.Cancel();
    }

    // What a crash report holds, and what it never does.
    private void PrivacyPolicy_Click(object sender, RoutedEventArgs e) =>
        PrivacyPolicyDialog.ShowFor(this, CrashReportsBox.IsChecked == true, on => CrashReportsBox.IsChecked = on);

    // Shown at once, so the choice can be made by looking.
    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is string theme)
        {
            AppSettings.Current.Theme = theme;
            ThemeManager.Apply();
        }
    }

    /// <summary>
    /// Whether an encode, a preview or a transcription is running behind this window. Only possible when it
    /// was opened again from the settings; nothing is installed while it is.
    /// </summary>
    public bool AppIsBusy
    {
        get => Dependencies.AppIsBusy;
        set => Dependencies.AppIsBusy = value;
    }

    /// <summary>Raised when components were installed or replaced from this window.</summary>
    public event Action? DependenciesChanged
    {
        add => Dependencies.Changed += value;
        remove => Dependencies.Changed -= value;
    }

    /// <summary>
    /// For when the download cannot be made, or is not wanted: a folder already on the machine that holds ffmpeg.exe
    /// (in it or in its bin folder). Saved at once, so it holds even if this window is then closed without pressing Start.
    /// </summary>
    private void UseExistingFfmpeg()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the folder that holds ffmpeg.exe" };
        if (dialog.ShowDialog(this) != true)
            return;

        var ffmpeg = new[] { dialog.FolderName, Path.Combine(dialog.FolderName, "bin") }.Select(folder => Path.Combine(folder, "ffmpeg.exe")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            MessageBox.Show(this, "That folder has no ffmpeg.exe (in it or in a bin folder inside it).", "HandPeg", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = AppSettings.Current.Clone();
        settings.FfmpegPath = ffmpeg;
        try
        {
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The path could not be saved.\n\n{ex.Message}", "HandPeg", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _ = Dependencies.RefreshAsync();

        // The FFmpeg that was pointed at is the one in use from here on: its encoders are asked for now.
        _ = ProbeEncodersAsync();
    }

    // Set while Start waits for the hardware encoder check.
    private bool _finishing;

    /// <summary>
    /// Start: when there is an FFmpeg, its hardware encoders are always known first. An asking still running is waited
    /// for, and an FFmpeg not asked yet is asked now, so that the probe's answer is saved and the best encoder chosen
    /// (unless one was chosen by hand) before the settings are.
    /// </summary>
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_finishing)
            return;

        if (File.Exists(DependencyUpdater.FfmpegPath))
        {
            _finishing = true;
            StartButton.IsEnabled = false;
            (StartStatusText.Text, StartStatusText.Visibility) = ("Finishing GPU hardware encoder check...", Visibility.Visible);
            try
            {
                await ProbeEncodersAsync();
                while (!_probeTask.IsCompleted)
                    await _probeTask;
            }
            finally
            {
                _finishing = false;
                StartStatusText.Visibility = Visibility.Collapsed;
                StartButton.IsEnabled = !Dependencies.IsRunning;
            }

            // Closed while it waited: that is not a Start.
            if (_closed)
                return;
        }

        Finish();
    }

    private void Finish()
    {
        var settings = AppSettings.Current.Clone();
        settings.Theme = ThemeBox.SelectedItem as string ?? ThemeManager.FollowSystem;
        settings.AutoFallbackToSoftware = FallbackBox.IsChecked == true;

        // The interface as HandPeg starts it, on a first start only: opened again (from the settings, or because the
        // engines are missing on this PC), the user's own view settings and arrangement stay.
        if (!settings.FirstRunComplete)
            settings.ApplyEditorDefaults();
        settings.DefaultVideoEncoder = EncoderBox.SelectedItem is EncoderOption encoder ? encoder.Name : "";
        settings.PromptToSaveOnExit = PromptBox.IsChecked == true;
        settings.SendCrashReports = CrashReportsBox.IsChecked == true;
        (settings.DefaultAudioEncoder, settings.DefaultAudioBitrate) = (AudioEncoderBox.SelectedItem as string ?? "aac", AudioBitrateBox.SelectedItem as string ?? "192k");

        // The workspace: on a first start always (there is no arrangement yet); opened again from the settings, only
        // when a card was chosen here, so that the arrangement in use is not replaced by the card that was simply ticked.
        // The main window lays itself out so and writes it into DockLayout (MainWindow.TakeFirstRunLayout).
        if (!settings.FirstRunComplete || _workspaceChosen)
            settings.FirstRunLayout = ChosenWorkspace();
        settings.FirstRunComplete = true;
        if (_benchmarkRoles is { } roles)
            HardwareBenchmarkService.WriteRoles(roles, settings);

        // The captions package's larger model is the one captions use, once it is there.
        if (EditorTier.IsChecked == true && File.Exists(DependencyUpdater.GetWhisperModelPath(TierDefaultModel)))
            settings.WhisperModel = TierDefaultModel;

        try
        {
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be written to: the choices still hold for this session, and the window
            // will come back next time.
            MessageBox.Show(this, $"The settings could not be saved, so this window will be shown again next time.\n\n{ex.Message}",
                "HandPeg", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _confirmed = true;
        ThemeManager.Apply();
        DialogResult = true;
    }
}
