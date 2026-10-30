using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Player: the few places that drive the GPU preview engine, J/K/L, scrubbing, solo and waveform seeks -----
public partial class MainWindow
{
    // ----- The player -----
    // The player is the GPU preview engine (MainWindow.GpuPreview.cs; the only one since 5.0-Verify, libmpv is gone): the
    // compositor draws the sequence's scene plan into the video surface, the WASAPI mixer plays its sound, and the mixer's
    // clock is the playhead. Whatever moves or changes the player goes through the members here (PlayerSeek, PlayerSetPause,
    // PlayerIsPlaying, TakePlayerPosition, ApplyLiveFilter and ScheduleLiveFilter, OpenInPlayer), which keep their names
    // from the libmpv days so that their callers need not know: an edit is a new scene plan, a jump an exact seek.

    private bool _shellDrawn;

    // What to open once the engine has started: the moment the sequence was loaded at (or a reload brings it back to), and
    // whether paused.
    private (long StartMs, bool Paused)? _pendingMedia;

    private bool PlayerIsPlaying => _gpu is { IsPlaying: true };

    // Whether the paused player was last sent to an exact moment by the user, which the playhead then keeps.
    private bool _pausedOnUsersMoment;

    private void PlayerSetPause(bool paused)
    {
        if (_gpu is { } gpu)
            GpuSetPaused(gpu, paused);
    }

    /// <summary>Sends the player to a moment: always the exact frame, answered at once (Instant Seek shows the nearest one meanwhile).</summary>
    private void PlayerSeek(long positionMs)
    {
        if (_gpu is { } gpu)
            GpuSeek(gpu, positionMs);

        // The engine starting (or starting again after a fault): it opens where the user went meanwhile.
        else if (_pendingMedia is { } pending)
            _pendingMedia = (positionMs, pending.Paused);
    }

    /// <summary>
    /// The video surface (a native window, which nothing of WPF's can be drawn over) is shown only while there is a
    /// video and a player to draw it. Otherwise it is hidden (it keeps its window, and the engine its swap chain), and
    /// what lies under it in the pane shows: the drop hint while nothing is loaded, the engine notice while the player
    /// is stopped (see OnGpuFaulted). Being content of the pane, they belong to whichever window holds the player.
    /// </summary>
    private void UpdateVideoVisibility()
    {
        VideoView.Visibility = _viewModel.HasSource && _gpu is not null ? Visibility.Visible : Visibility.Hidden;
        DropHint.Visibility = _viewModel.HasSource ? Visibility.Collapsed : Visibility.Visible;
    }

    // Whether anything has asked for the player yet: a video to open, a file dragged over the window, the Source box or
    // Browse being used. Until then the engine is not started (its worker, its swap chain and the audio device are well
    // over a hundred megabytes of an idle window).
    private bool _playerWanted;

    /// <summary>Starts the player ahead of need: something is about to be opened (a file is being dragged in, a path typed).</summary>
    private void WarmUpPlayer()
    {
        if (_gpu is not null || _playerWanted)
            return;

        _playerWanted = true;
        EnsureGpuEngine();
    }

    private readonly CancellationTokenSource _windowClosing = new();

    /// <summary>
    /// Opens a newly loaded source in the player, paused on its first frame (a project or a file opened is looked at
    /// before it is played, Pass 4.1C).
    /// </summary>
    private void PlayMedia(string path)
    {
        if (!File.Exists(path))
        {
            _viewModel.StatusText = $"The file to play is missing: {path}";
            return;
        }

        StopShuttle();
        OpenInPlayer(0, paused: true);

        // A new video is seen whole on the time bars.
        FitTimeline(announce: false);
    }

    /// <summary>The sequence into the player at a moment, paused or playing; given to the engine once it has started when it has not yet.</summary>
    private void OpenInPlayer(long startMs, bool paused)
    {
        if (_gpu is not null)
        {
            GpuOpen(startMs, paused);
            return;
        }

        _pendingMedia = (startMs, paused);
        EnsureGpuEngine();
    }

    /// <summary>
    /// Reload Player: for when the player has locked up or stopped (see OnGpuFaulted). The engine is replaced with a new
    /// one (a new worker, audio device and viewport, on the graphics card again after a fall back to the processor), and
    /// the sequence is opened again where it was.
    /// </summary>
    private void ReloadPlayer_Click(object sender, RoutedEventArgs e)
    {
        // An engine that gave up is let start again whatever is loaded (the notice says Reload Player starts it).
        if (!_viewModel.HasSource || !File.Exists(_viewModel.LocalMediaPath))
        {
            if (_gpu is null && !_startingGpu)
            {
                ForgetGpuFailures();
                EnsureGpuEngine();
            }

            _viewModel.StatusText = _viewModel.HasSource
                ? "The main video's file is not there (an offline drive?): the player starts again once it is."
                : "There is no video loaded to reload.";
            return;
        }

        var (resumeAt, wasPlaying) = ((long)_viewModel.PositionMs, _viewModel.IsPlaying);
        _viewModel.StatusText = "Reloading the player...";
        _isScrubbing = false;
        StopShuttle();
        CloseGpuEngine();
        ForgetGpuFailures();
        _viewModel.IsPlaying = false;
        FollowPlayerByFrame(false);
        UpdateVideoVisibility();
        _pendingMedia = (resumeAt, !wasPlaying);
        EnsureGpuEngine();
    }

    // ----- Live Preview -----

    /// <summary>Something the picture or the sound is made from changed: the engine gets the sequence again within a frame.</summary>
    private void ScheduleLiveFilter() => ScheduleGpuState();

    private void ApplyLiveFilter() => ScheduleGpuState();

    // ----- Following the player -----
    // While it plays, the position is read once per screen refresh (CompositionTarget.Rendering): one look per frame
    // drawn, and nothing queued behind a busy UI thread. Paused, the refresh is not followed (it would keep WPF drawing
    // for nothing).

    private volatile bool _followingByFrame;

    private void FollowPlayerByFrame(bool on)
    {
        if (on == _followingByFrame)
            return;

        _followingByFrame = on;
        if (on)
        {
            CompositionTarget.Rendering += FollowPlayer_Rendering;
        }
        else
        {
            CompositionTarget.Rendering -= FollowPlayer_Rendering;

            // Where it was when it stopped being followed by frame.
            TakePlayerPosition();
        }
    }

    private void FollowPlayer_Rendering(object? sender, EventArgs e) => TakePlayerPosition();

    private void TakePlayerPosition() => TakeGpuPosition();

    // ----- One audio track on its own -----

    private void OnSoloTrackChanged()
    {
        if (_gpu is null || !_viewModel.HasSource)
            return;

        // A track played on its own is played without the layers' sound (the engine's program is built for it).
        ScheduleLiveFilter();

        // The button is a play button: choosing a track starts it, and letting go of the last one pauses.
        if (_viewModel.SoloedTracks.Count > 0 && !PlayerIsPlaying)
            PlayPause_Click(this, new RoutedEventArgs());
        else if (_viewModel.SoloedTracks.Count == 0 && PlayerIsPlaying)
            PlayerSetPause(true);
    }

    // ----- Seeking from a waveform -----
    // A track's waveform on the Audio tab covers the whole video from edge to edge, so a point on it is a time.

    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement waveform || _viewModel.DurationMs <= 0 || !waveform.CaptureMouse())
            return;

        // Handled, so the list the track's row is in does not take the mouse for itself and end the drag.
        e.Handled = true;
        SeekToWaveformPoint(waveform, e);
    }

    private void Waveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } waveform)
            SeekToWaveformPoint(waveform, e);
    }

    private void Waveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => (sender as UIElement)?.ReleaseMouseCapture();

    private void SeekToWaveformPoint(FrameworkElement waveform, MouseEventArgs e)
    {
        // Setting the position moves the timeline, which seeks the player. A track's row shows the time bars' view
        // (it holds one); the master mix shows the whole sequence.
        var x = e.GetPosition(waveform).X;
        if (waveform is Border { Child: Grid grid } && grid.Children.OfType<TimelineTrack>().Any())
            _viewModel.PositionMs = Math.Clamp(TimeAt(waveform, x) * 1000, 0, _viewModel.DurationMs);
        else if (waveform.ActualWidth > 0)
            _viewModel.PositionMs = Math.Clamp(x / waveform.ActualWidth, 0, 1) * _viewModel.DurationMs;
    }

    // ----- Playback / scrubbing -----

    // ----- J, K, L -----
    // L plays; pressed again it plays twice as fast, then four times. J does the same backwards (silent). K stops. The
    // engine's clock runs at the speed asked, either way (GpuShuttle).

    // 0 while not shuttling; otherwise the speed, negative for backwards.
    private int _shuttle;

    private void Shuttle(int direction)
    {
        if (_gpu is { } gpu && _viewModel.HasSource)
            GpuShuttle(gpu, direction);
    }

    /// <summary>Ends J/K/L shuttling, and puts the playback speed back to the one chosen in the speed box.</summary>
    private void StopShuttle()
    {
        if (_shuttle == 0)
            return;

        _shuttle = 0;
        if (_gpu is { } gpu)
            gpu.Rate = _viewModel.PlaybackRate;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        StopShuttle();
        if (!_viewModel.HasSource)
        {
            _viewModel.LoadSourceCommand.Execute(null);
        }
        else if (_gpu is { } gpu)
        {
            // At the end: Play starts again from the beginning. Paused by hand: a Play Start to Stop under way is over.
            if (!gpu.IsPlaying && gpu.Position >= gpu.SequenceSeconds - 0.001)
                PlayerSeek(0);
            if (gpu.IsPlaying)
                _playUntilMs = null;
            GpuSetPaused(gpu, gpu.IsPlaying);
        }
        else if (_gpuGaveUp)
        {
            _viewModel.StatusText = "The GPU preview is stopped: Reload Player starts it again.";
        }
        else
        {
            // Not started yet (or about to start again, see OnGpuFailed): it starts, and opens what is loaded.
            EnsureGpuEngine();
        }
    }

    private void OnPlayerTimeChanged(long timeMs)
    {
        // Don't fight the user for the thumb while they are dragging it.
        if (_isScrubbing)
            return;

        // Paused on a moment the user sent it to (a seek, a frame or key step, a click on the timeline), the playhead
        // stays exactly there (4.2D). Paused by Pause, the position it stopped at is still taken.
        if (PlayerIsPlaying)
            _pausedOnUsersMoment = false;
        else if (_pausedOnUsersMoment)
            return;

        _updatingFromPlayer = true;
        _viewModel.PositionMs = timeMs;
        _updatingFromPlayer = false;

        // While playing: Play Start to Stop and Loop kept to their stretch, and the engine told where Loop goes back to.
        if (PlayerIsPlaying)
        {
            if (_gpu is { } engine)
            {
                var (from, to) = LoopWrap();
                engine.SetLoop(from, to);
            }

            KeepPlaybackInRange(timeMs);
        }
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromPlayer || _gpu is null)
            return;

        // Not a move of the playhead (4.1.1): the value pushed down by a timeline that became shorter (the slider
        // coerces it to its new end; what the sequence does about it is RefreshSequence's), or a step of less than a
        // millisecond (a position given back in other units).
        if (e.NewValue == TimelineSlider.Maximum && e.OldValue > TimelineSlider.Maximum)
            return;
        if (!_isScrubbing && Math.Abs(e.NewValue - e.OldValue) < 1)
            return;

        // Magnetism: while dragging, a thumb that comes close enough to an I-frame is pulled onto it.
        // The drag continues from wherever the thumb is, so it sticks until the pointer has moved clear.
        var settings = AppSettings.Current;
        if (_isScrubbing && !_applyingMagnet && settings.SnapTimelineToIFrames
            && TimelineSlider.ActualWidth > 0 && _viewModel.DurationMs > 0
            && _viewModel.GetNearestIFrameMs(e.NewValue) is { } iFrame && iFrame != e.NewValue)
        {
            const double pixelsPerStep = 4;
            var reach = Math.Clamp(settings.TimelineMagnetism, 1, 5) * pixelsPerStep / TimelineSlider.ActualWidth * _viewModel.DurationMs;
            if (Math.Abs(iFrame - e.NewValue) <= reach)
            {
                _applyingMagnet = true;
                TimelineSlider.Value = iFrame;
                _applyingMagnet = false;
                return;
            }
        }

        // The engine answers every position of a drag, exactly (Instant Seek): nothing to pace.
        PlayerSeek((long)e.NewValue);
    }

    /// <summary>
    /// A press anywhere on the master timeline, not only on the playhead: the playhead goes to where the
    /// pointer is, and the press is then handed to it, so that it is held from that moment exactly as if it
    /// had been pressed itself. Holding and dragging scrubs; letting go ends it, by the same steps as any
    /// drag of the playhead (DragStarted, DragCompleted). The slider has usually moved the playhead to the
    /// pointer already by the time this runs; what it does not do by itself is take hold of it.
    /// </summary>
    private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TimelineSlider.Template?.FindName("PART_Track", TimelineSlider) is not Track { Thumb: { } thumb } track
            || thumb.IsMouseOver || thumb.IsDragging || TimelineSlider.Maximum <= TimelineSlider.Minimum)
        {
            return;
        }

        // The track works out a value from where its playhead was last laid out. The slider may just have moved
        // the playhead (it does, for a press beside it), so the track is laid out first: asked before that, it
        // would add the distance to the pointer a second time.
        TimelineSlider.UpdateLayout();
        var value = track.ValueFromPoint(e.GetPosition(track));
        if (!double.IsFinite(value))
            return;

        TimelineSlider.Value = Math.Clamp(value, TimelineSlider.Minimum, TimelineSlider.Maximum);

        // The playhead is laid out where it now is before it is pressed: it measures the drag from there.
        TimelineSlider.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent, Source = thumb });
        e.Handled = true;
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e) => _isScrubbing = true;

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isScrubbing = false;

        // Let go (and a click on the timeline is a press let go at once): the exact frame, straight away.
        PlayerSeek((long)TimelineSlider.Value);
    }
}
