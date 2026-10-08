using System.IO;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Asked before Remove Dead Air runs: how quiet counts as silence, and how long a silence has to be. Both are
/// kept in the settings, so a threshold found for a microphone is found once.
/// </summary>
public partial class DeadAirDialog : Window
{
    public DeadAirDialog()
    {
        InitializeComponent();
        ThresholdSlider.Value = Math.Clamp(AppSettings.Current.DeadAirThresholdDb, ThresholdSlider.Minimum, ThresholdSlider.Maximum);
        DurationSlider.Value = Math.Clamp(AppSettings.Current.DeadAirMinSeconds, DurationSlider.Minimum, DurationSlider.Maximum);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        (settings.DeadAirThresholdDb, settings.DeadAirMinSeconds) = (ThresholdSlider.Value, Math.Round(DurationSlider.Value, 1));
        try
        {
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The values still hold for this session.
        }

        DialogResult = true;
    }
}
