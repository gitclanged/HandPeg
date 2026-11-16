using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>Split / Trim by Exact Timecode action.</summary>
public enum TimecodeEdit
{
    Split,
    TrimStart,
    TrimEnd,
}

/// <summary>
/// Split / Trim by Exact Timecode (row context menu): accepts HH:MM:SS:FF or HH:MM:SS.mmm in Sequence time, offers adjacent I-frames of
/// the clip's file as links, then splits or trims start/end there. The caller performs the edit (<see cref="Edit"/> at
/// <see cref="Seconds"/>).
/// </summary>
public partial class ManualCutWindow : Window
{
    private readonly double _start;
    private readonly double _end;
    private readonly double _frameRate;
    private readonly IReadOnlyList<double> _iFrames;

    /// <param name="clipName">Clip name for the header line.</param>
    /// <param name="start">Clip start, Sequence seconds.</param>
    /// <param name="end">Clip end, Sequence seconds (&lt;= <paramref name="start"/> means open-ended).</param>
    /// <param name="frameRate">File frame rate, for frame timecodes and half-frame tolerance.</param>
    /// <param name="iFrames">File I-frames in Sequence time, ascending; empty when unknown.</param>
    /// <param name="at">Initial time (playhead).</param>
    public ManualCutWindow(string clipName, double start, double end, double frameRate, IReadOnlyList<double> iFrames, double at)
    {
        _iFrames = iFrames;
        InitializeComponent();
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);
        (_start, _end, _frameRate) = (start, end, frameRate > 0 ? frameRate : 30);

        ClipText.Text = end > start
            ? $"{clipName}: {TimeDisplay.Format(start)} to {TimeDisplay.Format(end)}"
            : $"{clipName}: from {TimeDisplay.Format(start)}";

        // Use the time format from Settings.
        FormatText.Text = (TimeDisplay.UseFrames
            ? $"Times are on the timeline, as HH:MM:SS:FF; FF is the frame within the second: 00 to {Timecode.MaxFrame(_frameRate):00} "
              + $"at {_frameRate.ToString("0.###", CultureInfo.InvariantCulture)} fps."
            : "Times are on the timeline, as HH:MM:SS.mmm, for example 00:01:30.500. Frame timecodes (HH:MM:SS:FF) can be switched on in Settings, under Timeline.")
            + (iFrames.Count == 0 ? " This clip's file has no I-frame index yet, so no I-frames are offered." : "");

        TimeBox.Text = TimeDisplay.Format(at);
        TimeBox.Focus();
        TimeBox.SelectAll();
    }

    public TimecodeEdit Edit { get; private set; }

    /// <summary>Chosen time, Sequence seconds.</summary>
    public double Seconds { get; private set; }

    /// <summary>Updates the adjacent I-frame suggestions for the typed time.</summary>
    private void TimeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged fires during construction, before the frame rate is set.
        if (_frameRate <= 0)
            return;

        double? floor = null;
        double? ceiling = null;
        if (_iFrames.Count > 0 && TimeDisplay.TryParse(TimeBox.Text, out var seconds, out _))
        {
            // Half-frame tolerance: timecode resolution cannot address an I-frame more precisely.
            var tolerance = 0.5 / _frameRate;
            for (var i = _iFrames.Count - 1; i >= 0 && floor is null; i--)
                if (_iFrames[i] <= seconds + tolerance)
                    floor = _iFrames[i];
            for (var i = 0; i < _iFrames.Count && ceiling is null; i++)
                if (_iFrames[i] >= seconds - tolerance)
                    ceiling = _iFrames[i];

            // The time is an I-frame: show a single suggestion.
            if (floor is not null && ceiling is not null && Math.Abs(floor.Value - ceiling.Value) < tolerance)
                ceiling = null;
        }

        SetSuggestion(FloorLink, floor);
        SetSuggestion(CeilingLink, ceiling);
        Suggestions.Visibility = floor is null && ceiling is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetSuggestion(Button link, double? iFrame)
    {
        link.Visibility = iFrame is null ? Visibility.Collapsed : Visibility.Visible;
        link.Content = iFrame is { } seconds ? TimeDisplay.Format(seconds) : "";
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e) => TimeBox.Text = (string)((Button)sender).Content;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!TimeDisplay.TryParse(TimeBox.Text, out var seconds, out var error))
        {
            ErrorText.Text = error;
            return;
        }

        // Must be strictly inside the clip; an edit at either end would be empty.
        var slack = 0.5 / _frameRate;
        if (seconds <= _start + slack || (_end > _start && seconds >= _end - slack))
        {
            ErrorText.Text = _end > _start
                ? $"The time must fall inside the clip, after {TimeDisplay.Format(_start)} and before {TimeDisplay.Format(_end)}."
                : $"The time must fall after the clip's start, {TimeDisplay.Format(_start)}.";
            return;
        }

        Seconds = seconds;
        Edit = TrimStartChoice.IsChecked == true ? TimecodeEdit.TrimStart : TrimEndChoice.IsChecked == true ? TimecodeEdit.TrimEnd : TimecodeEdit.Split;
        DialogResult = true;
    }
}
