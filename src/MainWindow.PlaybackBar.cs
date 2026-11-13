using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HandPegApp.Models;

namespace HandPegApp;

// ----- The playback bar's controls -----
// What the bar shows is chosen in the Views menu's Playback Bar column (PlaybackControls; 5.4.4: the bar's own settings button is retired). Most of its controls are
// handled here: jumps (start, end, edit points), Play Backwards, the start and stop points, Play Start to Stop,
// Loop, markers, Export Frame, Safe Margins and Mute. Play, the steps and the I-frame jumps are the transport's own.
public partial class MainWindow
{
    // Play Start to Stop: the stretch being played, in milliseconds, while it is.

    /// <summary>Lists the bar's options under their headings (the Views menu's Playback Bar column), and keeps the player's mute and the safe margins in step.</summary>
    private void InitializePlaybackBar()
    {
        var options = CollectionViewSource.GetDefaultView(_viewModel.PlaybackControls.Options);
        if (options.GroupDescriptions.Count == 0)
            options.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PlaybackControlOption.Group)));

        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModels.MainViewModel.IsPlayerMuted):
                    if (_gpu is { } gpu)
                        gpu.Muted = _viewModel.IsPlayerMuted;
                    break;
                case nameof(ViewModels.MainViewModel.ShowSafeMargins) or nameof(ViewModels.MainViewModel.IsInteractiveCropActive)
                    or nameof(ViewModels.MainViewModel.DrawTargetLayer) or nameof(ViewModels.MainViewModel.FrameWidth)
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

    /// <summary>The stretch of the clip selected on the timeline, in milliseconds (what 4.2.0s selected cut segment was); null for none.</summary>
    private (double From, double To)? SelectedStretch() =>
        _selectedClip is Layer layer && _viewModel.ClipOf(layer) is { } clip ? (clip.Start * 1000, clip.End * 1000) : null;

    /// <summary>
    /// Moves the playhead to the nearest edit point that way: the start or end of a clip (every track's alike, 5.2-AuditB),
    /// or of the sequence.
    /// </summary>
    private void GoToEditPoint(bool forward)
    {
        var sequence = _viewModel.DurationMs / 1000;
        var points = new List<double> { 0, sequence };
        foreach (var layer in _viewModel.Layers.Where(l => l.HasTiming && !l.IsDeleted))
        {
            var (from, to) = layer.GetSpan(sequence);
            points.AddRange([from, to]);
        }

        var at = _viewModel.PositionMs / 1000;
        double? target = forward ? points.Where(p => p > at + 0.02).DefaultIfEmpty(double.NaN).Min() : points.Where(p => p < at - 0.02).DefaultIfEmpty(double.NaN).Max();
        if (target is { } seconds && !double.IsNaN(seconds))
            _viewModel.PositionMs = Math.Clamp(seconds, 0, sequence) * 1000;
    }

    /// <summary>
    /// While playing: Loop goes back to the start of the selected clip. True when the position was dealt with.
    /// </summary>
    private bool KeepPlaybackInRange(long timeMs)
    {
        if (_viewModel.LoopPlayback && SelectedStretch() is { } stretch && timeMs >= stretch.To - 15)
        {
            PlayerSeek((long)stretch.From);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Where Loop takes playback back from and to now, in seconds (the selected clip, else the sequence's
    /// end to its start): the engine makes the cuts there ready before the wrap. NaN to: Loop off.
    /// </summary>
    private (double From, double To) LoopWrap()
    {
        if (!_viewModel.LoopPlayback)
            return (double.NaN, double.NaN);
        return SelectedStretch() is { } stretch ? (stretch.To / 1000.0, stretch.From / 1000.0) : (double.PositiveInfinity, 0);
    }

    /// <summary>Playback reached the end of what is open: with Loop on, it starts again from the beginning.</summary>
    private void OnPlaybackEnded()
    {
        if (!_viewModel.LoopPlayback || _gpu is null)
            return;

        var from = SelectedStretch() is { } stretch ? (long)stretch.From : 0;
        _viewModel.PositionMs = from;
        PlayerSetPause(false);
    }

    /// <summary>Saves the frame on screen, as the player shows it (as the layout makes it), as a PNG in the output's folder.</summary>
    private void ExportFrame()
    {
        if (_gpu is not { } gpu)
            return;

        var folder = Path.GetDirectoryName(_viewModel.DestinationPath.Trim().Trim('"'));
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var time = TimeSpan.FromMilliseconds(_viewModel.PositionMs);
        var stem = $"{Path.GetFileNameWithoutExtension(_viewModel.LocalMediaPath)}_{(int)time.TotalHours:00}-{time.Minutes:00}-{time.Seconds:00}-{time.Milliseconds:000}";
        var path = Path.Combine(folder, stem + ".png");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}).png");

        GpuSaveFrame(gpu, path);
    }

    // ----- Safe margins -----

    private void SafeMarginsCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceSafeMargins();

    /// <summary>
    /// Outlines the action-safe (90%) and title-safe (80%) areas of the picture as the player shows it: the output
    /// frame, or while Interactive Crop or a Draw Target shows a clip's own file that file's picture, fitted into the player
    /// as the engine fits it.
    /// </summary>
    private void PlaceSafeMargins()
    {
        var shown = _viewModel.ShowSafeMargins && _viewModel.HasSource;
        SafeMarginsCanvas.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        var (width, height) = (SafeMarginsCanvas.ActualWidth, SafeMarginsCanvas.ActualHeight);
        if (!shown || width <= 0 || height <= 0)
            return;

        var (pictureWidth, pictureHeight) = CurrentOverlayMode == OverlayMode.None && _viewModel.FrameWidth > 0 && _viewModel.FrameHeight > 0
            ? ((double)_viewModel.FrameWidth, (double)_viewModel.FrameHeight)
            : ShownPictureSize() is { Width: > 0, Height: > 0 } source ? (source.Width, source.Height) : (16.0, 9.0);
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
    /// button (its menu holds the panes, the timeline's I-frame checkboxes and what the bar shows), the right end all of its controls. What the
    /// middle has no room for is left out of it and listed in its "more" menu (see OverflowStrip); nothing is ever wrapped
    /// onto a second line.
    /// Asked by the row each time it is measured, with the width it is offered (see PlaybackRow).
    /// </summary>
    private void FitTransport(double width)
    {
        // (The right end's pills open over the middle's buttons and never move them: a click off an open pill closes it, 5.4.4.)
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
