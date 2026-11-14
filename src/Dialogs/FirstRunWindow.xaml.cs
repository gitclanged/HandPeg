using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Shown before the main window on a first start (and again from Settings), in two screens (5.5A, FirstRunWindow.Screens.cs):
/// 1. the workspace the window starts laid out as, its theme (both shown at once in a live miniature) and the core engines with
/// their one-click downloads; 2. the GPU and NPU roles from the two-phase hardware profile (FirstRunWindow.Profile.cs: an estimate at
/// once, out of process, then a quick measurement in the background on the first start), the encoders HandPeg starts with (the
/// hardware ones estimated from the cards, then always confirmed by FFmpeg before it closes), what happens on closing and after a
/// failed hardware encode, and whether crash reports are written (with the Privacy Policy beside it). Closing it without pressing
/// Start Editing leaves the settings alone, and it is shown again next time.
/// </summary>
public partial class FirstRunWindow : Window
{
    // The theme in effect when the window opened, put back if it is closed without confirming.
    private readonly string _originalTheme = AppSettings.Current.Theme;
    private bool _confirmed;

    // The roles of the hardware profile shown (FirstRunWindow.Profile.cs): written into the settings only by Start (Finish), on a first
    // start, as every other choice here.
    private HardwareBenchmarkRoles? _benchmarkRoles;
    private bool _closed;

    // Whether the workspace was chosen here by hand: opened again from the settings, the window keeps the arrangement in use unless it was.
    private bool _workspaceChosen;
    private readonly bool _initialized;

    // Whether the window starts its own work (the hardware profile, the encoder probe, the component panel's look online): off for the
    // goldens only, whose window must reach no network and start no worker of its own.
    private readonly bool _live;

    public FirstRunWindow()
        : this(live: true)
    {
    }

    /// <summary>
    /// The window; with <paramref name="live"/> false (the goldens) nothing is started on its own (no hardware profile, no encoder probe)
    /// and the component panel is left out of the window, so it never asks GitHub for the latest builds.
    /// </summary>
    internal FirstRunWindow(bool live)
    {
        _live = live;
        InitializeComponent();

        _chosenTheme = ThemeManager.Normalize(_originalTheme);
        FallbackBox.IsChecked = AppSettings.Current.AutoFallbackToSoftware;
        PromptBox.IsChecked = AppSettings.Current.PromptToSaveOnExit;
        CrashReportsBox.IsChecked = AppSettings.Current.SendCrashReports;

        // The audio settings an editor starts with: encoded, so that edits to the sound never meet a copied track.
        AudioEncoderBox.ItemsSource = AudioTrack.AllCodecs;
        AudioEncoderBox.SelectedItem = AudioTrack.AllCodecs.Contains(AppSettings.Current.DefaultAudioEncoder) ? AppSettings.Current.DefaultAudioEncoder : "aac";
        AudioBitrateBox.ItemsSource = AudioTrack.AllBitrates;
        AudioBitrateBox.SelectedItem = AudioTrack.AllBitrates.Contains(AppSettings.Current.DefaultAudioBitrate) ? AppSettings.Current.DefaultAudioBitrate : "192k";

        // The components in plain words: what the cards' buttons fetch. (The 5.5A addenda, the user's call: an FFmpeg already on the PC is
        // pointed at in Settings, its ffmpeg.exe Path box, never here.)
        Dependencies.InstallLabel = "Download Required Components";
        Dependencies.ReadyLabel = "All required components are installed";

        // The two screens: the theme swatches, the live miniature, the slide between them, the fit to the work area, the download's
        // progress in the footer and the cards' Download buttons (FirstRunWindow.Screens.cs).
        InitializeScreens();

        // The encoders: what is always there at once, the hardware ones estimated from the cards as soon as the profile has them,
        // and asked of FFmpeg as soon as there is one (now, when it is here already). A component installed from this window may be the
        // FFmpeg that makes the asking possible, so then it is asked again, and Start waits for the asking to end (Start_Click).
        _wantedEncoder = AppSettings.Current.DefaultVideoEncoder;
        RefreshEncoders();
        Closed += (_, _) =>
        {
            _closed = true;
            _probeCancellation.Cancel();

            // 5.5A: the hardware profile's workers end with the window (killed, not waited for).
            _profileCancellation.Cancel();
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

        // 5.5.2 (the user's Q2): the Playback & A/V Sync Test's result, kept by Start Editing with the other choices; opened again from the
        // settings, the last one kept is shown.
        SyncTestCard.Completed += result => _syncResult = result;
        SyncTestCard.ShowKept(AppSettings.Current.LastPlaybackSyncTest);

        if (live)
            StartLiveWork();
        else if (Dependencies.Parent is Panel holder)
            holder.Children.Remove(Dependencies);
        _initialized = true;
    }

    // The Playback & A/V Sync Test's result from this window (null none), written by Finish.
    private PlaybackSyncResult? _syncResult;

    /// <summary>What the window starts on its own: the encoder probe and its re-asking, and the hardware profile.</summary>
    private void StartLiveWork()
    {
        _ = ProbeEncodersAsync();
        Dependencies.Changed += () => _ = ProbeEncodersAsync();

        // And whenever an install pass ends, however it ended: one that was cancelled or failed part of the way
        // may still have left an FFmpeg behind, and the encoders are asked of whichever FFmpeg is there now.
        Dependencies.RunningChanged += () =>
        {
            if (!Dependencies.IsRunning)
                _ = ProbeEncodersAsync();
        };

        // An FFmpeg installed from anywhere while this window is open (this panel, Settings, a feature's prompt, the
        // background update): its encoders are asked for the moment it is in place (5.2), on this window's thread; and (5.5A) the
        // quick hardware check that could only measure the bus measures the decoding too, with the test clip FFmpeg makes.
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

        // The hardware profile (5.5A): the estimate now, off this thread; the quick check once the window is up.
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
    /// The built-in layout whose card is chosen, by the name the Layout button lists it under (the main window takes it up and makes it the
    /// Default layout too, 5.4D: MainWindow.TakeFirstRunLayout).
    /// </summary>
    private string ChosenWorkspace() =>
        new[] { StandardLayoutCard, ColorLayoutCard, AssemblyLayoutCard, ShortsLayoutCard, VerticalCreatorLayoutCard }.FirstOrDefault(c => c?.IsChecked == true)?.Tag as string
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

    private readonly CancellationTokenSource _probeCancellation = new();

    // The encoder to select when the list is (re)filled: the one saved before, or blank for "the best there is".
    private string _wantedEncoder;
    private bool _fillingEncoders;

    // The hardware encoders FFmpeg's probe found (null until it has answered: then the profile's estimate stands in), the profile's
    // estimate, and what the list was last filled from (it is filled again only when that changes).
    private IReadOnlyList<string>? _verifiedHardware;
    private IReadOnlyList<string> _estimatedHardware = [];
    private string? _encodersShown;

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

    /// <summary>
    /// Fills the list again when what it is filled from has changed: FFmpeg's answer when it has come (it always wins, whichever arrives
    /// first), else the hardware profile's estimate; ranked for the Export role's card (5.5A).
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
    /// Hardware encoders first, the Export role's card's own family first (5.5A: the Desktop's RX 7900 XTX composites and exports, so AMF
    /// before the Arc A310's QuickSync), then NVENC, QuickSync, AMF and Media Foundation; then the software ones; Copy last.
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

    /// <summary>FFmpeg's answer (the hardware encoders that encoded here): the list from now on, whatever the profile estimated.</summary>
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
            (_probedFfmpeg, _probeRun, _verifiedHardware) = ("", _probeRun + 1, null);
            RefreshEncoders();
            ProbeBar.Visibility = Visibility.Collapsed;
            ShowEstimatedEncoderStatus();
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

            ShowVerifiedEncoders(hardware);
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

    // Until FFmpeg is here: the encoders the profile estimated from the cards, said as such.
    private void ShowEstimatedEncoderStatus()
    {
        // FFmpeg's answer is in, or an FFmpeg is being asked (its probe says so itself).
        if (_verifiedHardware is not null || _probedFfmpeg.Length > 0)
            return;

        EncoderStatusText.Text = _estimatedHardware.Count > 0
            ? "Estimated from the graphics cards: confirmed by FFmpeg once the core engines are downloaded."
            : "Hardware encoders are detected once the core engines are downloaded.";
    }

    // What a crash report holds, and what it never does.
    private void PrivacyPolicy_Click(object sender, RoutedEventArgs e) =>
        PrivacyPolicyDialog.ShowFor(this, CrashReportsBox.IsChecked == true, on => CrashReportsBox.IsChecked = on);

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

    // Set while Start waits for the hardware encoder check.
    private bool _finishing;

    /// <summary>
    /// Start Editing: when there is an FFmpeg, its hardware encoders are always known first. An asking still running is waited
    /// for, and an FFmpeg not asked yet is asked now, so that the probe's answer is saved and the best encoder chosen
    /// (unless one was chosen by hand) before the settings are.
    /// </summary>
    private async void Start_Click(object sender, RoutedEventArgs e)
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

            // Closed while it waited: that is not a Start.
            if (_closed)
                return;
        }

        Finish();
    }

    private void Finish()
    {
        // 5.5A: the hardware profile's workers (a quick check still measuring, an estimate not back yet) are killed first, before the
        // main window and its compositor start.
        _profileCancellation.Cancel();
        SyncTestCard.Cancel();

        var settings = AppSettings.Current.Clone();
        if (_syncResult is { } sync)
            settings.LastPlaybackSyncTest = sync;
        var firstStart = !settings.FirstRunComplete;
        settings.Theme = _chosenTheme;
        settings.AutoFallbackToSoftware = FallbackBox.IsChecked == true;

        // The interface as HandPeg starts it, on a first start only: opened again (from the settings, or because the
        // engines are missing on this PC), the user's own view settings and arrangement stay.
        if (firstStart)
            settings.ApplyEditorDefaults();
        settings.DefaultVideoEncoder = EncoderBox.SelectedItem is EncoderOption encoder ? encoder.Name : "";
        if (EncoderBox.SelectedItem is EncoderOption { Family: var family } && ViewModels.MainViewModel.BestStepOf(family) is { } best)
            settings.DefaultEncoderPreset = best;
        settings.PromptToSaveOnExit = PromptBox.IsChecked == true;
        settings.SendCrashReports = CrashReportsBox.IsChecked == true;
        (settings.DefaultAudioEncoder, settings.DefaultAudioBitrate) = (AudioEncoderBox.SelectedItem as string ?? "aac", AudioBitrateBox.SelectedItem as string ?? "192k");

        // The workspace: on a first start always (there is no arrangement yet); opened again from the settings, only
        // when a card was chosen here, so that the arrangement in use is not replaced by the card that was simply ticked.
        // The main window lays itself out so and writes it into DockLayout (MainWindow.TakeFirstRunLayout).
        if (firstStart || _workspaceChosen)
            settings.FirstRunLayout = ChosenWorkspace();
        settings.FirstRunComplete = true;

        // 5.5A: the GPU roles of the profile shown, on a first start (opened again, the roles stay as Settings has them: a card chosen there
        // by hand is never undone from here), against the cards the profile found itself (nothing lists the cards in this process). 5.5A
        // review: Decode and Background always Auto, whatever an older saved result shown here holds.
        if (firstStart && _benchmarkRoles is { } roles && _profile is { } profile)
            HardwareBenchmarkService.WriteRoles(HardwareBenchmarkService.DecodeAndBackgroundAuto(roles), settings, [.. profile.Adapters.Select(a => a.Index)]);

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
