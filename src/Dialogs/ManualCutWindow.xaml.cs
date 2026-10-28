using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>What Split / Trim by Exact Timecode is asked to do at its time.</summary>
public enum TimecodeEdit
{
    Split,
    TrimStart,
    TrimEnd,
}

/// <summary>
/// Split / Trim by Exact Timecode (a timeline row's right-click menu): a time typed as HH:MM:SS:FF (or HH:MM:SS.mmm) on
/// the sequence's clock, with the I-frames of the clip's file either side of it offered as links; then a split there, or
/// the clip's start or end trimmed to it. The caller carries the edit out (<see cref="Edit"/> at <see cref="Seconds"/>).
/// </summary>
public partial class ManualCutWindow : Window
{
    private readonly double _start;
    private readonly double _end;
    private readonly double _frameRate;
    private readonly IReadOnlyList<double> _iFrames;

    /// <param name="clipName">The clip's name, for the window's first line.</param>
    /// <param name="start">Where the clip starts on the sequence, in seconds.</param>
    /// <param name="end">Where it ends on the sequence (at or before <paramref name="start"/>: it runs to the end).</param>
    /// <param name="frameRate">Its file's frame rate, for frame timecodes and half a frame of slack.</param>
    /// <param name="iFrames">Its file's I-frames already placed on the sequence's clock, ascending; empty when unknown.</param>
    /// <param name="at">The time to start from: the playhead.</param>
    public ManualCutWindow(string clipName, double start, double end, double frameRate, IReadOnlyList<double> iFrames, double at)
    {
        _iFrames = iFrames;
        InitializeComponent();
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);
        (_start, _end, _frameRate) = (start, end, frameRate > 0 ? frameRate : 30);

        ClipText.Text = end > start
            ? $"{clipName}: {TimeDisplay.Format(start)} to {TimeDisplay.Format(end)}"
            : $"{clipName}: from {TimeDisplay.Format(start)}";

        // The dialog follows the time format chosen in the settings.
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

    /// <summary>The time chosen, in seconds on the sequence.</summary>
    public double Seconds { get; private set; }

    /// <summary>Shows the I-frames either side of the time being typed.</summary>
    private void TimeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Text is set while the window is still being built, before the frame rate is known.
        if (_frameRate <= 0)
            return;

        double? floor = null;
        double? ceiling = null;
        if (_iFrames.Count > 0 && TimeDisplay.TryParse(TimeBox.Text, out var seconds, out _))
        {
            // Half a frame of slack: a timecode cannot name an I-frame's timestamp more exactly than that.
            var tolerance = 0.5 / _frameRate;
            for (var i = _iFrames.Count - 1; i >= 0 && floor is null; i--)
                if (_iFrames[i] <= seconds + tolerance)
                    floor = _iFrames[i];
            for (var i = 0; i < _iFrames.Count && ceiling is null; i++)
                if (_iFrames[i] >= seconds - tolerance)
                    ceiling = _iFrames[i];

            // The time is itself an I-frame: one suggestion says it all.
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

        // Strictly inside the clip: a split or a trim at either end would leave nothing.
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
