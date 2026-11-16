using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Player transport: GPU engine entry points, J/K/L, scrubbing, solo and waveform seeks -----
public partial class MainWindow
{
    // ----- Player -----
    // The player is the GPU preview engine (MainWindow.GpuPreview.cs): the compositor renders the scene plan, the WASAPI mixer plays
    // audio, and the mixer clock is the playhead. All player changes go through these members (PlayerSeek, PlayerSetPause,
    // PlayerIsPlaying, TakePlayerPosition, OpenInPlayer) or ScheduleGpuState: an edit is a new scene plan, a jump an exact seek.

    private bool _shellDrawn;

    // Deferred open for when the engine finishes starting: start time (load or reload position) and paused state.
    private (long StartMs, bool Paused)? _pendingMedia;

    private bool PlayerIsPlaying => _gpu is { IsPlaying: true };

    // Paused player was last seeked explicitly; the playhead keeps that exact time instead of the engine's.
    private bool _pausedOnUsersMoment;

    private void PlayerSetPause(bool paused)
    {
        if (_gpu is { } gpu)
            GpuSetPaused(gpu, paused);
    }

    /// <summary>Exact-frame seek (Instant Seek shows the nearest frame meanwhile); updates the pending open while the engine starts.</summary>
    private void PlayerSeek(long positionMs)
    {
        if (_gpu is { } gpu)
            GpuSeek(gpu, positionMs);

        // Engine (re)starting: retarget the pending open to the new position.
        else if (_pendingMedia is { } pending)
            _pendingMedia = (positionMs, pending.Paused);
    }

    /// <summary>
    /// Shows the native video surface (which WPF cannot draw over) only with media and a running engine; otherwise hides it (HWND and swap
    /// chain kept) to reveal the pane content beneath: drop hint when empty, engine notice when stopped (OnGpuFaulted). That content moves
    /// with whichever window hosts the player.
    /// </summary>
    private void UpdateVideoVisibility()
    {
        VideoView.Visibility = _viewModel.HasSource && _gpu is not null ? Visibility.Visible : Visibility.Hidden;
        DropHint.Visibility = _viewModel.HasSource ? Visibility.Collapsed : Visibility.Visible;
    }

    // Set once the player is needed (media to open, file dragged over the window, Source box or Browse used). The engine stays unstarted
    // until then: its worker, swap chain and audio device cost over 100 MB of idle memory (Law 2).
    private bool _playerWanted;

    /// <summary>Pre-starts the engine when an open is imminent (file drag-in, typed path).</summary>
    private void WarmUpPlayer()
    {
        if (_gpu is not null || _playerWanted)
            return;

        _playerWanted = true;
        EnsureGpuEngine();
    }

    private readonly CancellationTokenSource _windowClosing = new();

    /// <summary>
    /// Opens a newly loaded source paused on its first frame and fits the timeline.
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

        // Fit the timeline to the new media.
        FitTimeline(announce: false);
    }

    /// <summary>Opens the sequence at <paramref name="startMs"/>, paused or playing; deferred until the engine has started.</summary>
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
    /// Reload Player (for a hung or stopped player, see OnGpuFaulted): replaces the engine (new worker, audio device and viewport, back
    /// on hardware after a WARP fallback) and reopens the sequence at the same position and play state.
    /// </summary>
    private void ReloadPlayer_Click(object sender, RoutedEventArgs e)
    {
        // A gave-up engine restarts even with no usable media (the notice promises Reload Player restarts it).
        if (!_viewModel.HasSource || !File.Exists(_viewModel.LocalMediaPath))
        {
            if (_gpu is null && !_startingGpu)
            {
                ForgetGpuFailures();
                EnsureGpuEngine();
            }

            _viewModel.StatusText = _viewModel.HasSource
                ? "The file you opened is not there (an offline drive?): the player starts again once it is."
                : "There is no video loaded to reload.";
            return;
        }

        var (resumeAt, wasPlaying) = ((long)_viewModel.PositionMs, _viewModel.IsPlaying);
        _viewModel.StatusText = "Reloading the player...";
        _isScrubbing = false;
        StopShuttle();
        ReleaseGpuEngine();
        ForgetGpuFailures();
        _viewModel.IsPlaying = false;
        FollowPlayerByFrame(false);
        UpdateVideoVisibility();
        _pendingMedia = (resumeAt, !wasPlaying);
        EnsureGpuEngine();
    }

    // ----- Playhead follow -----
    // While playing, the position is sampled once per display refresh (CompositionTarget.Rendering): one read per frame, nothing queued
    // behind a busy UI thread. Paused, the hook is removed so WPF does not keep rendering for nothing.

    // UI thread only (guards the CompositionTarget.Rendering subscription).
    private bool _followingByFrame;

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

            // Take the final position at unhook.
            TakePlayerPosition();
        }
    }

    private void FollowPlayer_Rendering(object? sender, EventArgs e) => TakePlayerPosition();

    private void TakePlayerPosition() => TakeGpuPosition();

    // ----- Solo audio track -----

    private void OnSoloTrackChanged()
    {
        if (_gpu is null || !_viewModel.HasSource)
            return;

        // Soloed tracks play without layer audio; the engine's program is rebuilt for it.
        ScheduleGpuState();

        // Solo acts as play: the first solo starts playback; clearing the last one pauses.
        if (_viewModel.SoloedTracks.Count > 0 && !PlayerIsPlaying)
            PlayPause_Click(this, new RoutedEventArgs());
        else if (_viewModel.SoloedTracks.Count == 0 && PlayerIsPlaying)
            PlayerSetPause(true);
    }

    // ----- Waveform seek -----
    // Audio tab waveforms span the full duration edge to edge, so x maps linearly to time.

    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement waveform || _viewModel.DurationMs <= 0 || !waveform.CaptureMouse())
            return;

        // Mark handled so the containing list does not steal capture and end the drag.
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
        // Setting PositionMs moves the timeline, which seeks the player (the master mix waveform spans the sequence).
        var x = e.GetPosition(waveform).X;
        if (waveform.ActualWidth > 0)
            _viewModel.PositionMs = Math.Clamp(x / waveform.ActualWidth, 0, 1) * _viewModel.DurationMs;
    }

    // ----- Playback / scrubbing -----

    // ----- J/K/L shuttle -----
    // L plays, repeated L doubles to 2x then 4x; J mirrors in reverse (silent); K stops. The engine clock runs at the requested rate
    // (GpuShuttle).

    // Shuttle speed: 0 when inactive, negative for reverse.
    private int _shuttle;

    private void Shuttle(int direction)
    {
        if (_gpu is { } gpu && _viewModel.HasSource)
            GpuShuttle(gpu, direction);
    }

    /// <summary>Ends shuttling and restores the selected playback rate.</summary>
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
            // At the end: Play restarts from 0.
            if (!gpu.IsPlaying && gpu.Position >= gpu.SequenceSeconds - 0.001)
                PlayerSeek(0);
            GpuSetPaused(gpu, gpu.IsPlaying);
        }
        else if (_gpuGaveUp)
        {
            _viewModel.StatusText = "The GPU preview is stopped: Reload Player starts it again.";
        }
        else
        {
            // Engine not started (or awaiting retry, see OnGpuFailed): start it and open the loaded media.
            EnsureGpuEngine();
        }
    }

    private void OnPlayerTimeChanged(long timeMs)
    {
        // Ignore engine time while a scrub holds the playhead (overview thumb, ruler).
        if (IsScrubHeld)
            return;

        // Paused after an explicit seek (seek, frame/key step, timeline click): keep the exact requested time. Paused via Pause: take
        // the engine's stop position.
        if (PlayerIsPlaying)
            _pausedOnUsersMoment = false;
        else if (_pausedOnUsersMoment)
            return;

        _updatingFromPlayer = true;
        _viewModel.PositionMs = timeMs;
        _updatingFromPlayer = false;

        // While playing: enforce Play Start to Stop and Loop ranges, and update the engine's loop points.
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

    /// <summary>
    /// Overview Scrubber value changed (binding is UpdateSourceTrigger=Explicit): clicks beside the thumb or keys set the playhead
    /// immediately (FollowPlayheadInPlayer seeks); thumb drags scrub via ScrubPlayheadTo (lines and player now, playhead once per frame).
    /// </summary>
    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromPlayer)
            return;

        // Ignore non-playhead moves: coercion to a shorter Maximum (RefreshSequence handles the sequence side) and sub-millisecond
        // round-trips from unit conversion.
        if (e.NewValue == TimelineSlider.Maximum && e.OldValue > TimelineSlider.Maximum)
            return;
        if (!_isScrubbing)
        {
            // Skip binding echoes and sub-millisecond steps.
            if (Math.Abs(e.NewValue - _viewModel.PositionMs) >= 1)
                _viewModel.PositionMs = e.NewValue;
            return;
        }

        // I-frame magnetism while dragging: within reach, snap the thumb to the I-frame; the drag continues from the thumb, so it sticks
        // until the pointer moves clear.
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

        // Instant Seek serves every drag position exactly, so no pacing; ScrubPlayheadTo skips repeats so the drag direction is not reset.
        ScrubPlayheadTo(e.NewValue);
    }

    /// <summary>
    /// Seeks the player when the playhead moves from any non-player source (ruler, rows, keys, studios). Independent of the Overview
    /// Scrubber binding, which is dead while the scrubber is hidden. Skips times the paused player is already at (the slider may have sent it).
    /// </summary>
    private void FollowPlayheadInPlayer()
    {
        if (_updatingFromPlayer || _gpu is null)
            return;

        // The tab still (MainWindow.ProjectTabs.cs) shows the resume time, so hide it on any playhead move except the switch's own restore
        // (including file-loaded tabs whose view restores after the switch), which keeps it until the first frame (WatchForFirstFrame).
        if (!_switchingTab && !_restoringTabView)
            HideTabStill();

        // Scrub flush (once per frame): the player was already seeked during the pointer move (MainWindow.Scrub.cs).
        if (_scrubFlushing)
            return;
        var ms = (long)_viewModel.PositionMs;
        if (!PlayerAt(ms))
            PlayerSeek(ms);
    }

    // True if the paused engine clock (set immediately by a seek) is already at this time within 1 ms.
    private bool PlayerAt(long positionMs) =>
        _gpu is { IsPlaying: false } engine && Math.Abs(engine.Position * 1000 - positionMs) < 1;

    /// <summary>
    /// Press anywhere on the Overview Scrubber: jumps the thumb to the pointer and forwards the press to the thumb, so dragging scrubs and
    /// release ends it through the normal DragStarted/DragCompleted path. The slider may already have moved the thumb; it does not grab it.
    /// </summary>
    private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TimelineSlider.Template?.FindName("PART_Track", TimelineSlider) is not Track { Thumb: { } thumb } track
            || thumb.IsMouseOver || thumb.IsDragging || TimelineSlider.Maximum <= TimelineSlider.Minimum)
        {
            return;
        }

        // Track.ValueFromPoint uses the thumb's last layout; the slider may have just moved it, so lay out first to avoid adding the
        // pointer offset twice.
        TimelineSlider.UpdateLayout();
        var value = track.ValueFromPoint(e.GetPosition(track));
        if (!double.IsFinite(value))
            return;

        TimelineSlider.Value = Math.Clamp(value, TimelineSlider.Minimum, TimelineSlider.Maximum);

        // Lay out the thumb at its new position before the synthetic press; it measures the drag from there.
        TimelineSlider.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent, Source = thumb });
        e.Handled = true;
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e) => _isScrubbing = true;

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isScrubbing = false;
        EndScrub();

        // Release (a click is press + immediate release): exact-frame seek now.
        PlayerSeek((long)TimelineSlider.Value);
    }
}
