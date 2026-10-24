using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HandPegApp.Models;

namespace HandPegApp;

// ----- The playback bar's controls -----
// What the bar shows is chosen with its settings button (PlaybackControls, per mode). Most of its controls are
// handled here: jumps (start, end, edit points), Play Backwards, the start and stop points, Play Start to Stop,
// Loop, markers, Export Frame, Safe Margins and Mute. Play, the steps and the I-frame jumps are the transport's own.
public partial class MainWindow
{
    // Play Start to Stop: the stretch being played, in milliseconds, while it is.
    private double? _playUntilMs;
    private double _playFromMs;

    /// <summary>Lists the bar's options under their headings in its settings, and keeps the player's mute and the safe margins in step.</summary>
    private void InitializePlaybackBar()
    {
        var options = CollectionViewSource.GetDefaultView(_viewModel.PlaybackControls.Options);
        if (options.GroupDescriptions.Count == 0)
            options.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PlaybackControlOption.Group)));

        // Encoder Mode's list leaves out what its bar can never show (Live Preview, Render Preview, Split).
        options.Filter = item => item is PlaybackControlOption { EditorOnly: false } || _viewModel.IsEditorMode;

        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModels.MainViewModel.IsEditorMode):
                    options.Refresh();
                    break;
                case nameof(ViewModels.MainViewModel.IsPlayerMuted):
                    _mpv?.SetMute(_viewModel.IsPlayerMuted);
                    break;
                case nameof(ViewModels.MainViewModel.ShowSafeMargins) or nameof(ViewModels.MainViewModel.LivePreview) or nameof(ViewModels.MainViewModel.FrameWidth)
                    or nameof(ViewModels.MainViewModel.FrameHeight) or nameof(ViewModels.MainViewModel.SourceWidth) or nameof(ViewModels.MainViewModel.SourceHeight)
                    or nameof(ViewModels.MainViewModel.HasSource):
                    PlaceSafeMargins();
                    break;
            }
        };
    }

    private void PlaybackControl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || !_viewModel.HasSource)
            return;

        switch (id)
        {
            case "JumpStart":
                _viewModel.PositionMs = 0;
                break;
            case "JumpEnd":
                _viewModel.PositionMs = _viewModel.DurationMs;
                break;
            case "PrevEdit":
                GoToEditPoint(forward: false);
                break;
            case "NextEdit":
                GoToEditPoint(forward: true);
                break;
            case "PlayReverse":
                Shuttle(-1);
                break;
            case "GoToIn" when InPointMs() is { } start:
                _viewModel.PositionMs = start;
                break;
            case "GoToIn":
                _viewModel.StatusText = "There is no start point: set one with [ (I), or select a cut segment.";
                break;
            case "GoToOut" when SelectedSegment() is { } segment:
                _viewModel.PositionMs = segment.End.TotalMilliseconds;
                break;
            case "GoToOut":
                _viewModel.StatusText = "Select a cut segment (on the timeline or in the Cut Segments pane) to go to its end.";
                break;
            case "PlayInOut":
                PlayStartToStop();
                break;
            case "Split":
                SplitSelectedClip();
                break;
            case "AddMarker":
                _viewModel.AddMarker();
                break;
            case "PrevMarker":
                _viewModel.GoToMarker(forward: false);
                break;
            case "NextMarker":
                _viewModel.GoToMarker(forward: true);
                break;
            case "ExportFrame":
                ExportFrame();
                break;
        }
    }

    /// <summary>The cut segment selected: on the timeline, or else in the Cut Segments pane.</summary>
    private CutSegment? SelectedSegment() => _selectedClip as CutSegment ?? _viewModel.SelectedSegment;

    /// <summary>The start point being set, or the start of the selected cut segment, in milliseconds.</summary>
    private double? InPointMs() => _viewModel.PendingStartMs ?? SelectedSegment()?.Start.TotalMilliseconds;

    /// <summary>
    /// Moves the playhead to the nearest edit point that way: the start or end of a clip of the main video, of a
    /// layer, of a cut segment, or of the sequence.
    /// </summary>
    private void GoToEditPoint(bool forward)
    {
        var sequence = _viewModel.DurationMs / 1000;
        var points = new List<double> { 0, sequence };
        foreach (var clip in _viewModel.GetMainClips())
            points.AddRange([clip.Start, clip.End]);
        foreach (var layer in _viewModel.Layers.Where(l => l.HasTiming && !l.IsDeleted))
        {
            var (from, to) = layer.GetSpan(sequence);
            points.AddRange([from, to]);
        }

        foreach (var segment in _viewModel.Segments)
            points.AddRange([segment.Start.TotalSeconds, segment.End.TotalSeconds]);

        var at = _viewModel.PositionMs / 1000;
        double? target = forward ? points.Where(p => p > at + 0.02).DefaultIfEmpty(double.NaN).Min() : points.Where(p => p < at - 0.02).DefaultIfEmpty(double.NaN).Max();
        if (target is { } seconds && !double.IsNaN(seconds))
            _viewModel.PositionMs = Math.Clamp(seconds, 0, sequence) * 1000;
    }

    /// <summary>Plays the selected cut segment, or from the start point being set to the playhead, and stops (or, looping, starts again) at its end.</summary>
    private void PlayStartToStop()
    {
        (double From, double To)? range = SelectedSegment() is { } segment
            ? (segment.Start.TotalMilliseconds, segment.End.TotalMilliseconds)
            : _viewModel.PendingStartMs is { } start && _viewModel.PositionMs > start + 50 ? (start, _viewModel.PositionMs) : null;
        if (range is not { } stretch)
        {
            _viewModel.StatusText = "Select a cut segment to play, or set a start point ([) and move the playhead past it.";
            return;
        }

        (_playFromMs, _playUntilMs) = (stretch.From, stretch.To);
        _viewModel.PositionMs = stretch.From;
        if (!PlayerIsPlaying)
            PlayPause_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// While playing: Play Start to Stop stops at its end, and Loop goes back to the start (of the stretch played, of
    /// the selected cut segment, or of the sequence). True when the position was dealt with.
    /// </summary>
    private bool KeepPlaybackInRange(long timeMs)
    {
        if (_playUntilMs is { } until)
        {
            if (timeMs < until - 15)
                return false;

            if (_viewModel.LoopPlayback)
            {
                PlayerSeek((long)_playFromMs);
                return true;
            }

            _playUntilMs = null;
            PlayerSetPause(true);
            _viewModel.StatusText = "Played to the stop point.";
            return true;
        }

        if (_viewModel.LoopPlayback && SelectedSegment() is { } segment && timeMs >= segment.End.TotalMilliseconds - 15)
        {
            PlayerSeek((long)segment.Start.TotalMilliseconds);
            return true;
        }

        return false;
    }

    /// <summary>Playback reached the end of what is open: with Loop on, it starts again from the beginning.</summary>
    private void OnPlaybackEnded()
    {
        if (!_viewModel.LoopPlayback || _mpv is not { } player)
            return;

        var from = _playUntilMs is not null ? (long)_playFromMs : SelectedSegment() is { } segment ? (long)segment.Start.TotalMilliseconds : 0;
        _viewModel.PositionMs = from;
        player.SetPause(false);
    }

    /// <summary>Saves the frame on screen, as the player shows it (with Live Preview, as the layout makes it), as a PNG in the output's folder.</summary>
    private void ExportFrame()
    {
        if (_mpv is not { } player)
            return;

        var folder = Path.GetDirectoryName(_viewModel.DestinationPath.Trim().Trim('"'));
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var time = TimeSpan.FromMilliseconds(_viewModel.PositionMs);
        var stem = $"{Path.GetFileNameWithoutExtension(_viewModel.LocalMediaPath)}_{(int)time.TotalHours:00}-{time.Minutes:00}-{time.Seconds:00}-{time.Milliseconds:000}";
        var path = Path.Combine(folder, stem + ".png");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}).png");

        player.SaveFrame(path);
        _viewModel.StatusText = $"Frame saved: {path}";
    }

    // ----- Safe margins -----

    private void SafeMarginsCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceSafeMargins();

    /// <summary>
    /// Outlines the action-safe (90%) and title-safe (80%) areas of the picture as the player shows it: the output
    /// frame with Live Preview on, the source with it off, fitted into the player as mpv fits it.
    /// </summary>
    private void PlaceSafeMargins()
    {
        var shown = _viewModel.ShowSafeMargins && _viewModel.HasSource;
        SafeMarginsCanvas.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        var (width, height) = (SafeMarginsCanvas.ActualWidth, SafeMarginsCanvas.ActualHeight);
        if (!shown || width <= 0 || height <= 0)
            return;

        var (pictureWidth, pictureHeight) = _viewModel.LivePreview && _viewModel.FrameWidth > 0 && _viewModel.FrameHeight > 0
            ? ((double)_viewModel.FrameWidth, (double)_viewModel.FrameHeight)
            : _viewModel.SourceWidth > 0 && _viewModel.SourceHeight > 0 ? (_viewModel.SourceWidth, _viewModel.SourceHeight) : (16.0, 9.0);
        var scale = Math.Min(width / pictureWidth, height / pictureHeight);
        var picture = new Rect((width - pictureWidth * scale) / 2, (height - pictureHeight * scale) / 2, pictureWidth * scale, pictureHeight * scale);

        foreach (var (outline, inset) in new[] { (ActionSafeRectangle, 0.05), (TitleSafeRectangle, 0.10) })
        {
            (outline.Width, outline.Height) = (picture.Width * (1 - 2 * inset), picture.Height * (1 - 2 * inset));
            Canvas.SetLeft(outline, picture.X + picture.Width * inset);
            Canvas.SetTop(outline, picture.Y + picture.Height * inset);
        }
    }

    // ----- Room for the controls -----

    /// <summary>
    /// The controls in the middle of the bar get the room its two ends do not need: the left end needs only its Views
    /// button (its checkboxes fold into that menu), the right end all of its controls. What the middle has no room
    /// for is left out of it and listed in its "more" menu (see OverflowStrip); nothing is ever wrapped onto a second line.
    /// Asked by the row each time it is measured, with the width it is offered (see PlaybackRow).
    /// </summary>
    private void FitTransport(double width)
    {
        TransportRight.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var right = Math.Ceiling(TransportRight.DesiredSize.Width);
        TransportRow.ColumnDefinitions[2].MinWidth = right;
        UpdateTransportMinWidth();
        TransportCenter.MaxWidth = Math.Max(width - TransportRow.Margin.Left - TransportRow.Margin.Right - right - TransportRow.ColumnDefinitions[0].MinWidth, 60);
    }
}

/// <summary>
/// The playback bar's row: each time it is measured it first has its middle fitted (<see cref="Fitting"/>) to the width
/// it is offered. Not to its own width: a row too wide for its pane is laid out at its own width and cut, so its own
/// width never shrank, and the middle, sized from it, never gave way (the bar's right end was cut off in a narrow pane).
/// </summary>
public sealed class PlaybackRow : Grid
{
    /// <summary>Told the width offered to the row, before it measures its parts.</summary>
    public Action<double>? Fitting { get; set; }

    protected override Size MeasureOverride(Size constraint)
    {
        if (!double.IsInfinity(constraint.Width))
            Fitting?.Invoke(constraint.Width);
        return base.MeasureOverride(constraint);
    }
}
