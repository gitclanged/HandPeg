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
    /// <summary>What was chosen: only the classic way in Encoder Mode; in Editor Mode, marking the silences or deleting them.</summary>
    public ViewModels.DeadAirMode Mode { get; private set; } = ViewModels.DeadAirMode.Classic;

    /// <param name="editor">Editor Mode: offers Split &amp; Mark beside Delete Dead Air.</param>
    /// <param name="target">What will be listened to and cut, in a few words, for the title.</param>
    public DeadAirDialog(bool editor = false, string target = "")
    {
        InitializeComponent();
        _editor = editor;
        if (editor)
        {
            MarkButton.Visibility = Visibility.Visible;
            ConfirmButton.Content = "Delete Dead Air";
            ConfirmButton.ToolTip = "Cuts at the silences and removes the silent stretches.";
            if (target.Length > 0)
                Title = $"Remove Dead Air: {target}";
        }

        ThresholdSlider.Value = Math.Clamp(AppSettings.Current.DeadAirThresholdDb, ThresholdSlider.Minimum, ThresholdSlider.Maximum);
        DurationSlider.Value = Math.Clamp(AppSettings.Current.DeadAirMinSeconds, DurationSlider.Minimum, DurationSlider.Maximum);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private readonly bool _editor;

    private void Mark_Click(object sender, RoutedEventArgs e)
    {
        Mode = ViewModels.DeadAirMode.SplitAndMark;
        Save();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Mode = _editor ? ViewModels.DeadAirMode.Delete : ViewModels.DeadAirMode.Classic;
        Save();
    }

    private void Save()
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
