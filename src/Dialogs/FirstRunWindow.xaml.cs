using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// First-run window (also reopenable from Settings), two screens (FirstRunWindow.Screens.cs): (1) workspace layout and theme with a
/// live miniature, plus core engine downloads; (2) GPU/NPU roles from the two-phase hardware profile (FirstRunWindow.Profile.cs:
/// out-of-process estimate, then full test; roles pickable per card), startup encoders (estimated, then confirmed by FFmpeg), close
/// behaviour and crash reporting. Closing without Start Editing changes nothing and it shows again next start.
/// </summary>
public partial class FirstRunWindow : Window
{
    // Theme at open, restored if closed without confirming.
    private readonly string _originalTheme = AppSettings.Current.Theme;
    private bool _confirmed;

    // Displayed hardware profile roles (FirstRunWindow.Profile.cs); persisted only by Start (Finish) on a first start.
    private HardwareBenchmarkRoles? _benchmarkRoles;
    private bool _closed;

    // Whether a workspace card was explicitly chosen; when reopened from Settings the current layout is kept otherwise.
    private bool _workspaceChosen;
    private readonly bool _initialized;

    // Whether the window starts background work (hardware profile, encoder probe, online component check); off only for goldens,
    // which must not touch the network or spawn workers.
    private readonly bool _live;

    public FirstRunWindow()
        : this(live: true)
    {
    }

    /// <summary>
    /// With <paramref name="live"/> false (goldens) no background work starts and the component panel is omitted, so GitHub is never queried.
    /// </summary>
    internal FirstRunWindow(bool live)
    {
        _live = live;
        InitializeComponent();

        _chosenTheme = ThemeManager.Normalize(_originalTheme);
        PromptBox.IsChecked = AppSettings.Current.PromptToSaveOnExit;
        CrashReportsBox.IsChecked = AppSettings.Current.SendCrashReports;

        // Default audio is encoded so audio edits never conflict with stream copy.
        AudioEncoderBox.ItemsSource = AudioTrack.AllCodecs;
        AudioEncoderBox.SelectedItem = AudioTrack.AllCodecs.Contains(AppSettings.Current.DefaultAudioEncoder) ? AppSettings.Current.DefaultAudioEncoder : "aac";
        AudioBitrateBox.ItemsSource = AudioTrack.AllBitrates;
        AudioBitrateBox.SelectedItem = AudioTrack.AllBitrates.Contains(AppSettings.Current.DefaultAudioBitrate) ? AppSettings.Current.DefaultAudioBitrate : "192k";

        // Plain-language component descriptions for the card buttons; an existing FFmpeg is configured in Settings (ffmpeg.exe Path), not here.
        Dependencies.InstallLabel = "Download Required Components";
        Dependencies.ReadyLabel = "All required components are installed";

        // Two-screen setup: theme swatches, live miniature, slide transition, work-area fit, footer download progress and card
        // Download buttons (FirstRunWindow.Screens.cs).
        InitializeScreens();

        // Encoders: software immediately, hardware estimated from the profile, then probed via FFmpeg once present. Installing FFmpeg
        // here triggers a re-probe; Start waits for it (Start_Click).
        _wantedEncoder = AppSettings.Current.DefaultVideoEncoder;
        RefreshEncoders();
        Closed += (_, _) =>
        {
            _closed = true;
            _probeCancellation.Cancel();

            // Kill hardware profile workers (estimate and full test) with the window; do not wait.
            _profileCancellation.Cancel();
        };

        // Closing aborts an install, so Start waits for it (or Cancel).
        Dependencies.RunningChanged += () => StartButton.IsEnabled = !Dependencies.IsRunning && !_finishing;

        // Compact mode: two cards and one progress bar; per-component rows hidden.
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

        // Playback & A/V Sync Test result, persisted by Start Editing; when reopened, the last saved result is shown.
        SyncTestCard.Completed += result => _syncResult = result;
        SyncTestCard.ShowKept(AppSettings.Current.LastPlaybackSyncTest);

        // Role picked from a card menu, applied by Start Editing (FirstRunWindow.Profile.cs).
        RoleCards.RoleChosen += RoleCards_RoleChosen;

        if (live)
            StartLiveWork();
        else if (Dependencies.Parent is Panel holder)
            holder.Children.Remove(Dependencies);
        _initialized = true;
    }

    // A/V Sync Test result from this window (null for none), written by Finish.
    private PlaybackSyncResult? _syncResult;

    /// <summary>Starts background work: encoder probe (with re-probes) and the hardware profile.</summary>
    private void StartLiveWork()
    {
        _ = ProbeEncodersAsync();
        Dependencies.Changed += () => _ = ProbeEncodersAsync();

        // Re-probe after every install pass, including cancelled or failed ones that may still have installed FFmpeg.
        Dependencies.RunningChanged += () =>
        {
            if (!Dependencies.IsRunning)
                _ = ProbeEncodersAsync();
        };

        // FFmpeg installed from any source while open: probe its encoders immediately on the UI thread, and rerun the full hardware
        // test that lacked a test clip without FFmpeg.
        Action<string> installed = tool =>
        {
            if (tool == DependencyUpdater.Ffmpeg)
                Dispatcher.BeginInvoke(() =>
                {
                    if (_closed)
                        return;
                    _ = ProbeEncodersAsync();
                    MeasureAgainWithDecoding();
                });
        };
        DependencyUpdater.Installed += installed;
        Closed += (_, _) => DependencyUpdater.Installed -= installed;

        // Hardware profile: estimate now off-thread; full test once the window is shown (every open).
        StartProfile();
        Loaded += Profile_Loaded;
    }

    // ----- The starting workspace -----

    private void Workspace_Checked(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            _workspaceChosen = true;
        ShowStage();
    }

    /// <summary>
    /// Selected built-in layout name (as listed by the Layout button); the main window applies it and makes it the Default layout
    /// (MainWindow.TakeFirstRunLayout).
    /// </summary>
    private string ChosenWorkspace() =>
        new[] { StandardLayoutCard, ColorLayoutCard, AssemblyLayoutCard, ShortsLayoutCard, VerticalCreatorLayoutCard }.FirstOrDefault(c => c?.IsChecked == true)?.Tag as string
        ?? "Standard Editing";

    // ----- The required components: two packages -----

    // Speech models fetched by the captions package; the second becomes the active model.
    private const string TierBaseModel = "ggml-base.en.bin";
    private const string TierDefaultModel = "ggml-medium.en.bin";

    private void Tier_Checked(object sender, RoutedEventArgs e) => ApplyTier();

    /// <summary>Updates the component list with the chosen package contents.</summary>
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

    private readonly CancellationTokenSource _probeCancellation = new();

    // Encoder to select on refill: the saved one, or blank for the best available.
    private string _wantedEncoder;
    private bool _fillingEncoders;

    // Probed hardware encoders (null until answered; the profile estimate stands in), the estimate, and the last fill signature.
    private IReadOnlyList<string>? _verifiedHardware;
    private IReadOnlyList<string> _estimatedHardware = [];
    private string? _encodersShown;

    private void EncoderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Preserve a manual choice across refills.
        if (!_fillingEncoders && EncoderBox.SelectedItem is EncoderOption chosen)
            _wantedEncoder = chosen.Name;
        UpdateCopyWarning();
    }

    private void UpdateCopyWarning()
    {
        // May fire during window construction.
        if (CopyWarningText is not null)
            CopyWarningText.Visibility = EncoderBox.SelectedItem is EncoderOption { Family: EncoderFamily.Copy } ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Refills the encoder list when its source changes: the FFmpeg probe once available (always authoritative), else the profile
    /// estimate; ranked for the Export role GPU.
    /// </summary>
    private void RefreshEncoders()
    {
        var hardware = _verifiedHardware ?? _estimatedHardware;
        var shown = $"{string.Join(",", hardware)}|{ExportEncoderSuffix(_profile)}|{HasQualcommAdreno(_profile)}";
        if (shown == _encodersShown)
            return;

        _encodersShown = shown;
        FillEncoders(hardware);
    }

    /// <summary>
    /// Encoder ranking: hardware first with the Export GPU's family leading (e.g. AMF before QSV when an AMD card is the Export GPU beside
    /// an Intel one), then NVENC, QSV, AMF, MF; then software; Copy last.
    /// </summary>
    private void FillEncoders(IReadOnlyList<string> hardware)
    {
        var (ordered, best) = RankEncoders(hardware, ExportEncoderSuffix(_profile));
        var adreno = HasQualcommAdreno(_profile);
        var encoders = ordered.Select(name => EncoderOption.FromHardwareName(name, adreno)).Concat(EncoderOption.Software).Append(EncoderOption.Copy).ToList();
        var select = encoders.FirstOrDefault(e => e.Name == _wantedEncoder) ?? encoders.First(e => e.Name == best);

        _fillingEncoders = true;
        EncoderBox.ItemsSource = encoders;
        EncoderBox.SelectedItem = select;
        _fillingEncoders = false;
        UpdateCopyWarning();
    }

    /// <summary>Applies the FFmpeg probe result (working hardware encoders), superseding the estimate.</summary>
    internal void ShowVerifiedEncoders(IReadOnlyList<string> hardware)
    {
        _verifiedHardware = hardware;
        RefreshEncoders();
        EncoderStatusText.Text = hardware.Count switch
        {
            0 => "No hardware encoders found on this machine.",
            1 => "1 hardware encoder found.",
            _ => $"{hardware.Count} hardware encoders found.",
        };
    }

    // Last probed FFmpeg identity and probe generation: re-probe only on a different FFmpeg; stale answers are dropped.
    private string _probedFfmpeg = "";
    private int _probeRun;

    // Latest probe task (running or completed); Start awaits it.
    private Task _probeTask = Task.CompletedTask;

    /// <summary>Probes the current FFmpeg's hardware encoders unless already probed.</summary>
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
            (_probedFfmpeg, _probeRun, _verifiedHardware) = ("", _probeRun + 1, null);
            RefreshEncoders();
            ProbeBar.Visibility = Visibility.Collapsed;
            ShowEstimatedEncoderStatus();
            return;
        }

        // Same file (path, size, mtime): the previous result stands.
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
            // ProbeAsync persists to encoder-probe.json, which the main window loads at start.
            var (hardware, _) = await EncoderProber.ProbeAsync(_probeCancellation.Token);
            if (run != _probeRun)
                return;

            ShowVerifiedEncoders(hardware);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Not recorded as probed: the next component change retries.
            if (run == _probeRun)
                (_probedFfmpeg, EncoderStatusText.Text) = ("", "Hardware encoders could not be checked.");
        }
        finally
        {
            if (run == _probeRun)
                ProbeBar.Visibility = Visibility.Collapsed;
        }
    }

    // Before FFmpeg exists: profile-estimated encoders, labelled as estimates.
    private void ShowEstimatedEncoderStatus()
    {
        // Probe answered or in progress (it reports its own status).
        if (_verifiedHardware is not null || _probedFfmpeg.Length > 0)
            return;

        EncoderStatusText.Text = _estimatedHardware.Count > 0
            ? "Estimated from the graphics cards: confirmed by FFmpeg once the core engines are downloaded."
            : "Hardware encoders are detected once the core engines are downloaded.";
    }

    // Crash report contents disclosure (included and excluded data).
    private void PrivacyPolicy_Click(object sender, RoutedEventArgs e) =>
        PrivacyPolicyDialog.ShowFor(this, CrashReportsBox.IsChecked == true, on => CrashReportsBox.IsChecked = on);

    /// <summary>Whether an encode, preview or transcription is running (only when reopened from Settings); installs are blocked meanwhile.</summary>
    public bool AppIsBusy
    {
        get => Dependencies.AppIsBusy;
        set => Dependencies.AppIsBusy = value;
    }

    /// <summary>Raised after components are installed or replaced from this window.</summary>
    public event Action? DependenciesChanged
    {
        add => Dependencies.Changed += value;
        remove => Dependencies.Changed -= value;
    }

    // Set while Start awaits the encoder probe.
    private bool _finishing;

    /// <summary>
    /// Start Editing: with FFmpeg present, awaits (or starts) the encoder probe so its result is saved and the best encoder selected
    /// (unless chosen manually) before settings are written.
    /// </summary>
    private void Start_Click(object sender, RoutedEventArgs e) => Faults.Forget(Start_ClickAsync(sender, e), nameof(Start_Click));

    private async Task Start_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (_finishing)
            return;

        if (_live && File.Exists(DependencyUpdater.FfmpegPath))
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

            // Window closed during the wait: not a Start.
            if (_closed)
                return;
        }

        Finish();
    }

    private void Finish()
    {
        // Kill hardware profile workers (pending estimate or full test) before the main window and compositor start.
        _profileCancellation.Cancel();
        SyncTestCard.Cancel();

        var settings = AppSettings.Current.Clone();
        if (_syncResult is { } sync)
            settings.LastPlaybackSyncTest = sync;
        var firstStart = !settings.FirstRunComplete;
        settings.Theme = _chosenTheme;

        // Reset UI defaults on a first start only; when reopened (Settings, or missing engines) existing view settings and layout stay.
        if (firstStart)
            settings.ApplyEditorDefaults();
        settings.DefaultVideoEncoder = EncoderBox.SelectedItem is EncoderOption encoder ? encoder.Name : "";
        if (EncoderBox.SelectedItem is EncoderOption { Family: var family } && ViewModels.MainViewModel.BestStepOf(family) is { } best)
            settings.DefaultEncoderPreset = best;
        settings.PromptToSaveOnExit = PromptBox.IsChecked == true;
        settings.SendCrashReports = CrashReportsBox.IsChecked == true;
        (settings.DefaultAudioEncoder, settings.DefaultAudioBitrate) = (AudioEncoderBox.SelectedItem as string ?? "aac", AudioBitrateBox.SelectedItem as string ?? "192k");

        // Workspace: always on a first start; when reopened only if a card was explicitly chosen (a pre-ticked card never replaces the
        // current layout). The main window applies it and writes DockLayout (MainWindow.TakeFirstRunLayout).
        if (firstStart || _workspaceChosen)
            settings.FirstRunLayout = ChosenWorkspace();
        settings.FirstRunComplete = true;

        // Profile GPU roles apply on a first start only (reopened, Settings' roles are kept), matched against the profile's own GPU list
        // (this process does not enumerate GPUs). Decode and Background are always Auto regardless of older saved results.
        if (firstStart && _benchmarkRoles is { } roles && _profile is { } profile)
            HardwareBenchmarkService.WriteRoles(HardwareBenchmarkService.DecodeAndBackgroundAuto(roles), settings, [.. profile.Adapters.Select(a => a.Index)]);

        // Manually picked card roles apply on any open (roles are only ever assigned by explicit pick), matched to the profile's GPU list.
        if (_rolesChosen && (_profile ?? _figures) is { } listed)
            WritePicks(_picks, settings, [.. listed.Adapters.Where(a => a.SameCardAs < 0).Select(a => a.Index)]);

        // Use the captions package's larger model once installed.
        if (EditorTier.IsChecked == true && File.Exists(DependencyUpdater.GetWhisperModelPath(TierDefaultModel)))
            settings.WhisperModel = TierDefaultModel;

        try
        {
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("First run: the settings could not be saved", ex);

            // Settings folder not writable: choices apply for this session and the window reappears next start.
            MessageBox.Show(this, $"The settings could not be saved, so this window will be shown again next time.\n\n{ex.Message}",
                "HandPeg", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _confirmed = true;
        ThemeManager.Apply();
        DialogResult = true;
    }
}
