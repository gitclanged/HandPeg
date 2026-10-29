using System.IO;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Asked before Remove Dead Air runs: what the dead air is found by (4.4: the sound's silences, or the gaps between the
/// words its captions heard), and the values of each: how quiet counts as silence and for how long, or how long a gap
/// between words has to be and how much of it is kept beside each word. One-click presets set them all. Everything is
/// kept in the settings, so a threshold found for a microphone is found once.
/// </summary>
public partial class DeadAirDialog : Window
{
    /// <summary>What was chosen: marking the dead air or deleting it.</summary>
    public ViewModels.DeadAirMode Mode { get; private set; } = ViewModels.DeadAirMode.Delete;

    /// <summary>What the dead air is found by.</summary>
    public ViewModels.DeadAirSource Source => CaptionsSourceRadio.IsChecked == true ? ViewModels.DeadAirSource.Captions : ViewModels.DeadAirSource.Audio;

    /// <param name="target">What will be listened to and cut, in a few words, for the title.</param>
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

    /// <summary>Shows the values of the way chosen.</summary>
    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        // Raised while the window is still being read in, before the grids below the buttons exist.
        if (AudioGrid is null || CaptionsGrid is null || AudioHint is null)
            return;

        var byCaptions = Source == ViewModels.DeadAirSource.Captions;
        AudioGrid.Visibility = AudioHint.Visibility = byCaptions ? Visibility.Collapsed : Visibility.Visible;
        CaptionsGrid.Visibility = byCaptions ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A one-click preset: the values of both ways at once, which can then be changed by hand.</summary>
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
        var settings = AppSettings.Current;
        (settings.DeadAirThresholdDb, settings.DeadAirMinSeconds) = (ThresholdSlider.Value, Math.Round(DurationSlider.Value, 1));
        settings.DeadAirSource = Source == ViewModels.DeadAirSource.Captions ? AppSettings.DeadAirFromCaptions : AppSettings.DeadAirFromAudio;
        (settings.DeadAirCaptionMinGapSeconds, settings.DeadAirCaptionPadBeforeSeconds, settings.DeadAirCaptionPadAfterSeconds) =
            (Math.Round(MinGapSlider.Value, 2), Math.Round(PadBeforeSlider.Value, 3), Math.Round(PadAfterSlider.Value, 3));
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
