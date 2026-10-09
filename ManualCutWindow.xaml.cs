using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Adds a cut segment from typed HH:MM:SS:FF timecodes.
/// </summary>
public partial class ManualCutWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly double _frameRate;
    private readonly bool _snap;

    public ManualCutWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _frameRate = viewModel.SourceFrameRate;
        _snap = viewModel.SnapToIFrames && viewModel.IFrames.Count > 0;

        // The dialog follows the time format chosen in the settings.
        FormatText.Text = TimeDisplay.UseFrames
            ? $"Enter times as HH:MM:SS:FF. FF is the frame within the second: 00 to {Timecode.MaxFrame(_frameRate):00} "
              + $"at this video's {_frameRate.ToString("0.###", CultureInfo.InvariantCulture)} fps."
            : "Enter times as HH:MM:SS.mmm, for example 00:01:30.500. Frame timecodes (HH:MM:SS:FF) can be switched on in Settings, under Timeline.";

        SnapText.Text = _snap
            ? "Snap cuts to I-frames is on. Click an I-frame to use it; otherwise Save moves the start back to the I-frame before it and the stop on to the I-frame after it."
            : viewModel.SnapToIFrames
                ? "Snap cuts to I-frames is on, but this video has no I-frame index yet: the times are used as typed."
                : "";

        // Start from what is already on screen: the pending start point if there is one, and the playhead.
        var position = viewModel.PositionMs / 1000;
        StartBox.Text = TimeDisplay.Format((viewModel.PendingStartMs ?? viewModel.PositionMs) / 1000);
        StopBox.Text = TimeDisplay.Format(position);

        StartBox.Focus();
        StartBox.SelectAll();
    }

    private void StartBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateSuggestions(StartBox, StartSuggestions, StartFloor, StartCeiling);

    private void StopBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateSuggestions(StopBox, StopSuggestions, StopFloor, StopCeiling);

    /// <summary>Shows the I-frames either side of the time being typed.</summary>
    private void UpdateSuggestions(TextBox box, Panel panel, Button floorLink, Button ceilingLink)
    {
        // Text is set while the window is still being built.
        if (_viewModel is null)
            return;

        double? floor = null;
        double? ceiling = null;
        if (_snap && TimeDisplay.TryParse(box.Text, out var seconds, out _))
        {
            var tolerance = 0.5 / _frameRate;
            floor = _viewModel.GetIFrameFloor(seconds, tolerance);
            ceiling = _viewModel.GetIFrameCeiling(seconds, tolerance);

            // The time is itself an I-frame: one suggestion says it all.
            if (floor is not null && ceiling is not null && Math.Abs(floor.Value - ceiling.Value) < tolerance)
                ceiling = null;
        }

        SetSuggestion(floorLink, floor);
        SetSuggestion(ceilingLink, ceiling);
        panel.Visibility = floor is null && ceiling is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetSuggestion(Button link, double? iFrame)
    {
        link.Visibility = iFrame is null ? Visibility.Collapsed : Visibility.Visible;
        link.Content = iFrame is { } seconds ? TimeDisplay.Format(seconds) : "";
    }

    private void StartSuggestion_Click(object sender, RoutedEventArgs e) => StartBox.Text = (string)((Button)sender).Content;

    private void StopSuggestion_Click(object sender, RoutedEventArgs e) => StopBox.Text = (string)((Button)sender).Content;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TimeDisplay.TryParse(StartBox.Text, out var start, out var startError))
        {
            ErrorText.Text = $"Start: {startError}";
            return;
        }

        if (!TimeDisplay.TryParse(StopBox.Text, out var stop, out var stopError))
        {
            ErrorText.Text = $"Stop: {stopError}";
            return;
        }

        if (_viewModel.AddManualSegment(start, stop) is { } error)
        {
            ErrorText.Text = error;
            return;
        }

        DialogResult = true;
    }
}
