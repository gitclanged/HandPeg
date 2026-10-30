using System.IO;
using System.Windows;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The GPU preview engine: the player (Pass 5.0-AudioAnd3D, 5.0D; the only player since 5.0-Verify) -----
// The player is GpuPreviewEngine: the compositor draws the scene plan of the sequence into the video surface, the WASAPI
// mixer plays its sound, and the mixer's clock is the playhead. Everything that moves it (seeks, play and pause, J/K/L,
// scrubs, Loop, the zoom) goes through the few places in MainWindow.Player.cs (PlayerSeek, PlayerSetPause,
// PlayerIsPlaying, TakePlayerPosition, ApplyLiveFilter ...). An edit is a new scene plan, never a graph.
// When the engine cannot start, or its worker's supervisor gives up on it, the player stops with a notice where the
// picture was (OnGpuFailed) and starts again by itself a few seconds later; failing again, on the processor (WARP: slow,
// but a picture); failing there too, it waits for Reload Player.

public partial class MainWindow
{
    private GpuPreviewEngine? _gpu;
    private bool _startingGpu;
    private bool _gpuPointerHooked;

    // The engine's failures in a row (a start that came to nothing, or a worker its supervisor gave up on), when the last
    // engine started, whether the next start is on the processor (WARP, after the card failed twice), and whether it has
    // stopped trying until Reload Player.
    private int _gpuFailures;
    private long _gpuStartedAt;
    private bool _gpuOnWarp, _gpuGaveUp;

    // A stopped engine starts again this long after it stopped; one that had run this long fails as if for the first time.
    private const int GpuRetrySeconds = 3;
    private const long GpuSteadyMs = 5 * 60 * 1000;
    private readonly System.Windows.Threading.DispatcherTimer _gpuRetry = new() { Interval = TimeSpan.FromSeconds(GpuRetrySeconds) };
    private bool _gpuRetryHooked;

    /// <summary>
    /// Starts the GPU engine on the video surface (after Frame 1, off the UI thread), then opens what is loaded in it.
    /// Nothing to do while it runs or starts, before anything wants it, while it waits to start again after a failure, and
    /// once it has stopped trying (see <see cref="OnGpuFailed"/>).
    /// </summary>
    private void EnsureGpuEngine()
    {
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

        // The window closed while it started: nothing will ever close it but this.
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

        // Reload Player asked for the graphics card while this start was on the processor (WARP): the card's start follows.
        if (warp is not null && !_gpuOnWarp)
        {
            engine.Dispose();
            EnsureGpuEngine();
            return;
        }

        _gpu = engine;
        _gpuStartedAt = Environment.TickCount64;
        EngineNotice.Visibility = Visibility.Collapsed;
        if (warp is not null)
            _viewModel.StatusText = "The GPU preview draws on the processor (WARP), slowly. Reload Player tries the graphics card again.";
        engine.Ended += () => Dispatcher.BeginInvoke(() =>
        {
            // The end of an emptied engine's plan (the project closed while playing, UnloadGpu): nothing to loop.
            if (!ReferenceEquals(_gpu, engine) || !_viewModel.HasSource)
                return;
            _viewModel.IsPlaying = false;
            FollowPlayerByFrame(false);
            _updatingFromPlayer = true;
            _viewModel.PositionMs = engine.Position * 1000;
            _updatingFromPlayer = false;

            // Wound back to the start (J): the shuttle ends there; Loop is for playing forwards.
            if (_shuttle < 0)
            {
                _shuttle = 0;
                engine.Rate = _viewModel.PlaybackRate;
                return;
            }

            OnPlaybackEnded();
        });
        engine.Faulted += reason => Dispatcher.BeginInvoke(() => OnGpuFaulted(engine, reason));
        engine.Volume = (float)(_viewModel.Volume / 100.0);
        engine.Muted = _viewModel.IsPlayerMuted;
        engine.Rate = _viewModel.PlaybackRate;

        // A started-again engine draws the player's zoom and pan the overlays are laid with.
        engine.SetView(_playerZoom, _playerPanX, _playerPanY);
        if (!_gpuPointerHooked)
        {
            VideoView.SizeChanged += (_, _) => _gpu?.Invalidate();

            // The moment under the pointer on a timeline (the Timeline's rows and its ruler, the Overview Bar): Instant Seek
            // decodes it before a click.
            UnifiedTimeline.Surface.PointerSecondsChanged += OnTimelinePointer;
            TimelineSlider.MouseMove += OverviewPointerMoved;
            TimelineSlider.MouseLeave += OverviewPointerLeft;

            // A video likely to be dropped (its Media tile rested on, a drag of it begun): made ready ahead (MainWindow.Drops).
            MediaBrowser.PrewarmRequested += PrewarmDrop;

            // The captions made again, or the subtitle track's pictures read or retimed: the engine takes them with the sequence.
            _viewModel.CaptionsChanged += ScheduleGpuState;
            _gpuPointerHooked = true;
        }

        UpdateVideoVisibility();

        // What was loaded while it started, where the playhead is.
        var (start, paused) = _pendingMedia is { } media ? (media.StartMs, media.Paused) : ((long)_viewModel.PositionMs, true);
        _pendingMedia = null;
        if (_viewModel.HasSource)
            GpuOpen(start, paused);
    }

    /// <summary>The engine's worker failed too often in a row and its supervisor gave up: the engine goes, and the player stops where the playhead is.</summary>
    private void OnGpuFaulted(GpuPreviewEngine engine, string reason)
    {
        if (!ReferenceEquals(_gpu, engine))
            return;

        AppLog.Write($"GPU engine: stopped ({reason}).");
        CrashReporter.Note("player", "the GPU preview stopped (its worker failed too often)");
        _gpu = null;
        engine.Dispose();
        _viewModel.IsPlaying = false;
        FollowPlayerByFrame(false);
        _shuttle = 0;
        _pendingMedia = ((long)_viewModel.PositionMs, true);
        UpdateVideoVisibility();

        // After a long steady run, a failure is a first one again (and the card is tried again before the processor).
        if (Environment.TickCount64 - _gpuStartedAt > GpuSteadyMs)
            (_gpuFailures, _gpuOnWarp) = (0, false);
        OnGpuFailed(reason);
    }

    /// <summary>
    /// The engine could not start, or stopped: the notice where the picture was says why, and it starts again by itself in
    /// <see cref="GpuRetrySeconds"/> seconds: on the same card the first time, on the processor (WARP) after a second failure
    /// in a row. After that it waits for Reload Player.
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

    /// <summary>Reload Player: the failures so far are forgotten, and the engine is tried on the card again, at once.</summary>
    private void ForgetGpuFailures()
    {
        _gpuRetry.Stop();
        (_gpuFailures, _gpuOnWarp, _gpuGaveUp) = (0, false, false);
        if (EngineNotice.Visibility == Visibility.Visible)
            (EngineNoticeTitle.Text, EngineNoticeText.Text) = ("Starting the GPU Preview Again...", "");
    }

    /// <summary>The sequence into the engine, at a moment, paused or playing (a file or project opened, the engine just started).</summary>
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

    // An edit is a new plan; edits that come in a burst (a slider dragged) are taken once per frame drawn, not once each.
    private readonly System.Windows.Threading.DispatcherTimer _gpuStateTimer = new(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
    private bool _gpuStateTimerHooked;

    /// <summary>Something the picture or the sound is made from changed: the engine gets the sequence again within a frame.</summary>
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
    /// The sequence with the subtitle track's pictures (a PGS or VobSub track: the preview's alone), its sound (one soloed
    /// track is the program's to apply: the state is filtered for two or more) and its captions, into the engine.
    /// </summary>
    private void PushGpuState(GpuPreviewEngine engine)
    {
        // A rectangle drawn on the source (the crop, a layer's place) and Live Preview off show the file itself.
        engine.ShowSource(!_viewModel.LivePreview || CurrentOverlayMode != OverlayMode.None);
        var solo = _viewModel.SoloedTracks.Count == 1 ? new Services.Audio.AudioBuildOptions { SoloStreams = _viewModel.SoloedTracks.Select(t => t.Index).ToList() } : null;
        var state = _viewModel.LivePreviewState();
        if (_viewModel.PreviewPictureSubtitles() is { } pictures)
            state = state with { PictureSubtitles = pictures };
        engine.SetState(state, solo);
        var (lines, style, scale) = _viewModel.GpuCaptions();
        engine.SetCaptions(lines, style, scale);
        RequestProgramStrip(state);
    }

    /// <summary>
    /// The project closed (HasSource went false): the engine is emptied, so its worker stops decoding the closed project's
    /// files and lets them go (5.0-Release review: its last plan's cut hints and the pointer's kept them open and decoding).
    /// </summary>
    private void UnloadGpu()
    {
        OnTimelinePointer(null);
        _gpu?.Unload();
    }

    /// <summary>The playhead to a moment in the engine: always the exact frame (Instant Seek shows the nearest at once meanwhile).</summary>
    private void GpuSeek(GpuPreviewEngine engine, long positionMs)
    {
        if (!engine.IsPlaying)
            _pausedOnUsersMoment = true;
        engine.Seek(positionMs / 1000.0);
        engine.PrepareAround(positionMs / 1000.0);
    }

    /// <summary>The engine's clock into the playhead (once per screen refresh while playing, see FollowPlayerByFrame).</summary>
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

    /// <summary>Play or pause the engine (the play button, Space, K).</summary>
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
            // Paused where the clock stopped: that moment is the playhead's.
            _updatingFromPlayer = true;
            _viewModel.PositionMs = engine.Position * 1000;
            _updatingFromPlayer = false;
        }
    }

    /// <summary>J, K, L on the engine: its clock runs at so many times, backwards too (silent), no stepping needed.</summary>
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

        // At most four times: the sound's clock (the master) runs no faster.
        var speed = Math.Sign(_shuttle) == direction ? Math.Min(Math.Abs(_shuttle) * 2, 4)
            : direction > 0 && _shuttle == 0 && engine.IsPlaying ? 2
            : 1;
        _shuttle = direction * speed;
        engine.Rate = direction * speed;
        if (!engine.IsPlaying)
            GpuSetPaused(engine, paused: false);
        _viewModel.StatusText = $"{(direction > 0 ? "Forward" : "Reverse")} {speed}x. K stops; {(direction > 0 ? "L" : "J")} again goes faster.";
    }

    // ----- The pointer over a timeline, told to the engine (Pass 5.0-Release: the user's hover pre-roll) -----

    /// <summary>The pointer's moment is told to the engine at most once in this long, ms (about 30 times a second: the newest wins).</summary>
    private const int PointerHintMs = 33;

    // The newest moment not told yet (NaN: the pointer left), whether there is one, when the engine was told last
    // (Environment.TickCount64), and the timer that tells it the newest at the end of a burst. Nothing is made per move.
    private double _pointerPending = double.NaN;
    private bool _pointerOwed;
    private long _pointerToldAt = long.MinValue;
    private System.Windows.Threading.DispatcherTimer? _pointerTimer;

    /// <summary>
    /// The moment under the pointer on a timeline (null when it left): the engine decodes its frame before a click, and once
    /// it rests a short play-ahead from it. Sampled: told at once when the last telling is a sample's time ago, else the
    /// newest moment is told when that time is up; the pointer leaving is told at once.
    /// </summary>
    private void OnTimelinePointer(double? sequenceSeconds)
    {
        // Nothing open: an empty timeline's moments are no file's (the engine's plan is empty, UnloadGpu).
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

        // The rest of the sample's time from the last telling.
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

    /// <summary>The pointer over the Overview Bar: its moment of the sequence (the slider's track, as a click there seeks).</summary>
    private void OverviewPointerMoved(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_viewModel.DurationMs <= 0 || TimelineSlider.ActualWidth <= 0)
            return;
        OnTimelinePointer(TimelineMsAt(e.GetPosition(TimelineSlider).X) / 1000);
    }

    private void OverviewPointerLeft(object sender, System.Windows.Input.MouseEventArgs e) => OnTimelinePointer(null);

    /// <summary>
    /// Saves the frame at the playhead as a PNG (Export Frame with the GPU engine): drawn once as the export makes it (the
    /// canvas's size, no bars, no zoom, no 3D view) and read back.
    /// </summary>
    private async void GpuSaveFrame(GpuPreviewEngine engine, string path)
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

        // Written beside it first and moved into place: a folder that cannot be written (a read-only share, a full disk)
        // says so and leaves no half-written picture, and never takes the window down.
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

    private void CloseGpuEngine()
    {
        var engine = _gpu;
        _gpu = null;
        engine?.Dispose();
    }
}
