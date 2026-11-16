using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HandPegApp.Models;

namespace HandPegApp;

// ----- Playback bar controls -----
// Visible controls are chosen in the Views menu's Playback Bar column (PlaybackControls). Handled here: jumps (start, end, edit points),
// Play Backwards, Split, markers, Loop, Export Frame, Safe Margins and Mute. Play, frame steps and I-frame jumps belong to the transport.
public partial class MainWindow
{
    // Bar wiring, button dispatch, edit-point and loop navigation, frame export, safe margins and transport fitting.

    /// <summary>Groups the bar options by heading (Views menu Playback Bar column) and keeps player mute and safe margins in sync.</summary>
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

    /// <summary>Span of the clip selected on the timeline in milliseconds; null when none.</summary>
    private (double From, double To)? SelectedStretch() =>
        _selectedClip is Layer layer && _viewModel.ClipOf(layer) is { } clip ? (clip.Start * 1000, clip.End * 1000) : null;

    /// <summary>
    /// Moves the playhead to the nearest edit point in the given direction: any clip start or end on any track, or the
    /// sequence bounds (20 ms dead zone around the playhead).
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
    /// During playback with Loop on, wraps to the selected clip's start within 15 ms of its end. Returns true when it seeked.
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
    /// Current Loop wrap (From, To) in seconds: the selected clip's end to start, else sequence end (+Infinity) to 0, so the engine
    /// pre-rolls the cuts before the wrap. Both NaN when Loop is off.
    /// </summary>
    private (double From, double To) LoopWrap()
    {
        if (!_viewModel.LoopPlayback)
            return (double.NaN, double.NaN);
        return SelectedStretch() is { } stretch ? (stretch.To / 1000.0, stretch.From / 1000.0) : (double.PositiveInfinity, 0);
    }

    /// <summary>End of playback: with Loop on, restarts from the selected clip's start (or 0).</summary>
    private void OnPlaybackEnded()
    {
        if (!_viewModel.LoopPlayback || _gpu is null)
            return;

        var from = SelectedStretch() is { } stretch ? (long)stretch.From : 0;
        _viewModel.PositionMs = from;
        PlayerSetPause(false);
    }

    /// <summary>Saves the displayed composite frame as a PNG in the output folder (Pictures as fallback), with a unique name.</summary>
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
    /// Positions action-safe (90%) and title-safe (80%) outlines over the displayed picture: the output frame, or the source
    /// picture while Interactive Crop or a Draw Target is shown, letterboxed as the engine fits it.
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

    // ----- Transport width fitting -----

    /// <summary>
    /// Gives the centre controls the width left by both ends: the left end needs only its Views button (panes, I-frame options,
    /// bar contents), the right end all its controls. Centre controls that do not fit overflow into its "more" menu (OverflowStrip);
    /// nothing wraps. Called by PlaybackRow on every measure with the offered width.
    /// </summary>
    private void FitTransport(double width)
    {
        // Right-end pills expand over the centre buttons without reflowing them; clicking outside an open pill closes it.
        TransportRight.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var right = Math.Ceiling(TransportRight.DesiredSize.Width);
        TransportRow.ColumnDefinitions[2].MinWidth = right;
        UpdateTransportMinWidth();
        TransportCenter.MaxWidth = Math.Max(width - TransportRow.Margin.Left - TransportRow.Margin.Right - right - TransportRow.ColumnDefinitions[0].MinWidth, 60);
    }
}

/// <summary>
/// Playback bar row that fits its centre (<see cref="Fitting"/>) to the offered measure width before measuring. Using its own
/// ActualWidth instead would never shrink (an over-wide row is clipped at its own width), so the right end got cut off in narrow panes.
/// </summary>
public sealed class PlaybackRow : Grid
{
    /// <summary>Receives the offered row width before children are measured.</summary>
    public Action<double>? Fitting { get; set; }

    protected override Size MeasureOverride(Size constraint)
    {
        if (!double.IsInfinity(constraint.Width))
            Fitting?.Invoke(constraint.Width);
        return base.MeasureOverride(constraint);
    }
}
