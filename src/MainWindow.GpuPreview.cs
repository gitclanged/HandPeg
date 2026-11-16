using System.IO;
using System.Windows;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

// ----- GPU preview engine (the player) -----
// GpuPreviewEngine is the sole player: the compositor renders the sequence's scene plan into the video surface and the WASAPI mixer
// plays audio; the mixer clock is the playhead. All transport (seek, play/pause, J/K/L, scrub, Loop, zoom) goes through
// MainWindow.Player.cs (PlayerSeek, PlayerSetPause, PlayerIsPlaying, TakePlayerPosition ...); state changes go through
// ScheduleGpuState. An edit produces a new scene plan, never a graph.
// Start failure or supervisor give-up: show a notice (OnGpuFailed) and auto-retry after a few seconds; a second consecutive failure
// retries on WARP (slow but functional); a failure there waits for Reload Player.

public partial class MainWindow
{
    private GpuPreviewEngine? _gpu;
    private bool _startingGpu;
    private bool _gpuPointerHooked;

    // Consecutive failures (failed start or supervisor give-up), last start time, next start on WARP (after two hardware failures), and
    // gave-up state awaiting Reload Player.
    private int _gpuFailures;
    private long _gpuStartedAt;
    private bool _gpuOnWarp, _gpuGaveUp;

    // Retry delay after a stop; an engine that ran at least GpuSteadyMs resets the failure count.
    private const int GpuRetrySeconds = 3;
    private const long GpuSteadyMs = 5 * 60 * 1000;
    private readonly System.Windows.Threading.DispatcherTimer _gpuRetry = new() { Interval = TimeSpan.FromSeconds(GpuRetrySeconds) };
    private bool _gpuRetryHooked;

    /// <summary>
    /// Starts the GPU engine on the video surface (after Frame 1, off the UI thread), then opens the loaded media. No-op while running or
    /// starting, before the player is needed, during a retry wait, or after giving up (<see cref="OnGpuFailed"/>).
    /// </summary>
    private void EnsureGpuEngine()
    {
        // Enable project thumbnail capture on save (returns none while the engine is not running).
        if (_viewModel.CaptureThumbnail is null)
            _viewModel.CaptureThumbnail = CaptureProjectThumbnailAsync;
        if (_gpu is not null || _startingGpu || !_shellDrawn || _gpuRetry.IsEnabled || _gpuGaveUp || _windowClosing.IsCancellationRequested)
            return;
        if (!_playerWanted && _pendingMedia is null && !_viewModel.HasSource)
            return;
        var surface = VideoView.SurfaceHandle;
        if (surface == IntPtr.Zero)
            return;

        _startingGpu = true;
        _ = StartGpuAsync(surface);
    }

    /// <summary>Current window-side engine inputs (<see cref="Services.PlayerInputs"/>).</summary>
    private PlayerInputs PlayerInputs() =>
        new((float)(_viewModel.Volume / 100.0), _viewModel.IsPlayerMuted, _viewModel.PlaybackRate, _playerZoom, _playerPanX, _playerPanY, GradeCompareNow, _gradeWipe);

    private async Task StartGpuAsync(IntPtr surface)
    {
        GpuPreviewEngine? engine = null;
        var warp = _gpuOnWarp ? GpuAdapters.WarpIndex : null;
        try
        {
            engine = await GpuPreviewEngine.StartAsync(surface, warp);
        }
        finally
        {
            _startingGpu = false;
        }

        // Window closed during start: dispose here, since nothing else owns the engine.
        if (_windowClosing.IsCancellationRequested)
        {
            engine?.Dispose();
            return;
        }

        if (engine is null)
        {
            OnGpuFailed(GpuPreviewEngine.LastStartFailure);
            return;
        }

        // Reload Player requested hardware during a WARP start: dispose this engine (pool teardown) and start on hardware once it completes.
        if (warp is not null && !_gpuOnWarp)
        {
            _ = engine.DisposeInBackground();
            EnsureGpuEngine();
            return;
        }

        _gpu = engine;
        _gpuStartedAt = Environment.TickCount64;

        // Side windows' Live Video Previews mirror this engine at its reported aspect.
        engine.MirrorAspectChanged += aspect => Dispatcher.BeginInvoke(() => TakeMirrorAspect(engine, aspect));
        ReattachMirrors();
        EngineNotice.Visibility = Visibility.Collapsed;
        if (warp is not null)
            _viewModel.StatusText = "The GPU preview draws on the processor (WARP), slowly. Reload Player tries the graphics card again.";
        engine.Ended += () => Dispatcher.BeginInvoke(() =>
        {
            // Ignore ends from an emptied plan (project closed while playing, UnloadGpu), and when Play was pressed again before this queued
            // handler ran (no stale IsPlaying = false, no Loop restart).
            if (!ReferenceEquals(_gpu, engine) || !_viewModel.HasSource || engine.IsPlaying)
                return;
            _viewModel.IsPlaying = false;
            FollowPlayerByFrame(false);
            _updatingFromPlayer = true;
            _viewModel.PositionMs = engine.Position * 1000;
            _updatingFromPlayer = false;

            // Reverse shuttle (J) reached the start: stop shuttling; Loop applies only to forward play.
            if (_shuttle < 0)
            {
                _shuttle = 0;
                engine.Rate = _viewModel.PlaybackRate;
                return;
            }

            OnPlaybackEnded();
        });
        engine.Faulted += reason => Dispatcher.BeginInvoke(() => OnGpuFaulted(engine, reason));

        // Push all inputs to a new engine: volume, mute, rate, player zoom/pan (shared with overlays) and grade Before/After (covers Reload
        // Player, fault restarts, and a compare set before any engine existed).
        engine.Apply(PlayerInputs());
        if (!_gpuPointerHooked)
        {
            // Resize: invalidate on SurfaceResized (the child HWND's real new size; SizeChanged fires before the host moves it and may read
            // the old size) and on SizeChanged. In between, the frame is centred, never stretched (GpuViewport).
            VideoView.SizeChanged += (_, _) => _gpu?.InvalidateView();
            VideoView.SurfaceResized += () => _gpu?.InvalidateView();

            // Pointer time over the Timeline rows/ruler or Overview Scrubber: Instant Seek pre-decodes it before a click.
            UnifiedTimeline.Surface.PointerSecondsChanged += OnTimelinePointer;
            TimelineSlider.MouseMove += OverviewPointerMoved;
            TimelineSlider.MouseLeave += OverviewPointerLeft;

            // Prewarm a likely drop (Media tile hover dwell or drag start; MainWindow.Drops).
            MediaBrowser.PrewarmRequested += _drops.PrewarmDrop;

            // Caption rebuilds and subtitle bitmap loads/retimes resend the sequence state.
            _viewModel.CaptionsChanged += ScheduleGpuState;
            _gpuPointerHooked = true;
        }

        UpdateVideoVisibility();

        // Open media loaded during start-up (pending start/pause), else the current playhead paused.
        var (start, paused) = _pendingMedia is { } media ? (media.StartMs, media.Paused) : ((long)_viewModel.PositionMs, true);
        _pendingMedia = null;
        if (_viewModel.HasSource)
        {
            // A tab restored in 3D view before an engine existed: enter 3D first, as RestoreTabView does.
            RestoreExplodedOnEngine();
            GpuOpen(start, paused);
        }
    }

    /// <summary>Worker supervisor gave up after repeated faults: drops the engine and stops the player at the playhead.</summary>
    private void OnGpuFaulted(GpuPreviewEngine engine, string reason)
    {
        if (!ReferenceEquals(_gpu, engine))
            return;

        AppLog.Write($"GPU engine: stopped ({reason}).");
        CrashReporter.Note("player", "the GPU preview stopped (its worker failed too often)");
        _gpu = null;
        _ = engine.DisposeInBackground();
        ForgetMirrors();
        _viewModel.IsPlaying = false;
        FollowPlayerByFrame(false);
        _shuttle = 0;
        _pendingMedia = ((long)_viewModel.PositionMs, true);
        UpdateVideoVisibility();

        // After a long stable run, reset the failure count (retry hardware before WARP).
        if (Environment.TickCount64 - _gpuStartedAt > GpuSteadyMs)
            (_gpuFailures, _gpuOnWarp) = (0, false);
        OnGpuFailed(reason);
    }

    /// <summary>
    /// Engine start failure or stop: shows the reason in the video notice and auto-retries after <see cref="GpuRetrySeconds"/> s, on the
    /// same adapter first, on WARP after a second consecutive failure; after that, waits for Reload Player.
    /// </summary>
    private void OnGpuFailed(string reason)
    {
        _gpuFailures++;
        reason = reason.Trim().TrimEnd('.');
        string next;
        if (_gpuFailures == 1)
        {
            next = "It starts again by itself in a moment; Reload Player starts it at once.";
        }
        else if (!_gpuOnWarp && GpuAdapters.WarpIndex is not null)
        {
            _gpuOnWarp = true;
            next = "It starts again in a moment on the processor (WARP: slow, but it shows the picture). Reload Player tries the graphics card again.";
        }
        else
        {
            _gpuGaveUp = true;
            next = "Reload Player (in the playback bar) starts it again.";
        }

        (EngineNotice.Visibility, EngineProgress.Visibility) = (Visibility.Visible, Visibility.Collapsed);
        EngineNoticeTitle.Text = "GPU Preview Stopped";
        EngineNoticeText.Text = $"GPU preview stopped: {reason}. {next}";
        _viewModel.StatusText = $"GPU preview stopped: {reason}.";
        if (_gpuGaveUp)
            return;

        if (!_gpuRetryHooked)
        {
            _gpuRetry.Tick += (_, _) =>
            {
                _gpuRetry.Stop();
                EngineNoticeTitle.Text = "Starting the GPU Preview Again...";
                EnsureGpuEngine();
            };
            _gpuRetryHooked = true;
        }

        _gpuRetry.Stop();
        _gpuRetry.Start();
    }

    /// <summary>Reload Player: clears failure state so the next start uses hardware immediately.</summary>
    private void ForgetGpuFailures()
    {
        _gpuRetry.Stop();
        (_gpuFailures, _gpuOnWarp, _gpuGaveUp) = (0, false, false);
        if (EngineNotice.Visibility == Visibility.Visible)
            (EngineNoticeTitle.Text, EngineNoticeText.Text) = ("Starting the GPU Preview Again...", "");
    }

    /// <summary>Pushes the sequence and seeks to <paramref name="startMs"/>, paused or playing (after open or engine start).</summary>
    private void GpuOpen(long startMs, bool paused)
    {
        if (_gpu is not { } engine)
            return;
        _gpuStateTimer.Stop();
        PushGpuState(engine);
        engine.Seek(startMs / 1000.0);
        if (paused)
            engine.Pause();
        else
            engine.Play();
        _viewModel.IsPlaying = engine.IsPlaying;
        FollowPlayerByFrame(engine.IsPlaying);
        TakeGpuPosition();
    }

    // Coalesces burst edits (slider drags) into one scene-plan push per ~frame (8 ms, Render priority).
    private readonly System.Windows.Threading.DispatcherTimer _gpuStateTimer = new(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
    private bool _gpuStateTimerHooked;

    /// <summary>Video or audio inputs changed: schedules a state push within a frame.</summary>
    private void ScheduleGpuState()
    {
        if (_gpu is null)
            return;
        if (!_gpuStateTimerHooked)
        {
            _gpuStateTimer.Tick += (_, _) =>
            {
                _gpuStateTimer.Stop();
                if (_gpu is { } engine && _viewModel.HasSource)
                    PushGpuState(engine);
            };
            _gpuStateTimerHooked = true;
        }

        if (!_gpuStateTimer.IsEnabled)
            _gpuStateTimer.Start();
    }

    /// <summary>
    /// Pushes the sequence state with preview-only bitmap subtitles (PGS/VobSub), audio (solo state from the document) and captions.
    /// </summary>
    private void PushGpuState(GpuPreviewEngine engine)
    {
        // Interactive Crop or a Draw Target overlay shows the overlay clip's source (else the video at the playhead); otherwise the
        // composite.
        engine.ShowSource(CurrentOverlayMode != OverlayMode.None, _viewModel.ShownSourceClip);
        var state = _viewModel.LivePreviewState();
        if (_viewModel.PreviewPictureSubtitles() is { } pictures)
            state = state with { PictureSubtitles = pictures };
        engine.SetState(state, null);
        var (lines, style, scale) = _viewModel.GpuCaptions();
        engine.SetCaptions(lines, style, scale);
        RequestProgramStrip(state);
    }

    /// <summary>
    /// Project closed (HasSource false): clears the pointer hint and empties the engine so the worker releases the closed project's files
    /// (stale cut and pointer hints would otherwise keep them open and decoding).
    /// </summary>
    private void UnloadGpu()
    {
        OnTimelinePointer(null);
        _gpu?.Unload();
    }

    /// <summary>Exact-frame seek (Instant Seek shows the nearest frame meanwhile) and pre-roll around the target.</summary>
    private void GpuSeek(GpuPreviewEngine engine, long positionMs)
    {
        if (!engine.IsPlaying)
            _pausedOnUsersMoment = true;
        engine.Seek(positionMs / 1000.0);
        engine.PrepareAround(positionMs / 1000.0);
    }

    /// <summary>Copies the engine clock to the playhead while playing (per display refresh via FollowPlayerByFrame).</summary>
    private void TakeGpuPosition()
    {
        if (_gpu is not { } engine)
            return;
        var ms = (long)Math.Round(engine.Position * 1000);
        if (engine.IsPlaying)
        {
            engine.PrepareAround(engine.Position);
            OnPlayerTimeChanged(ms);
        }
    }

    /// <summary>Play/pause (play button, Space, K).</summary>
    private void GpuSetPaused(GpuPreviewEngine engine, bool paused)
    {
        if (paused)
            engine.Pause();
        else
            engine.Play();
        var playing = engine.IsPlaying;
        _viewModel.IsPlaying = playing;
        FollowPlayerByFrame(playing);
        if (!playing)
        {
            // Sync the playhead to the clock's stop position.
            _updatingFromPlayer = true;
            _viewModel.PositionMs = engine.Position * 1000;
            _updatingFromPlayer = false;
        }
    }

    /// <summary>J/K/L shuttle: sets the engine clock rate (reverse is silent); no frame stepping needed.</summary>
    private void GpuShuttle(GpuPreviewEngine engine, int direction)
    {
        if (direction == 0)
        {
            _shuttle = 0;
            engine.Rate = _viewModel.PlaybackRate;
            GpuSetPaused(engine, paused: true);
            _viewModel.StatusText = "Paused.";
            return;
        }

        // Capped at 4x: the audio master clock runs no faster.
        var speed = Math.Sign(_shuttle) == direction ? Math.Min(Math.Abs(_shuttle) * 2, 4)
            : direction > 0 && _shuttle == 0 && engine.IsPlaying ? 2
            : 1;
        _shuttle = direction * speed;
        engine.Rate = direction * speed;
        if (!engine.IsPlaying)
            GpuSetPaused(engine, paused: false);
        _viewModel.StatusText = $"{(direction > 0 ? "Forward" : "Reverse")} {speed}x. K stops; {(direction > 0 ? "L" : "J")} again goes faster.";
    }

    // ----- Timeline pointer hints (hover pre-roll) -----

    /// <summary>Minimum interval between pointer hints to the engine in ms (~30 Hz; latest wins).</summary>
    private const int PointerHintMs = 33;

    // Throttle state: latest unsent time (NaN = pointer left), pending flag, last send (Environment.TickCount64), and the trailing-edge
    // timer. No per-move allocation.
    private double _pointerPending = double.NaN;
    private bool _pointerOwed;
    private long _pointerToldAt = long.MinValue;
    private System.Windows.Threading.DispatcherTimer? _pointerTimer;

    /// <summary>
    /// Timeline pointer time (null on leave): the engine pre-decodes that frame and, at rest, a short pre-roll. Throttled to
    /// <see cref="PointerHintMs"/> with a trailing send of the latest value; leave is sent immediately.
    /// </summary>
    private void OnTimelinePointer(double? sequenceSeconds)
    {
        // Nothing open: the engine plan is empty (UnloadGpu), so treat as leave.
        if (!_viewModel.HasSource)
            sequenceSeconds = null;
        if (sequenceSeconds is null)
        {
            _pointerOwed = false;
            _pointerTimer?.Stop();
            _pointerPending = double.NaN;
            _gpu?.SetPointer(null);
            return;
        }

        _pointerPending = sequenceSeconds.Value;
        if (_pointerTimer is { IsEnabled: true })
        {
            _pointerOwed = true;
            return;
        }

        var now = Environment.TickCount64;
        if (now - _pointerToldAt >= PointerHintMs)
        {
            TellPointer(now);
            return;
        }

        _pointerOwed = true;
        if (_pointerTimer is null)
        {
            _pointerTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input);
            _pointerTimer.Tick += PointerTimer_Tick;
        }

        // Remaining throttle interval since the last send.
        _pointerTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(PointerHintMs - (now - _pointerToldAt), 1, PointerHintMs));
        _pointerTimer.Start();
    }

    private void PointerTimer_Tick(object? sender, EventArgs e)
    {
        _pointerTimer?.Stop();
        if (_pointerOwed)
            TellPointer(Environment.TickCount64);
    }

    private void TellPointer(long now)
    {
        _pointerOwed = false;
        _pointerToldAt = now;
        _gpu?.SetPointer(double.IsNaN(_pointerPending) ? null : _pointerPending);
    }

    /// <summary>Overview Scrubber hover: maps the slider track x to sequence time (same mapping as click-seek).</summary>
    private void OverviewPointerMoved(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_viewModel.DurationMs <= 0 || TimelineSlider.ActualWidth <= 0)
            return;
        OnTimelinePointer(TimelineMsAt(e.GetPosition(TimelineSlider).X) / 1000);
    }

    private void OverviewPointerLeft(object sender, System.Windows.Input.MouseEventArgs e) => OnTimelinePointer(null);

    /// <summary>
    /// Export Frame: renders the playhead frame once as export would (canvas size, no bars, zoom or 3D view), reads it back and saves a PNG.
    /// </summary>
    private void GpuSaveFrame(GpuPreviewEngine engine, string path) => Faults.Forget(GpuSaveFrameAsync(engine, path), nameof(GpuSaveFrame));

    private async Task GpuSaveFrameAsync(GpuPreviewEngine engine, string path)
    {
        if (await engine.CaptureFrameAsync() is not { } frame || frame.Width <= 0 || frame.Height <= 0)
        {
            _viewModel.StatusText = "The frame could not be read from the GPU engine.";
            return;
        }

        var (bgra, width, height) = frame;
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        // Write to .part then move: unwritable targets (read-only share, full disk) report an error, leave no partial file and never
        // crash the window.
        var part = path + ".part";
        try
        {
            using (var file = File.Create(part))
                encoder.Save(file);
            File.Move(part, path, overwrite: true);
            _viewModel.StatusText = $"Frame saved: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            AppLog.Write("Export Frame", ex);
            try
            {
                File.Delete(part);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            _viewModel.StatusText = $"The frame could not be saved to {path}: {ex.Message}";
        }
    }

    /// <summary>
    /// Project thumbnail for saves (MainViewModel.CaptureThumbnail): GPU-rendered small playhead frame (GpuPreviewEngine.CaptureThumbnailAsync)
    /// JPEG-encoded on the thread pool. Null with no engine, while playing, or with nothing open. Starts on the UI thread; nothing after
    /// the first await needs it.
    /// </summary>
    private async Task<byte[]?> CaptureProjectThumbnailAsync()
    {
        if (_gpu is not { } engine || !_viewModel.HasSource)
            return null;

        if (await engine.CaptureThumbnailAsync().ConfigureAwait(false) is not { } frame)
            return null;

        return await Task.Run(() => ProjectThumbnail.EncodeJpeg(frame.Bgra, frame.Width, frame.Height)).ConfigureAwait(false);
    }

    /// <summary>Window close: disposes the engine synchronously on the UI thread (process is exiting).</summary>
    private void CloseGpuEngine()
    {
        var engine = _gpu;
        _gpu = null;
        engine?.Dispose();
        ForgetMirrors();
    }

    /// <summary>Reload Player: releases the engine with pool teardown, which the next start awaits.</summary>
    private void ReleaseGpuEngine()
    {
        var engine = _gpu;
        _gpu = null;
        _ = engine?.DisposeInBackground();
        ForgetMirrors();
    }

    // ----- Side window Live Video Preview mirrors -----

    // Mirror HWND attached per side window on the current engine, and the last aspect reported by the engine.
    private readonly Dictionary<Docking.SideWindow, IntPtr> _mirrored = [];
    private double _mirrorAspect = 16.0 / 9.0;

    private const string MirrorIdleNote = "The Live Video Preview shows the player's picture here once a project or a video is open.";

    /// <summary>
    /// Reconciles mirrors: every open Live Video Preview side window is attached (GpuPreviewEngine.AttachMirror on its surface HWND);
    /// closed or Pane Docker windows are detached. Applies canvas aspect and idle notes. Called on side window open/close/mode change,
    /// preview surface creation, or engine change.
    /// </summary>
    private void SyncMirrors()
    {
        var open = _dock.SideWindows;
        foreach (var (window, handle) in _mirrored.ToList())
        {
            if (open.Contains(window) && window.PreviewHandle == handle)
                continue;
            _gpu?.DetachMirror(handle);
            _mirrored.Remove(window);
        }

        foreach (var window in open.Where(w => w.Mode == Docking.SideWindowMode.LiveVideoPreview))
        {
            window.SetPreviewAspect(_mirrorAspect);
            if (_gpu is { } engine && window.PreviewHandle is var handle && handle != IntPtr.Zero && !_mirrored.ContainsKey(window))
            {
                engine.AttachMirror(handle);
                _mirrored[window] = handle;
            }
        }

        UpdateMirrorNotes();
    }

    /// <summary>New engine: re-attaches every Live Video Preview from scratch.</summary>
    private void ReattachMirrors()
    {
        _mirrored.Clear();
        SyncMirrors();
    }

    /// <summary>Engine gone (mirrors with it): clears tracking and shows idle notes until the next engine.</summary>
    private void ForgetMirrors()
    {
        _mirrored.Clear();
        UpdateMirrorNotes();
    }

    /// <summary>Mirror aspect (canvas aspect) changed: refits every preview.</summary>
    private void TakeMirrorAspect(GpuPreviewEngine engine, double aspect)
    {
        if (!ReferenceEquals(_gpu, engine))
            return;
        _mirrorAspect = aspect;
        foreach (var window in _dock.SideWindows.Where(w => w.Mode == Docking.SideWindowMode.LiveVideoPreview))
            window.SetPreviewAspect(aspect);
    }

    /// <summary>
    /// Updates preview notes: no engine = idle note in place of the picture; player hidden (inactive tab or too small, so no new frames) =
    /// last frame kept, flagged in the title bar.
    /// </summary>
    private void UpdateMirrorNotes()
    {
        if (_dock is null)
            return;
        var playerHidden = !VideoView.IsVisible || VideoView.ActualWidth < 8 || VideoView.ActualHeight < 8;
        foreach (var window in _dock.SideWindows.Where(w => w.Mode == Docking.SideWindowMode.LiveVideoPreview))
        {
            window.ShowPreviewNote(_gpu is null ? MirrorIdleNote : null);
            window.SetStatus(_gpu is not null && playerHidden ? "The player is hidden: its last picture" : null);
        }
    }
}
