using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Shown before the main window on a first start: the theme, whether a failed hardware encode may fall back
/// to software, and the mode, which sets a whole group of settings at once. Closing it without pressing
/// Start leaves the settings alone, and it is shown again next time.
/// </summary>
public partial class FirstRunWindow : Window
{
    // The theme in effect when the window opened, put back if it is closed without confirming.
    private readonly string _originalTheme = AppSettings.Current.Theme;
    private bool _confirmed;

    public FirstRunWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;

        ThemeBox.ItemsSource = ThemeManager.Themes;
        ThemeBox.SelectedItem = ThemeManager.Themes.Contains(_originalTheme) ? _originalTheme : ThemeManager.FollowSystem;
        FallbackBox.IsChecked = AppSettings.Current.AutoFallbackToSoftware;
        (AppSettings.Current.UiMode == AppSettings.EditorMode ? EditorCard : EncoderCard).IsChecked = true;

        Dependencies.UseExistingFfmpegRequested += UseExistingFfmpeg;

        // The encoders: what is always there at once, the hardware ones when FFmpeg has been asked. A tool
        // installed from this window may be the FFmpeg that makes the asking possible, so then it is asked again.
        _wantedEncoder = AppSettings.Current.DefaultVideoEncoder;
        FillEncoders([]);
        Loaded += (_, _) => _ = ProbeEncodersAsync();
        Dependencies.Changed += () => _ = ProbeEncodersAsync();
        Closed += (_, _) => _probeCancellation.Cancel();
        UpdateEncoderPanel();

        // Closing the window stops an install, so Start waits for it (or for Cancel).
        Dependencies.RunningChanged += () => StartButton.IsEnabled = !Dependencies.IsRunning;

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closed += (_, _) =>
        {
            if (!_confirmed)
            {
                AppSettings.Current.Theme = _originalTheme;
                ThemeManager.Apply();
            }
        };
    }

    // ----- Default video encoder (Editor Mode) -----

    // The best hardware encoder is the first of these that works here: NVENC, then QuickSync, then AMF.
    private static readonly string[] PreferredHardware = ["h264_nvenc", "h264_qsv", "h264_amf"];

    private readonly CancellationTokenSource _probeCancellation = new();

    // The encoder to select when the list is (re)filled: the one saved before, or blank for "the best there is".
    private string _wantedEncoder;
    private bool _fillingEncoders;

    private void Mode_Checked(object sender, RoutedEventArgs e) => UpdateEncoderPanel();

    private void UpdateEncoderPanel()
    {
        // Raised while the window is still being built.
        if (EncoderPanel is null || EncoderBox is null)
            return;

        EncoderPanel.Visibility = EditorCard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CopyWarningText.Visibility = EncoderBox.SelectedItem is EncoderOption { Family: EncoderFamily.Copy } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EncoderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A choice made by hand is kept when the list is filled again.
        if (!_fillingEncoders && EncoderBox.SelectedItem is EncoderOption chosen)
            _wantedEncoder = chosen.Name;
        UpdateEncoderPanel();
    }

    /// <summary>Hardware encoders that work here first, best vendor first; then the software ones; Copy last.</summary>
    private void FillEncoders(IReadOnlyList<string> hardware)
    {
        var rank = (string name) => name.EndsWith("_nvenc", StringComparison.Ordinal) ? 0 : name.EndsWith("_qsv", StringComparison.Ordinal) ? 1 : 2;
        var encoders = hardware.OrderBy(rank).Select(EncoderOption.FromHardwareName).Concat(EncoderOption.Software).Append(EncoderOption.Copy).ToList();

        var best = PreferredHardware.FirstOrDefault(hardware.Contains) ?? EncoderOption.Software[0].Name;
        var select = encoders.FirstOrDefault(e => e.Name == _wantedEncoder) ?? encoders.First(e => e.Name == best);

        _fillingEncoders = true;
        EncoderBox.ItemsSource = encoders;
        EncoderBox.SelectedItem = select;
        _fillingEncoders = false;
        UpdateEncoderPanel();
    }

    private async Task ProbeEncodersAsync()
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
        {
            EncoderStatusText.Text = "Hardware encoders are looked for once FFmpeg is installed (below).";
            return;
        }

        EncoderStatusText.Text = "Checking hardware encoders...";
        try
        {
            var (hardware, _) = await EncoderProber.ProbeAsync(_probeCancellation.Token);
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
            EncoderStatusText.Text = "Hardware encoders could not be checked.";
        }
    }

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

    /// <summary>Raised when tools were installed or replaced from this window.</summary>
    public event Action? DependenciesChanged
    {
        add => Dependencies.Changed += value;
        remove => Dependencies.Changed -= value;
    }

    /// <summary>
    /// For when the download cannot be made, or is not wanted: an ffmpeg.exe already on the machine. Saved at
    /// once, so it holds even if this window is then closed without pressing Start.
    /// </summary>
    private void UseExistingFfmpeg()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select your ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|Programs|*.exe" };
        if (dialog.ShowDialog(this) != true)
            return;

        var settings = AppSettings.Current.Clone();
        settings.FfmpegPath = dialog.FileName;
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
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current.Clone();
        settings.Theme = ThemeBox.SelectedItem as string ?? ThemeManager.FollowSystem;
        settings.AutoFallbackToSoftware = FallbackBox.IsChecked == true;
        settings.ApplyMode(EditorCard.IsChecked == true ? AppSettings.EditorMode : AppSettings.EncoderMode);
        settings.DefaultVideoEncoder = EditorCard.IsChecked == true && EncoderBox.SelectedItem is EncoderOption encoder ? encoder.Name : "";
        settings.DefaultMode = settings.UiMode;
        settings.FirstRunComplete = true;

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
        Close();
    }
}
