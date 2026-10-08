using System.IO;
using System.Windows;
using System.Windows.Controls;
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
