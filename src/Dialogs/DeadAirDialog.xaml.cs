using System.IO;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Remove Dead Air options: detection by audio silence (threshold, minimum duration) or caption word gaps (minimum gap, per-word
/// padding). One-click presets set all values. Persisted in settings so a microphone's threshold is tuned once.
/// </summary>
public partial class DeadAirDialog : Window
{
    /// <summary>Chosen action: mark or delete dead air.</summary>
    public ViewModels.DeadAirMode Mode { get; private set; } = ViewModels.DeadAirMode.Delete;

    /// <summary>Detection method.</summary>
    public ViewModels.DeadAirSource Source => CaptionsSourceRadio.IsChecked == true ? ViewModels.DeadAirSource.Captions : ViewModels.DeadAirSource.Audio;

    /// <param name="target">Short description of the analysed/cut target for the title.</param>
    public DeadAirDialog(string target = "")
    {
        InitializeComponent();
        if (target.Length > 0)
            Title = $"Remove Dead Air: {target}";

        var settings = AppSettings.Current;
        ThresholdSlider.Value = Math.Clamp(settings.DeadAirThresholdDb, ThresholdSlider.Minimum, ThresholdSlider.Maximum);
        DurationSlider.Value = Math.Clamp(settings.DeadAirMinSeconds, DurationSlider.Minimum, DurationSlider.Maximum);
        MinGapSlider.Value = Math.Clamp(settings.DeadAirCaptionMinGapSeconds, MinGapSlider.Minimum, MinGapSlider.Maximum);
        PadBeforeSlider.Value = Math.Clamp(settings.DeadAirCaptionPadBeforeSeconds, PadBeforeSlider.Minimum, PadBeforeSlider.Maximum);
        PadAfterSlider.Value = Math.Clamp(settings.DeadAirCaptionPadAfterSeconds, PadAfterSlider.Minimum, PadAfterSlider.Maximum);
        (settings.DeadAirSource == AppSettings.DeadAirFromCaptions ? CaptionsSourceRadio : AudioSourceRadio).IsChecked = true;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>Shows the parameter grid for the selected method.</summary>
    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        // Fires during XAML load, before the parameter grids exist.
        if (AudioGrid is null || CaptionsGrid is null || AudioHint is null)
            return;

        var by = Source;
        AudioGrid.Visibility = AudioHint.Visibility = by == ViewModels.DeadAirSource.Audio ? Visibility.Visible : Visibility.Collapsed;
        CaptionsGrid.Visibility = by == ViewModels.DeadAirSource.Captions ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One-click preset setting both methods' values (editable afterwards).</summary>
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var (threshold, shortest, gap, before, after) = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "Tight" => (-35.0, 0.5, 0.3, 0.05, 0.08),
            "Podcast" => (-40.0, 1.5, 0.8, 0.12, 0.2),
            _ => (-35.0, 1.0, 0.5, 0.08, 0.12),
        };
        (ThresholdSlider.Value, DurationSlider.Value) = (threshold, shortest);
        (MinGapSlider.Value, PadBeforeSlider.Value, PadAfterSlider.Value) = (gap, before, after);
    }


    private void Mark_Click(object sender, RoutedEventArgs e)
    {
        Mode = ViewModels.DeadAirMode.SplitAndMark;
        Save();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Mode = ViewModels.DeadAirMode.Delete;
        Save();
    }

    private void Save()
    {
        var (threshold, minimum) = (ThresholdSlider.Value, Math.Round(DurationSlider.Value, 1));
        var source = Source switch
        {
            ViewModels.DeadAirSource.Captions => AppSettings.DeadAirFromCaptions,
            _ => AppSettings.DeadAirFromAudio,
        };
        var (gap, before, after) = (Math.Round(MinGapSlider.Value, 2), Math.Round(PadBeforeSlider.Value, 3), Math.Round(PadAfterSlider.Value, 3));
        AppSettings.Update(settings =>
        {
            (settings.DeadAirThresholdDb, settings.DeadAirMinSeconds, settings.DeadAirSource) = (threshold, minimum, source);
            (settings.DeadAirCaptionMinGapSeconds, settings.DeadAirCaptionPadBeforeSeconds, settings.DeadAirCaptionPadAfterSeconds) = (gap, before, after);
        });

        DialogResult = true;
    }
}
