using System.Windows;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// The processing of one audio track: compressor, gate, de-noise, tone, and behind "Show Advanced Filters"
/// the limiter, equalizer, pitch/tempo and stereo width. Works on a copy, which the caller takes on OK.
/// </summary>
public partial class TrackAudioFiltersDialog : Window
{
    public TrackAudioFiltersDialog(AudioTrack track) : this($"{track.SourceFileName}   {track.Description}", track.Filters)
    {
    }

    /// <param name="heading">Which sound the filters are for.</param>
    /// <param name="filters">Its filters as they are; a copy is edited.</param>
    public TrackAudioFiltersDialog(string heading, TrackAudioFilters filters)
    {
        InitializeComponent();
        TrackText.Text = heading;
        DataContext = Filters = filters.Clone();
    }

    /// <summary>The filters as edited. Only meaningful once the dialog has been confirmed.</summary>
    public TrackAudioFilters Filters { get; private set; }

    // A fresh set, keeping only whether the advanced part is showing.
    private void Reset_Click(object sender, RoutedEventArgs e) =>
        DataContext = Filters = new TrackAudioFilters { ShowAdvanced = Filters.ShowAdvanced };

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}