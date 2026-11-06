using System.Diagnostics;
using System.Runtime.InteropServices;
using HandPegApp.Models;
using HandPegApp.Services.Audio;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>
/// The v5.0 preview engine (Pass 5.0-AudioAnd3D, 5.0D): the player (the only one since 5.0-Verify). Four parts that never
/// wait for one another:
/// <list type="bullet">
/// <item>the compositor worker (<see cref="CompositorClient"/>), drawing <see cref="GpuScenePlan"/>'s scenes, decoding on the
/// card with Instant Seek;</item>
/// <item>the WASAPI mixer (<see cref="WasapiAudioEngine"/>), whose device clock is the master clock while playing;</item>
/// <item>the viewport (<see cref="GpuViewportControl"/>) on the video surface's native window;</item>
/// <item>the frame pump, a thread of its own: while playing it waits for the swap chain's turn, evaluates the plan at the
/// moment the picture will be on the screen (the audio clock plus one refresh), publishes the scene, and presents the
/// worker's frame; paused, it publishes only what changed (a seek, an edit, a resize) and presents what the worker drew
/// (also the exact frame Instant Seek brings in after a stand-in), and leaves the worker its hints (GpuHintWriter: every
/// cut within the lookahead horizon, playing or not; paused, where a scrub is heading and the pointer over a timeline).</item>
/// </list>
/// <para>Threads: <see cref="SetState"/> and <see cref="PrepareAround"/> on the UI thread (WPF rasterizes text and
/// captions there); everything else from any thread. The pump is the only thread that writes scenes and hints.</para>
/// </summary>
public sealed partial class GpuPreviewEngine : IDisposable
{
    private readonly CompositorClient _client;
    private readonly WasapiAudioEngine _audio;
    private readonly GpuViewportControl _viewport;
    private readonly GpuResourceBroker _broker;
    private readonly Thread _pump;
    private readonly AutoResetEvent _dirty = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly EventWaitHandle? _workerFrame;
    private GpuScenePlan _plan = GpuScenePlan.Empty;
    private volatile bool _playing;
    private double _pausedAt;
    private double _rate = 1;

    // The view of the player (its zoom and pan, MainWindow's player navigation) and the pointer over the timeline (its
    // moment, and when it last moved: Stopwatch ticks).
    private float _viewScale = 1, _viewPanX, _viewPanY;
    private double _pointer = double.NaN;
    private long _pointerMoved;

    // The worker's hints, written by the pump alone (only when they change), and the plan they were written from.
    private readonly GpuHintWriter _hintWriter = new();
    private GpuScenePlan? _hintedPlan;

    // A file opened ahead of a drop (Prewarm): its source, and until when (Stopwatch ticks) its first frames are asked for.
    private sealed record PrewarmedFile(int Source, long PathHash, long Until);
    private PrewarmedFile? _prewarm;

    /// <summary>How long a file opened ahead of a drop is kept asked for, seconds (no drop came: let go).</summary>
    private const double PrewarmSeconds = 10;

    // Something the picture shows changed (1): the paused pump draws again. A pointer moving over the timeline only
    // changes the hints and wakes the pump without it (no frame drawn for a hover).
    private int _sceneDirty = 1;

    // The scrub's heading: where the last seeks went and when (a paused drag of the playhead).
    private double _lastSeekAt = double.NaN;
    private long _lastSeekTicks;
    private double _scrubVelocity;

    // The display's refresh interval while playing (measured between the swap chain's turns), seconds.
    private double _refresh = 1 / 60.0;

    private GpuPreviewEngine(CompositorClient client, WasapiAudioEngine audio, nint surface)
    {
        (_client, _audio) = (client, audio);
        _viewport = new GpuViewportControl(client, surface);
        _broker = new GpuResourceBroker(client);

        // A texture finished on the background lane (a mask, a LUT, a caption): the paused picture is drawn again with it.
        _broker.ResourcesChanged += Invalidate;
        try
        {
            _workerFrame = EventWaitHandle.OpenExisting(CompositorProtocol.FrameEvent(client.BaseName));
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            _workerFrame = null;
        }

        _pump = new Thread(Pump) { Name = "GPU frame pump", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _pump.Start();
        Current = this;
    }

    /// <summary>The engine the player runs on now (null while it is not started, or stopped): Settings' cache readout.</summary>
    public static GpuPreviewEngine? Current { get; private set; }

    /// <summary>
    /// Starts the engine for a player surface: the worker and its standby (off the UI thread), the audio device, then the
    /// viewport and the pump. Null when the compositor cannot start (no Direct3D 11 card, a worker that fails at once), with
    /// the reason in <see cref="LastStartFailure"/>: the player then says so and tries again (MainWindow.OnGpuFailed). On the
    /// card of Settings' Compositor GPU role, or on <paramref name="adapter"/> (a DXGI index: the processor's WARP after the
    /// card failed). Call after Frame 1.
    /// </summary>
    public static async Task<GpuPreviewEngine?> StartAsync(nint surface, int? adapter = null)
    {
        var settings = AppSettings.Current;
        var card = adapter ?? settings.CompositorGpuAdapter;
        var budget = settings.GpuFrameCacheMb switch { < 0 => -1L, 0 => 0L, var mb => (long)mb << 20 };
        var threads = settings.GpuDecodeThreads;
        var started = await Task.Run(() =>
        {
            CompositorClient? client = null;
            try
            {
                client = new CompositorClient(card, keepStandby: true, budget, threads, softwareDecode: !settings.PlayerHardwareAcceleration);
                var clock = Stopwatch.StartNew();
                while (client.State == CompositorWorkerState.Starting && clock.ElapsedMilliseconds < 8000)
                    Thread.Sleep(10);
                if (client.State != CompositorWorkerState.Ready)
                {
                    LastStartFailure = $"the compositor did not start ({client.State}: {client.Message})";
                    AppLog.Write($"GPU engine: {LastStartFailure}.");
                    client.Dispose();
                    return ((CompositorClient, WasapiAudioEngine)?)null;
                }

                AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"GPU engine started in {clock.ElapsedMilliseconds} ms: {client.Message}"));
                return (client, WasapiAudioEngine.Create(settings.AudioOutputDeviceId));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LastStartFailure = $"the compositor could not be started ({ex.Message})";
                AppLog.Write("GPU engine: the compositor could not be started", ex);
                client?.Dispose();
                return null;
            }
        });

        return started is { } parts ? new GpuPreviewEngine(parts.Item1, parts.Item2, surface) : null;
    }

    /// <summary>Raised on the pump's thread when playback reaches the end of the sequence (the engine is paused there).</summary>
    public event Action? Ended;

    /// <summary>Why the last <see cref="StartAsync"/> gave no engine, for the player's notice.</summary>
    public static string LastStartFailure { get; private set; } = "";

    /// <summary>Raised on the pump's thread when the compositor gave up (it failed too often in a row): the player stops and says why (MainWindow.OnGpuFaulted).</summary>
    public event Action<string>? Faulted;

    public CompositorClient Client => _client;

    public WasapiAudioEngine Audio => _audio;

    public bool IsPlaying => _playing;

    public double SequenceSeconds => Volatile.Read(ref _plan).SequenceSeconds;

    /// <summary>The master clock: the sound being heard while playing, the paused moment otherwise (sequence seconds).</summary>
    public double Position => _playing ? _audio.Position : Volatile.Read(ref _pausedAt);

    /// <summary>J, K, L: forward and back at so many times (0.25 to 4, negative backward; sound only forward).</summary>
    public double Rate
    {
        get => _rate;
        set
        {
            // What the sound's clock (the master) runs at: 0.25 to 4 times, either way.
            _rate = value == 0 ? 0 : Math.Sign(value) * Math.Clamp(Math.Abs(value), 0.25, 4);
            _audio.Rate = _rate;
        }
    }

    public float Volume
    {
        get => _audio.MasterVolume;
        set => _audio.MasterVolume = Math.Clamp(value, 0, 4);
    }

    public bool Muted
    {
        get => _audio.Muted;
        set => _audio.Muted = value;
    }

    /// <summary>The frame cache's counters (Settings' readout, diagnostics).</summary>
    public FrameCacheStats CacheStats => _client.CacheStats;

    // ----- What it shows -----

    /// <summary>
    /// The sequence as it is now (UI thread): a new scene plan and sound program, drawn at once where the playhead is.
    /// <paramref name="audio"/> carries what the state itself does not (one soloed track, see MainViewModel.Soloed).
    /// </summary>
    public void SetState(SequenceExportState state, AudioBuildOptions? audio = null)
    {
        try
        {
            var plan = GpuScenePlan.Create(state, _broker);
            Volatile.Write(ref _plan, plan);
            _state = state;
            Volatile.Write(ref _canvasFps, state.Timeline.FrameRate.IsValid ? state.Timeline.FrameRate.Fps : 0);
            var heard = audio is null ? AudioProgram.Build(state) : AudioProgram.Build(state, audio);
            _audio.SetProgram(heard);
            // The bands are of what an export hears (every track): with nothing soloed and the usual buses and ducking, the program just
            // built is that one (immutable; a render makes its own mixer), so no second one is built here.
            var exportHears = audio is null or { Duck: null, Buses: null } && !state.Timeline.Tracks.Exists(t => t.IsSoloed);
            PlanEnergies(state, exportHears ? heard : null);
            plan.PrepareAround(Position);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An edit the plan cannot draw yet must never take the player down: the last plan stays.
            AppLog.Write("GPU engine: the scene could not be planned", ex);
        }

        Invalidate();
    }

    /// <summary>
    /// Nothing open any more (UI thread; the project closed): the engine as it starts, an empty plan and sound program, no
    /// pointer and no file made ready, so the worker's hints name none of the closed project's files and its decoders are
    /// let go when idle (DecoderPool.Tidy). Opening something gives it a state again (SetState).
    /// </summary>
    public void Unload()
    {
        Pause();
        Volatile.Write(ref _plan, GpuScenePlan.Empty);
        _state = null;
        Volatile.Write(ref _canvasFps, 0);
        _audio.SetProgram(AudioProgram.Empty);
        ForgetEnergies();
        Volatile.Write(ref _prewarm, null);
        Volatile.Write(ref _pointer, double.NaN);
        Volatile.Write(ref _pointerMoved, Stopwatch.GetTimestamp());
        Invalidate();
    }

    /// <summary>
    /// The captions the player shows (UI thread): line by line on the sequence clock, their style and text scale (as the
    /// .ass the export writes); null lines for none. Rasterized near the playhead by <see cref="PrepareAround"/>.
    /// </summary>
    public void SetCaptions(IReadOnlyList<IReadOnlyList<CaptionWord>>? lines, CaptionStyle? style, double textScale)
    {
        _broker.Text.SetCaptions(lines, style, textScale);
        Volatile.Read(ref _plan).PrepareAround(Position);
        Invalidate();
    }

    /// <summary>The audio settings saved in Settings (device, buffer, sync offset): the device is opened again only when it changed.</summary>
    public void ApplyAudioSettings(AppSettings settings) =>
        _audio.ApplySettings(settings.AudioOutputDeviceId, settings.AudioBufferMs, settings.AudioSyncOffsetMs);

    /// <summary>Rasterizes the captions near the playhead (UI thread, a couple of milliseconds a call): called as the playhead moves.</summary>
    public void PrepareAround(double sequenceSeconds) => Volatile.Read(ref _plan).PrepareAround(sequenceSeconds);

    /// <summary>Draw again (the window was resized, the DPI changed, a resource arrived).</summary>
    public void Invalidate()
    {
        Volatile.Write(ref _sceneDirty, 1);
        _dirty.Set();
    }

    /// <summary>The player's zoom and pan (MainWindow's player navigation: 2^zoom, pan as fractions of the fitted picture).</summary>
    public void SetView(double zoomLog2, double panX, double panY)
    {
        (_viewScale, _viewPanX, _viewPanY) = ((float)Math.Pow(2, zoomLog2), (float)panX, (float)panY);
        Invalidate();
    }

    /// <summary>How much the magnifier loupe enlarges, against the player's own scale (the user's bug 6: 400 %).</summary>
    public const float LoupeZoom = 4;

    private long _loupeKey;
    private int _loupeKind;

    /// <summary>
    /// The magnifier loupe (5.0-Verify): while an edge of the clip <paramref name="clipKey"/> (GpuHash.Of its id) is tuned,
    /// the player shows it 400 % in a corner (SceneRenderer.Loupe.cs); 0 puts it away. <paramref name="kind"/>: 1 feather,
    /// 2 mask, 3 matte, 4 key (FrameSceneHeader.LoupeKind).
    /// </summary>
    public void SetLoupe(long clipKey, int kind)
    {
        if (Interlocked.Exchange(ref _loupeKey, clipKey) == clipKey && Interlocked.Exchange(ref _loupeKind, kind) == kind)
            return;
        Volatile.Write(ref _loupeKind, kind);
        Invalidate();
    }

    /// <summary>
    /// The moment under the pointer over a timeline (null when it left; MainWindow tells it at most about 30 times a
    /// second): while paused its frame is decoded ahead of a click, and once it rests (GpuHintWriter.HoverRest) a short
    /// play-ahead from it, so a click there and Space start at once. Nothing is done for it while playing or scrubbing.
    /// </summary>
    public void SetPointer(double? sequenceSeconds)
    {
        var seconds = sequenceSeconds ?? double.NaN;
        if (seconds.Equals(Volatile.Read(ref _pointer)))
            return;
        Volatile.Write(ref _pointerMoved, Stopwatch.GetTimestamp());
        Volatile.Write(ref _pointer, seconds);
        if (!_playing)
            _dirty.Set();
    }

    /// <summary>
    /// A file a drop is likely to open (UI thread; Pass 5.0-Release, on a fast local drive only, MainWindow.Drops): put in
    /// the source table (the drop's plan finds it there) and its first frames decoded by the worker, after everything else
    /// it does, for <see cref="PrewarmSeconds"/> at most; null lets go of it. Nothing while playing.
    /// </summary>
    public void Prewarm(string? path)
    {
        if (path is null)
        {
            if (Interlocked.Exchange(ref _prewarm, null) is not null)
                Wake();
            return;
        }

        var source = _broker.SourceFor(path, 0);
        if (source < 0)
            return;
        Volatile.Write(ref _prewarm, new PrewarmedFile(source, _broker.PathHashOf(source), Stopwatch.GetTimestamp() + (long)(PrewarmSeconds * Stopwatch.Frequency)));
        Wake();

        void Wake()
        {
            if (!_playing)
                _dirty.Set();
        }
    }

    // ----- The clock -----

    /// <summary>Sends the playhead to a moment (exact; a scrub calls this at the pointer's rate, no pacing needed).</summary>
    public void Seek(double sequenceSeconds)
    {
        var seconds = Math.Clamp(sequenceSeconds, 0, Math.Max(SequenceSeconds, 0));

        // A paused drag of the playhead: its heading, for the hint a little ahead of it.
        var now = Stopwatch.GetTimestamp();
        if (!_playing && !double.IsNaN(_lastSeekAt))
        {
            var elapsed = (now - _lastSeekTicks) / (double)Stopwatch.Frequency;
            if (elapsed is > 0.001 and < 0.25)
                _scrubVelocity = 0.6 * _scrubVelocity + 0.4 * ((seconds - _lastSeekAt) / elapsed);
            else
                _scrubVelocity = 0;
        }

        (_lastSeekAt, _lastSeekTicks) = (seconds, now);
        Volatile.Write(ref _pausedAt, seconds);
        _audio.Seek(seconds);
        Invalidate();
    }

    public void Play()
    {
        if (_playing)
            return;
        var from = Volatile.Read(ref _pausedAt);
        if (_rate >= 0 && from >= SequenceSeconds - 1e-3)
            from = 0;
        _audio.Seek(from);
        _scrubVelocity = 0;

        // Several videos at once: the clock waits for their first frames (PlayStartWait), so none is shown late or out of step.
        _primingSince = _state is { } state && VideosAt(state, from) > 1 ? Stopwatch.GetTimestamp() : 0;
        _primingScene = 0;
        if (_primingSince == 0)
            _audio.Play();
        _playing = true;
        Invalidate();
    }

    /// <summary>
    /// How long, at most, playback may wait for its first frames, ms (5.1-DogfoodPolish, the user's Q1: 350 at most). One video never
    /// waits, and neither do videos whose frames at the playhead and just after it are already decoded: see <see cref="PlayStartReady"/>.
    /// </summary>
    public const int PlayStartWait = 350;

    // When play was pressed with several videos on screen, and the first scene drawn since (0: none yet).
    private long _primingSince, _primingScene;

    /// <summary>How many different videos are on screen at a moment of the timeline.</summary>
    public static int VideosAt(SequenceExportState state, double at) =>
        state.Timeline.Tracks.Where(t => !t.IsHidden).SelectMany(t => t.Clips)
            .Where(c => c.Start <= at && c.End > at && !c.IsHidden && c.ContentKind == Models.LayerKind.VideoFile)
            .Select(c => c.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// Whether play can start: every video on screen has its frame at the playhead and the two after it, as the first scene drawn since the
    /// press found them (the worker's PresentedExact and PresentedAhead, each one's frames looked up in the cache; idea 3 of
    /// LATE_FRAMES_MULTI_VIDEO.md, the user's Q2, and their answer to it: the frames after it too, 5.1-DogfoodPolish Q1). The lanes being
    /// busy says nothing: the pre-roll keeps them so, which is what used to run the wait out every time. An earlier scene's answer is about
    /// somewhere else, so it does not count: <paramref name="scene"/> has to be presented first.
    /// </summary>
    /// <param name="since">When play was pressed (Stopwatch ticks).</param>
    /// <param name="scene">The first scene drawn since; 0 while there is none.</param>
    public static bool PlayStartReady(CompositorClient client, long since, long scene)
    {
        ref var control = ref client.Memory.Control;
        return (scene > 0 && Volatile.Read(ref control.PresentedSequence) >= scene && Volatile.Read(ref control.PresentedExact) != 0 && Volatile.Read(ref control.PresentedAhead) != 0)
            || Stopwatch.GetElapsedTime(since).TotalMilliseconds >= PlayStartWait;
    }

    public void Pause()
    {
        if (!_playing)
            return;
        var at = _audio.Position;
        _audio.Pause();
        _primingSince = 0;
        _playing = false;
        Volatile.Write(ref _pausedAt, Math.Clamp(at, 0, Math.Max(SequenceSeconds, 0)));
        _audio.Seek(Volatile.Read(ref _pausedAt));
        Invalidate();
    }

    // ----- The frame pump -----

    private void Pump()
    {
        var waits = _workerFrame is null ? new WaitHandle[] { _dirty, _stop } : new WaitHandle[] { _dirty, _stop, _workerFrame };
        var lastTurn = Stopwatch.GetTimestamp();
        var failedNoted = false;
        var (owedTurn, presentedLast) = (false, false);
        Span<GpuSeekHint> hints = stackalloc GpuSeekHint[CompositorProtocol.MaxHints];
        while (!_stop.WaitOne(0))
        {
            try
            {
                // Only the supervisor's giving up stops the player: a worker that failed once is taken over (~80 ms).
                // Raised once someone listens (the pump starts before MainWindow subscribes).
                if (_client.GaveUp && !failedNoted && Faulted is { } faulted)
                {
                    failedNoted = true;
                    faulted(_client.Message);
                }

                // A one-off picture of the frame (Export Frame): drawn and read back here, the one thread that writes scenes.
                if (Interlocked.Exchange(ref _capture, null) is { } capture)
                    Capture(capture);

                // A project's thumbnail (a save, Pass 5.2D): drawn small the same way, and never while playing.
                if (Interlocked.Exchange(ref _thumbnail, null) is { } thumbnail)
                    CaptureThumbnail(thumbnail);

                if (_playing)
                {
                    // Paced by the swap chain: one frame queued at most, so what is drawn now is on screen one refresh on. A
                    // turn that presented nothing left the swap chain's event unset: the next turn does not wait for it. The
                    // refresh is measured only between two paced turns (a presented frame, then the swap chain's turn).
                    var paced = !owedTurn;
                    if (owedTurn)
                        Thread.Sleep(1);
                    else
                        _viewport.WaitForTurn(50);
                    var now = Stopwatch.GetTimestamp();
                    var interval = (now - lastTurn) / (double)Stopwatch.Frequency;
                    lastTurn = now;
                    // 1.5 ms up: a 500 Hz display is 2 ms a refresh (5.0-Verify: a floor of 4 ms left a 500 Hz display
                    // measured at 1/60 s, the picture 15 ms ahead of the sound).
                    if (paced && presentedLast && interval is > 0.0015 and < 0.05)
                        _refresh = 0.9 * _refresh + 0.1 * interval;
                    presentedLast = false;

                    if (_primingSince != 0 && PlayStartReady(_client, _primingSince, _primingScene))
                    {
                        _primingSince = 0;
                        _audio.Play();
                    }

                    var plan = Volatile.Read(ref _plan);
                    var at = _audio.Position + _refresh * Math.Max(_rate, 0);
                    if (_rate >= 0 && at >= plan.SequenceSeconds - 1e-3 || _rate < 0 && at <= 0)
                    {
                        Pause();
                        Volatile.Write(ref _pausedAt, _rate >= 0 ? plan.SequenceSeconds : 0);
                        Ended?.Invoke();
                        continue;
                    }

                    // The same frame of the timeline as the one drawn last (a 60 fps timeline on a 144 Hz display, or the
                    // 3D view at rest): not drawn again, unless something else changed (an edit, the camera, the window).
                    // The worker's own redraw (an exact frame after a stand-in) is still shown.
                    var frame = CanvasFrameOf(at);
                    var moved = false;
                    CameraMoving(ref moved);
                    if (frame == _lastPlayedFrame && frame != long.MinValue && Volatile.Read(ref _sceneDirty) == 0 && !moved)
                    {
                        // The frame drawn last presented again (a copy, no render): the swap chain keeps its beat, so the
                        // next turn wakes at the next refresh and a new frame is on screen at the first one after its time.
                        var kept = _viewport.PresentNewest(vsync: true, force: true);
                        (owedTurn, presentedLast) = (!kept, kept);
                        continue;
                    }

                    Volatile.Write(ref _sceneDirty, 0);
                    _lastPlayedFrame = frame;
                    var sequence = PublishAt(plan, at, playing: true);
                    if (_primingSince != 0 && _primingScene == 0)
                        _primingScene = sequence;

                    // The cuts ahead (rewritten only when one is passed or another comes into the horizon).
                    LeaveHints(plan, at, playing: true, hints);
                    if (sequence > 0)
                        _ = _client.WaitForFrame(sequence, Math.Max(12, (int)(_refresh * 1000)));
                    owedTurn = !(_viewport.PresentNewest(vsync: true) || _viewport.PresentNewest(vsync: true, force: true));
                    presentedLast = !owedTurn;
                    continue;
                }

                _lastPlayedFrame = long.MinValue;

                // Paused with the camera moving (the 3D exploded view orbiting or opening): drawn every refresh, as playing.
                var moving = false;
                CameraMoving(ref moving);
                if (moving)
                {
                    if (owedTurn)
                        Thread.Sleep(1);
                    else
                        _viewport.WaitForTurn(50);
                    var sequence = PublishAt(Volatile.Read(ref _plan), Volatile.Read(ref _pausedAt), playing: false);
                    if (sequence > 0)
                        _ = _client.WaitForFrame(sequence, Math.Max(12, (int)(_refresh * 1000)));
                    owedTurn = !(_viewport.PresentNewest(vsync: true) || _viewport.PresentNewest(vsync: true, force: true));
                    continue;
                }

                owedTurn = false;

                // Paused: only what changed is drawn; the worker's own redraws (an exact frame after a stand-in) are shown.
                // The wait ends when the pointer comes to rest, for its play-ahead.
                var woke = WaitHandle.WaitAny(waits, PausedWaitMs());
                if (woke == 1)
                    return;
                if (_playing)
                    continue;
                var pausedPlan = Volatile.Read(ref _plan);
                var pausedAt = Volatile.Read(ref _pausedAt);
                if (woke == 0 && Interlocked.Exchange(ref _sceneDirty, 0) != 0)
                    PublishAt(pausedPlan, pausedAt, playing: false);
                LeaveHints(pausedPlan, pausedAt, playing: false, hints);

                if (_viewport.HasNewFrame)
                    _viewport.PresentNewest(vsync: false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never out of this thread: logged, and the next turn tries again.
                AppLog.Write("GPU frame pump", ex);
                _stop.WaitOne(100);
            }
        }
    }

    /// <summary>Writes and publishes the scene at a moment; 0 when there is nothing to show it on (a hidden player).</summary>
    // The timeline's own rate (frames a second, 0 unknown), and the frame of it drawn last while playing.
    private double _canvasFps;
    private long _lastPlayedFrame = long.MinValue;

    /// <summary>The timeline frame a moment is in, at the canvas's own rate; long.MinValue when the rate is unknown (every refresh is drawn).</summary>
    private long CanvasFrameOf(double seconds)
    {
        var fps = Volatile.Read(ref _canvasFps);
        return fps > 0 && double.IsFinite(seconds) ? (long)Math.Floor(seconds * fps + 1e-6) : long.MinValue;
    }

    private long PublishAt(GpuScenePlan plan, double seconds, bool playing)
    {
        var (width, height) = _viewport.PixelSize;
        if (width < 8 || height < 8)
            return 0;

        // The end of the sequence shows its last frame, not what comes after it: nothing.
        if (plan.SequenceSeconds > 0.002 && seconds > plan.SequenceSeconds - 0.001)
            seconds = plan.SequenceSeconds - 0.001;

        _client.ClearScene();
        ref var header = ref _client.Scene;
        var layers = MemoryMarshal.CreateSpan(ref _client.Layer(0), CompositorProtocol.MaxLayers);
        if (_sourceView)
            plan.EvaluateSource(seconds, Volatile.Read(ref _sourceKey), ref header, layers);
        else
            plan.Evaluate(seconds, ref header, layers);
        header.PlayheadSeconds = seconds;
        header.IsPlaying = playing ? 1 : 0;
        (header.OutputWidth, header.OutputHeight) = (width, height);
        header.Flags |= FrameSceneHeader.FlagLetterbox;
        if (!_sourceView)
            WriteAudioBands(ref header, seconds, playing);
        (header.ViewScale, header.ViewPanX, header.ViewPanY) = (_viewScale, _viewPanX, _viewPanY);
        if (!_sourceView && Volatile.Read(ref _loupeKey) is var loupe and not 0)
            (header.LoupeClipKey, header.LoupeKind, header.LoupeZoom) = (loupe, Volatile.Read(ref _loupeKind), LoupeZoom);
        if (header.CanvasWidth <= 0 || header.CanvasHeight <= 0)
            (header.CanvasWidth, header.CanvasHeight) = (1920, 1080);
        if (!_sourceView)
            WriteCamera(ref header, layers);
        return _client.Publish();
    }

    // ----- The source as it is (a rectangle drawn on it, Live Preview off) -----

    private volatile bool _sourceView;

    // The clip whose picture the source view shows (GpuHash.Of its id); 0 for the bottom video at the playhead.
    private long _sourceKey;

    /// <summary>
    /// Show a clip's own picture, whole and unfinished, instead of the sequence (true while a crop or a Cutout's place is drawn
    /// on a clip's source, and with Live Preview off): its file itself (5.2-AuditB: any clip's, where it was the primary
    /// file's; GpuScenePlan.EvaluateSource). <paramref name="clipId"/> is the clip; empty for the bottom video at the playhead.
    /// </summary>
    public void ShowSource(bool source, Guid clipId = default)
    {
        var key = clipId == Guid.Empty ? 0 : GpuHash.Of(clipId);
        if (_sourceView == source && Interlocked.Read(ref _sourceKey) == key)
            return;
        Interlocked.Exchange(ref _sourceKey, key);
        _sourceView = source;
        Invalidate();
    }

    // ----- One picture of the frame, read back (Export Frame) -----

    private TaskCompletionSource<(byte[] Bgra, int Width, int Height)?>? _capture;

    // The state the plan was made from, and the plan a capture draws (the originals', never a proxy's: Export Frame).
    private SequenceExportState? _state;
    private GpuScenePlan? _capturePlan;
    private double _captureAt = double.NaN;

    /// <summary>
    /// The frame at the playhead as the export makes it: the canvas's own size, no letterbox, no zoom, no 3D view, every
    /// exact frame, from the original files (a clip optimized for scrubbing shows its original, not its proxy), BGRA8
    /// (alpha meaningless). Null when it could not be read. Called on the UI thread (a plan of the originals is made
    /// there when the player's reads proxies); drawn on the pump's thread.
    /// </summary>
    /// <param name="at">The moment of the sequence to draw; null for the playhead.</param>
    public Task<(byte[] Bgra, int Width, int Height)?> CaptureFrameAsync(double? at = null)
    {
        var request = new TaskCompletionSource<(byte[], int, int)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        GpuScenePlan? originals = null;
        if (_state is { IsPreview: true } state)
        {
            try
            {
                originals = GpuScenePlan.Create(state with { IsPreview = false }, _broker);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("GPU engine: the frame's plan of the original files could not be made (the player's is used)", ex);
            }
        }

        Volatile.Write(ref _capturePlan, originals);
        Interlocked.Exchange(ref _captureAt, at ?? double.NaN);
        Interlocked.Exchange(ref _capture, request)?.TrySetResult(null);
        _dirty.Set();
        return request.Task;
    }

    private void Capture(TaskCompletionSource<(byte[] Bgra, int Width, int Height)?> request)
    {
        try
        {
            var plan = Interlocked.Exchange(ref _capturePlan, null) ?? Volatile.Read(ref _plan);
            var wanted = Interlocked.Exchange(ref _captureAt, double.NaN);
            var at = Math.Min(double.IsNaN(wanted) ? Position : Math.Max(wanted, 0), Math.Max(plan.SequenceSeconds - 0.001, 0));
            _client.ClearScene();
            ref var header = ref _client.Scene;
            var layers = MemoryMarshal.CreateSpan(ref _client.Layer(0), CompositorProtocol.MaxLayers);
            plan.Evaluate(at, ref header, layers);
            header.PlayheadSeconds = at;
            (header.OutputWidth, header.OutputHeight) = (Math.Max(header.CanvasWidth, 16), Math.Max(header.CanvasHeight, 16));
            header.Flags |= FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback;
            var bytes = (long)header.OutputWidth * header.OutputHeight * 4;
            _client.EnsureReadback(bytes);
            var pixels = new byte[bytes];
            var sequence = _client.Publish();

            // The stop event ends the wait (none, at once): a player closed during it is never held past Dispose's join.
            request.TrySetResult(_client.TryReadback(sequence, pixels, out var width, out var height, 20_000, _stop) ? (pixels, width, height) : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU engine: the frame could not be read back", ex);
            request.TrySetResult(null);
        }
        finally
        {
            // The player's own picture again.
            Invalidate();
        }
    }

    // ----- A small picture of the frame for the project file (Pass 5.2D, ProjectThumbnail) -----

    private TaskCompletionSource<(byte[] Bgra, int Width, int Height)?>? _thumbnail;

    // The plan a thumbnail draws when the player's has fades (the same state without them), and the short side asked for.
    private GpuScenePlan? _thumbnailPlan;
    private int _thumbnailShortSide = ProjectThumbnail.ShortSide;

    /// <summary>How long the pump waits for a thumbnail to be read back, ms: one that takes longer gives none (the save keeps the last one).</summary>
    private const int ThumbnailReadbackMs = 3000;

    /// <summary>
    /// The frame at the playhead as the player composes it (its proxies, its captions), without the fades (a project that fades in is not
    /// black at its start), drawn small: the short side <paramref name="shortSide"/> (never more than the canvas's), both sides even, BGRA8
    /// (alpha meaningless). Its own readback, the size of that picture: the player's is not grown to the canvas for it. A project file's
    /// thumbnail (ProjectThumbnail). Null at once while playing (no played frame ever waits for one: Law 3) or with nothing loaded, and
    /// null when it could not be drawn in time. Called on the UI thread (a plan without the fades is made there when the player's has
    /// them); drawn on the pump's thread, and completed off both.
    /// </summary>
    public Task<(byte[] Bgra, int Width, int Height)?> CaptureThumbnailAsync(int shortSide = ProjectThumbnail.ShortSide)
    {
        if (_playing || _state is not { } state)
            return Task.FromResult<(byte[] Bgra, int Width, int Height)?>(null);

        var request = new TaskCompletionSource<(byte[] Bgra, int Width, int Height)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        GpuScenePlan? unfaded = null;
        if (state.FadeIn || state.FadeOut)
        {
            try
            {
                unfaded = GpuScenePlan.Create(state with { FadeIn = false, FadeOut = false }, _broker);
                unfaded.PrepareAround(Position);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("GPU engine: the thumbnail's plan without the fades could not be made (the player's is used)", ex);
                unfaded = null;
            }
        }

        Volatile.Write(ref _thumbnailPlan, unfaded);
        Volatile.Write(ref _thumbnailShortSide, Math.Max(shortSide, 16));
        Interlocked.Exchange(ref _thumbnail, request)?.TrySetResult(null);
        _dirty.Set();
        return request.Task;
    }

    private void CaptureThumbnail(TaskCompletionSource<(byte[] Bgra, int Width, int Height)?> request)
    {
        var unfaded = Interlocked.Exchange(ref _thumbnailPlan, null);

        // Asked for while paused and playing by now: none, so that the played frame of this turn is not late for it.
        if (_playing)
        {
            request.TrySetResult(null);
            return;
        }

        try
        {
            var plan = unfaded ?? Volatile.Read(ref _plan);
            var at = Math.Min(Position, Math.Max(plan.SequenceSeconds - 0.001, 0));
            _client.ClearScene();
            ref var header = ref _client.Scene;
            var layers = MemoryMarshal.CreateSpan(ref _client.Layer(0), CompositorProtocol.MaxLayers);
            plan.Evaluate(at, ref header, layers);
            header.PlayheadSeconds = at;

            // Smaller than the canvas, in its shape: without a letterbox the compositor stretches the canvas over the output.
            (header.OutputWidth, header.OutputHeight) = ProjectThumbnail.ThumbnailSize(header.CanvasWidth, header.CanvasHeight, Volatile.Read(ref _thumbnailShortSide));
            header.Flags |= FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback;
            var bytes = (long)header.OutputWidth * header.OutputHeight * 4;
            _client.EnsureReadback(bytes);
            var pixels = new byte[bytes];
            var sequence = _client.Publish();

            // Ended by the stop event as Export Frame's is (a save racing the player's close gets none, at once).
            request.TrySetResult(_client.TryReadback(sequence, pixels, out var width, out var height, ThumbnailReadbackMs, _stop) && width > 0 && height > 0
                ? (pixels, width, height)
                : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU engine: the project's thumbnail could not be drawn", ex);
            request.TrySetResult(null);
        }
        finally
        {
            // The player's own picture again.
            Invalidate();
        }
    }

    /// <summary>The 3D exploded view's camera and the layers' depth and marks, written into each scene (GpuPreviewEngine.Exploded.cs).</summary>
    partial void WriteCamera(ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers);

    /// <summary>Whether the camera is moving and the paused player must be drawn every refresh (GpuPreviewEngine.Exploded.cs).</summary>
    partial void CameraMoving(ref bool moving);

    /// <summary>A scrub is running: the playhead was sent somewhere within this long, seconds (the pointer's work waits).</summary>
    private const double ScrubbingFor = 0.25;

    /// <summary>
    /// The worker's hints (Instant Seek; GpuHintWriter): every cut within the lookahead horizon of the playhead, playing or
    /// not; paused, also where a scrub is heading (its speed carried 120 ms ahead) and, when no scrub runs, the pointer over
    /// a timeline (its frame while it moves, its play-ahead once it rests). Written to the worker only when they changed.
    /// The playhead's own neighbourhood the worker decodes by itself (its pre-roll, its play-ahead).
    /// </summary>
    private void LeaveHints(GpuScenePlan plan, double at, bool playing, Span<GpuSeekHint> hints)
    {
        if (!ReferenceEquals(plan, _hintedPlan))
        {
            // A new plan may have put its files in other entries of the source table: written again whatever they say.
            _hintWriter.Reset();
            _hintedPlan = plan;
        }

        var now = Stopwatch.GetTimestamp();
        var (scrubAhead, pointer, resting) = (double.NaN, double.NaN, false);
        var scrubbing = !playing && (now - _lastSeekTicks) * 1.0 / Stopwatch.Frequency < ScrubbingFor;
        if (scrubbing)
        {
            if (Math.Abs(_scrubVelocity) > 0.5)
                scrubAhead = at + _scrubVelocity * 0.12;
        }
        else if (!playing)
        {
            pointer = Volatile.Read(ref _pointer);
            resting = (now - Volatile.Read(ref _pointerMoved)) * 1.0 / Stopwatch.Frequency >= GpuHintWriter.HoverRest;
        }

        // A file opened ahead of a drop: its first frames, after everything else the worker does (never while playing).
        var extra = default(GpuSeekHint);
        if (!playing && Volatile.Read(ref _prewarm) is { } file && now < file.Until)
            extra = new GpuSeekHint { SourceId = file.Source, Kind = (int)GpuSeekHintKind.HoverRoll, PathHash = file.PathHash, Span = 0.2f };

        // The cuts ahead as if Space were pressed: not while a scrub runs (they would move with every step of it).
        var rate = playing ? _rate : scrubbing ? 0 : _rate > 0 ? _rate : 1;
        if (_hintWriter.Write(plan, at, rate, hints, out var count, scrubAhead, pointer, resting, extra, Volatile.Read(ref _loopFrom), Volatile.Read(ref _loopTo)))
            _client.SetHints(hints[..count]);
    }

    // Where Loop takes playback back from and to (seconds; NaN: no Loop; from infinite: the sequence's end), for the hints.
    private double _loopFrom = double.NaN, _loopTo = double.NaN;

    /// <summary>
    /// Where Loop takes playback back to (<paramref name="to"/>, NaN: Loop off) when it reaches <paramref name="from"/>
    /// (infinity: the sequence's end), so that the cuts there are made ready before the wrap (any thread).
    /// </summary>
    public void SetLoop(double from, double to)
    {
        Volatile.Write(ref _loopFrom, from);
        Volatile.Write(ref _loopTo, to);
    }

    /// <summary>How long the paused pump waits for something to change: until the pointer counts as resting, or a scrub as over, at most 250 ms.</summary>
    private int PausedWaitMs()
    {
        var now = Stopwatch.GetTimestamp();
        var wait = 0.25;
        if (!double.IsNaN(Volatile.Read(ref _pointer)))
        {
            var rest = GpuHintWriter.HoverRest - (now - Volatile.Read(ref _pointerMoved)) * 1.0 / Stopwatch.Frequency;
            if (rest > 0)
                wait = Math.Min(wait, rest);
        }

        var scrub = ScrubbingFor - (now - _lastSeekTicks) * 1.0 / Stopwatch.Frequency;
        if (scrub > 0)
            wait = Math.Min(wait, scrub);
        return Math.Clamp((int)Math.Ceiling(wait * 1000), 1, 250);
    }

    // ----- The audio-reactive shader bus (Pass 5.2B) -----
    // While a card pulses with the sound (AudioReactive.Pulses), a map of the bands of the Sequence's sound (as an export hears it) is made in
    // the background, and made again only when what is heard changes (AudioReactive.KeyOf): a frame drawn paused or scrubbed carries the
    // bands the export's frame will. Until the map reaches a moment, a frame played carries the engine's own bands of what it plays.

    private readonly object _energiesGate = new();
    private AudioEnergyMap? _energies;
    private CancellationTokenSource? _energiesMaking;
    private int _energiesKey, _energiesVersion;
    private volatile bool _pulses;

    /// <summary>
    /// The state's map (UI thread, SetState): kept while what is heard is the same, made again in the background when not, none when nothing
    /// pulses. <paramref name="heard"/> is the program SetState built when it is the one an export hears (nothing soloed), else null for
    /// one made here.
    /// </summary>
    private void PlanEnergies(SequenceExportState state, AudioProgram? heard)
    {
        if (!AudioReactive.Pulses(state))
        {
            ForgetEnergies();
            return;
        }

        _pulses = true;
        var program = heard ?? AudioReactive.ProgramOf(state);
        var key = AudioReactive.KeyOf(program);
        if (key == _energiesKey && _energiesMaking is not null)
            return;

        _energiesMaking?.Cancel();
        var making = new CancellationTokenSource();
        (_energiesMaking, _energiesKey) = (making, key);
        int version;
        lock (_energiesGate)
            version = ++_energiesVersion;
        _ = Task.Run(() =>
        {
            try
            {
                // Filled from the Sequence's start, and drawn with as it fills (TryAt knows what is made). Published only while this is still
                // the newest plan, under the gate the version moves under, so a task overtaken never covers a newer map (or a forgotten one's null).
                var map = AudioEnergyMap.Of(0, program.SequenceSeconds);
                lock (_energiesGate)
                {
                    if (_energiesVersion != version)
                        return;
                    Volatile.Write(ref _energies, map);
                }

                map.Fill(program, making.Token);
                Invalidate();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("GPU engine: the sound's bands could not be measured; the audio-reactive effects follow the playing sound only", ex);
            }
        });
    }

    private void ForgetEnergies()
    {
        _pulses = false;
        _energiesMaking?.Cancel();
        (_energiesMaking, _energiesKey) = (null, 0);
        lock (_energiesGate)
        {
            _energiesVersion++;
            Volatile.Write(ref _energies, null);
        }
    }

    // The bands at a frame's moment (the pump): the map's where it is made (as the export looks them up), else, playing, the engine's own.
    private void WriteAudioBands(ref FrameSceneHeader header, double seconds, bool playing)
    {
        if (!_pulses || GpuExport.WriteBands(ref header, Volatile.Read(ref _energies), seconds) || !playing)
            return;
        (header.AudioBass, header.AudioMid, header.AudioHigh) = _audio.LiveBands;
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current, this))
            Current = null;
        ForgetEnergies();

        // The stop event also ends a frame's or a thumbnail's readback wait within a slice (TryReadback's abort), so the pump is
        // out well inside the join (Pass 5.2D).
        _stop.Set();
        var pumpEnded = _pump.Join(2000);

        // A frame or a thumbnail asked for and not drawn (the engine stopped first): none, so that what waits on it goes on.
        Interlocked.Exchange(ref _capture, null)?.TrySetResult(null);
        Interlocked.Exchange(ref _thumbnail, null)?.TrySetResult(null);
        if (!pumpEnded)
        {
            // Still inside a call that has not returned (a driver hung in a present): nothing it reads is let go under it. The
            // control plane unmapped, or the swap chain released, under a running thread is an access violation, not an
            // exception it could catch, and its events disposed would throw out of its loop. The workers are ended and the sound
            // stopped; the memory, the viewport and the events go with the process.
            AppLog.Write("GPU engine: the frame pump did not stop within 2 s; the compositor's memory, the viewport and the pump's events are kept");
            _audio.Dispose();
            _broker.Dispose();
            _client.DisposeKeepingMemory();
            return;
        }

        _viewport.Dispose();
        _audio.Dispose();
        _broker.Dispose();
        _client.Dispose();
        _workerFrame?.Dispose();
        _dirty.Dispose();
        _stop.Dispose();
    }
}
