using System.Diagnostics;
using System.Runtime.InteropServices;
using HandPegApp.Models;
using HandPegApp.Services.Audio;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>
/// GPU preview engine behind the player. Four non-blocking parts:
/// <list type="bullet">
/// <item>compositor worker (<see cref="CompositorClient"/>): renders <see cref="GpuScenePlan"/> scenes, GPU decode with Instant Seek;</item>
/// <item>WASAPI mixer (<see cref="WasapiAudioEngine"/>): its device clock is the master clock during playback;</item>
/// <item>viewport (<see cref="GpuViewport"/>) on the video surface HWND;</item>
/// <item>frame pump thread: playing, it waits on the swap chain, evaluates the plan at display time (audio clock + display lead),
/// publishes and presents; paused, it publishes only changes and presents worker redraws. It also writes seek hints (GpuHintWriter).</item>
/// </list>
/// <para>Threads: <see cref="SetState"/> and <see cref="PrepareAround"/> on the UI thread (WPF text rasterization); all else any
/// thread. The pump is the sole writer of scenes and hints.</para>
/// </summary>
public sealed partial class GpuPreviewEngine : IDisposable
{
    private readonly CompositorClient _client;
    private readonly WasapiAudioEngine _audio;
    private readonly GpuViewport _viewport;
    private readonly GpuResourceBroker _broker;
    private readonly Thread _pump;
    private readonly AutoResetEvent _dirty = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly EventWaitHandle? _workerFrame;
    private GpuScenePlan _plan = GpuScenePlan.Empty;
    private volatile bool _playing;
    private double _pausedAt;
    private double _rate = 1;

    // Player zoom/pan and timeline hover (time and last-move Stopwatch ticks).
    private float _viewScale = 1, _viewPanX, _viewPanY;
    private double _pointer = double.NaN;
    private long _pointerMoved;

    // The worker's hints, written by the pump alone (only when they change), and the plan they were written from.
    private readonly GpuHintWriter _hintWriter = new();
    private GpuScenePlan? _hintedPlan;

    // A file opened ahead of a drop (Prewarm): its source, and until when (Stopwatch ticks) its first frames are asked for.
    private sealed record PrewarmedFile(int Source, long PathHash, long Until);
    private PrewarmedFile? _prewarm;

    // Tab pre-roll requests (PrewarmTabs): playhead frames and their expiry (Stopwatch ticks).
    private sealed record PrewarmedTabs(GpuSeekHint[] Hints, long Until);
    private PrewarmedTabs? _tabPrewarm;

    /// <summary>Tab pre-roll length past the playhead, file seconds (capped by the worker's HoverRoll).</summary>
    public const double TabPreRollSeconds = 0.5;

    /// <summary>Seconds a pre-opened drop candidate stays requested before release.</summary>
    private const double PrewarmSeconds = 10;

    // 1 when visible content changed and the paused pump must redraw; hover only updates hints and wakes the pump without a redraw.
    private int _sceneDirty = 1;

    // Scrub direction from recent seek targets and times. Written by Seek (UI thread), read by the pump via Volatile (gates tab pre-roll).
    private double _lastSeekAt = double.NaN;
    private long _lastSeekTicks;
    private double _scrubVelocity;

    // The display's refresh interval while playing (measured between the swap chain's turns), seconds.
    private double _refresh = 1 / 60.0;

    private GpuPreviewEngine(CompositorClient client, WasapiAudioEngine audio, nint surface, bool player)
    {
        (_client, _audio, _player) = (client, audio, player);

        // Player only: lean-park inactive tabs on unified memory or battery (worker and standby share the control block). Strip, export,
        // bench, test and A/V Sync Test engines never request it.
        if (player)
            client.TabParking = 1;
        _viewport = new GpuViewport(client, surface);
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
        if (player)
            Current = this;
    }

    /// <summary>Active player engine (null when stopped); used by the Settings cache readout.</summary>
    public static GpuPreviewEngine? Current { get; private set; }

    /// <summary>The swap chain it presents on (the Playback &amp; A/V Sync Test's sampler reads its frame statistics).</summary>
    internal GpuViewport Viewport => _viewport;

    /// <summary>
    /// Starts an engine for a player surface: worker and standby (off the UI thread), audio device, viewport, pump. Returns null
    /// when the compositor cannot start (no D3D11 adapter, immediate worker failure) with <see cref="LastStartFailure"/> set
    /// (MainWindow.OnGpuFailed retries). Uses the Compositor GPU role, or <paramref name="adapter"/> (DXGI index, e.g. WARP fallback).
    /// Call after Frame 1.
    /// <para><paramref name="player"/> false (A/V Sync Test): secondary engine, never <see cref="Current"/>; no standby, small cache,
    /// no tab parking, and its adapter does not define Auto.</para>
    /// </summary>
    public static async Task<GpuPreviewEngine?> StartAsync(nint surface, int? adapter = null, bool player = true)
    {
        // Wait for any engine still disposing (DisposeInBackground): an HWND allows one flip-model swap chain at a time.
        await Volatile.Read(ref _teardown);
        var settings = AppSettings.Current;
        var card = adapter ?? settings.CompositorGpuAdapter;
        var budget = !player ? 64L << 20 : settings.GpuFrameCacheMb switch { < 0 => -1L, 0 => 0L, var mb => (long)mb << 20 };
        var threads = settings.GpuDecodeThreads;
        var started = await Task.Run(() =>
        {
            CompositorClient? client = null;
            try
            {
                client = new CompositorClient(card, keepStandby: player, budget, threads, softwareDecode: !settings.PlayerHardwareAcceleration);
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

                // The render adapter defines Auto for background work (never an automatic secondary GPU); WARP excluded.
                if (player && client.Memory.Control.IsSoftwareDevice == 0 && client.Memory.Control.AdapterIndex >= 0)
                    GpuAdapters.CompositorIndex = client.Memory.Control.AdapterIndex;
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

        return started is { } parts ? new GpuPreviewEngine(parts.Item1, parts.Item2, surface, player) : null;
    }

    /// <summary>Raised on the pump's thread when playback reaches the end of the sequence (the engine is paused there).</summary>
    public event Action? Ended;

    /// <summary>Why the last <see cref="StartAsync"/> gave no engine, for the player's notice.</summary>
    public static string LastStartFailure { get; private set; } = "";

    /// <summary>Raised on the pump thread when the supervisor stops respawning the compositor (MainWindow.OnGpuFaulted).</summary>
    public event Action<string>? Faulted;

    public CompositorClient Client => _client;

    public WasapiAudioEngine Audio => _audio;

    public bool IsPlaying => _playing;

    public double SequenceSeconds => Volatile.Read(ref _plan).SequenceSeconds;

    /// <summary>Master clock in sequence seconds: audio device position while playing, else the paused time.</summary>
    public double Position => _playing ? PlayingPosition() : SeekMailSlot.PeekOr(ref _pendingSeekBits, Volatile.Read(ref _pausedAt));

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

    /// <summary>Applies all window inputs (<see cref="PlayerInputs"/>) to a new engine before its first frame.</summary>
    public void Apply(PlayerInputs inputs)
    {
        Volume = inputs.Volume;
        Muted = inputs.Muted;
        Rate = inputs.Rate;
        SetView(inputs.ZoomLog2, inputs.PanX, inputs.PanY);
        SetGradeCompare(inputs.GradeCompare, inputs.GradeWipe);
    }

    /// <summary>The frame cache's counters (Settings' readout, diagnostics).</summary>
    public FrameCacheStats CacheStats => _client.CacheStats;

    // ----- What it shows -----

    /// <summary>
    /// Installs the current Sequence (UI thread): rebuilds the scene plan and audio program and redraws at the playhead.
    /// <paramref name="audio"/> carries non-state inputs (solo, MainViewModel.Soloed).
    /// </summary>
    public void SetState(SequenceExportState state, AudioBuildOptions? audio = null)
    {
        _uiOwner.Check();
        try
        {
            var plan = GpuScenePlan.Create(state, _broker);
            ReleaseUnusedKeptSources();
            var canvasMoved = plan.CanvasWidth != _plan.CanvasWidth || plan.CanvasHeight != _plan.CanvasHeight;
            Volatile.Write(ref _plan, plan);
            _state = state;
            if (canvasMoved)
                CanvasChanged?.Invoke();
            Volatile.Write(ref _canvasFps, state.Timeline.FrameRate.IsValid ? state.Timeline.FrameRate.Fps : 0);
            var heard = audio is null ? AudioProgram.Build(state) : AudioProgram.Build(state, audio);
            _audio.SetProgram(heard);
            // Reactive bands follow the export mix (all tracks). Without solo the program just built is that mix (immutable), so reuse it.
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
    /// Project closed (UI thread): resets to an empty plan and audio program with no hover or prewarm, so hints reference no closed
    /// files and idle decoders are released (DecoderPool.Tidy). Also clears the Instant Seek cache and resets its budget to baseline.
    /// </summary>
    public void Unload()
    {
        Empty();
        _client.ClearFrameCache();
    }

    /// <summary>Shared reset for <see cref="Unload"/> and <see cref="Suspend"/> (UI thread), excluding worker cache handling.</summary>
    private void Empty()
    {
        Pause();
        Volatile.Write(ref _plan, GpuScenePlan.Empty);
        _state = null;
        Volatile.Write(ref _canvasFps, 0);
        _audio.SetProgram(AudioProgram.Empty);
        ForgetEnergies();
        Volatile.Write(ref _prewarm, null);
        Volatile.Write(ref _tabPrewarm, null);
        Volatile.Write(ref _pointer, double.NaN);
        Volatile.Write(ref _pointerMoved, Stopwatch.GetTimestamp());
        Invalidate();
    }

    /// <summary>Delay (ms) before a second idle audio-decoder release after <see cref="Suspend"/>, catching voices retired late.</summary>
    private const int ReleaseAgainMs = 1500;

    /// <summary>
    /// Tab deactivation (UI thread, MainWindow.ProjectTabs.cs): resets like <see cref="Unload"/>, then releases project resources eagerly:
    /// clears the worker source table (GpuResourceBroker.ReleaseAll), requests a worker trim (CompositorClient.TrimWorker; driver Trim
    /// is idle-deferred so rapid switches coalesce) and disposes idle audio decoders now and again shortly after.
    /// Cached frames are the worker's decision (CompositorWorker.TrimEmptiesCache): emptied on unified memory or small cards, kept LRU on
    /// large cards for the returning tab. No clear is sent here, so the switch never pays for freeing a full cache. Engine, worker and
    /// audio device stay up for the next tab's SetState. Non-blocking: all requests are counters.
    /// <para>Sources named by tab pre-roll are kept for the incoming tab (the next SetState releases unused ones), and the worker keeps its
    /// working set when a scene follows the trim.</para>
    /// </summary>
    public void Suspend()
    {
        var prewarmed = Volatile.Read(ref _tabPrewarm) is { } tabs && Stopwatch.GetTimestamp() < tabs.Until ? tabs : null;
        Empty();
        ReleaseSourcesForSwitch(prewarmed);
        _client.TrimWorker();
        _audio.ReleaseIdleDecoders();
        var audio = _audio;
        _ = Task.Delay(ReleaseAgainMs).ContinueWith(_ =>
        {
            try
            {
                audio.ReleaseIdleDecoders();
            }
            catch (InvalidOperationException)
            {
                // The engine stopped meanwhile (an object disposed): its mixer and decoders went with it.
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Monotonic sequence number of the newest presented scene; overlay stills wait for one newer than their own scene.</summary>
    public long PresentedScene => Volatile.Read(ref _client.Memory.Control.PresentedSequence);

    /// <summary>Whether the newest scene presented showed every video's exact frame (not a stand-in from the cache).</summary>
    public bool PresentedExact => Volatile.Read(ref _client.Memory.Control.PresentedExact) != 0;

    /// <summary>
    /// True when the worker adapter uses unified memory (integrated or ARM64 GPU); false until Ready. Written once by the worker; any thread.
    /// </summary>
    public bool UnifiedMemory => _client.UnifiedMemory;

    // Scene marks: newest requested (UI thread), the value the pump read before the plan and playhead (pump thread), and the newest
    // mark a published scene satisfied plus that scene's number (pump; number written first).
    private long _sceneMark, _turnMark, _markPublished, _markedScene;

    /// <summary>
    /// Takes a scene mark (UI thread) after a change that must be shown before any stand-in (tab activation); <see cref="PresentedSince"/>
    /// reports when a later scene was presented. Requests a redraw.
    /// </summary>
    public long MarkScene()
    {
        var mark = Interlocked.Increment(ref _sceneMark);
        Invalidate();
        return mark;
    }

    /// <summary>
    /// True once the viewport has presented (IDXGISwapChain::Present, not just worker render) a scene built after <paramref name="mark"/>
    /// (<see cref="MarkScene"/>), never an in-flight older scene such as the previous tab's frame.
    /// </summary>
    public bool PresentedSince(long mark) => mark > 0 && Volatile.Read(ref _markPublished) >= mark && _viewport.PresentedSequence >= Volatile.Read(ref _markedScene);

    /// <summary>
    /// Sets player captions (UI thread): timed lines, style and text scale matching the exported .ass; null for none.
    /// Rasterized near the playhead by <see cref="PrepareAround"/>.
    /// </summary>
    public void SetCaptions(IReadOnlyList<IReadOnlyList<CaptionWord>>? lines, CaptionStyle? style, double textScale)
    {
        _broker.Text.SetCaptions(lines, style, textScale);
        Volatile.Read(ref _plan).PrepareAround(Position);
        Invalidate();
    }

    /// <summary>Applies audio settings (device, buffer, sync offset); reopens the device only when it changed.</summary>
    public void ApplyAudioSettings(AppSettings settings) =>
        _audio.ApplySettings(settings.AudioOutputDeviceId, settings.AudioBufferMs, settings.AudioSyncOffsetMs);

    /// <summary>Rasterizes the captions near the playhead (UI thread, a couple of milliseconds a call): called as the playhead moves.</summary>
    public void PrepareAround(double sequenceSeconds)
    {
        _uiOwner.Check();
        Volatile.Read(ref _plan).PrepareAround(sequenceSeconds);
    }

    // The UI thread, which owns SetState, PrepareAround, Prewarm and PrewarmTabs (the first of them to be called takes it).
    private readonly ThreadOwner _uiOwner = new();

    /// <summary>Draw again (the window was resized, the DPI changed, a resource arrived).</summary>
    public void Invalidate()
    {
        Volatile.Write(ref _loopbackStale, 1);
        Volatile.Write(ref _sceneDirty, 1);
        _dirty.Set();
    }

    /// <summary>The player's zoom and pan (MainWindow's player navigation: 2^zoom, pan as fractions of the fitted picture).</summary>
    public void SetView(double zoomLog2, double panX, double panY)
    {
        (_viewScale, _viewPanX, _viewPanY) = ((float)Math.Pow(2, zoomLog2), (float)panX, (float)panY);
        InvalidateView();
    }

    /// <summary>
    /// Redraws for a placement-only change (zoom, pan, resize); the Export Quality Preview content stamp is kept, so the paused frame
    /// is not re-encoded.
    /// </summary>
    public void InvalidateView()
    {
        Volatile.Write(ref _sceneDirty, 1);
        _dirty.Set();
    }

    /// <summary>Edge loupe magnification relative to the player scale (400 %).</summary>
    public const float LoupeZoom = 4;

    private long _loupeKey;
    private int _loupeKind;

    /// <summary>
    /// Edge loupe: while an edge of clip <paramref name="clipKey"/> (GpuHash.Of its id) is tuned, shows it at 400 % in a corner
    /// (SceneRenderer.Loupe.cs); 0 hides it. <paramref name="kind"/>: 1 feather, 2 mask, 3 matte, 4 key (FrameSceneHeader.LoupeKind).
    /// </summary>
    public void SetLoupe(long clipKey, int kind)
    {
        if (Interlocked.Exchange(ref _loupeKey, clipKey) == clipKey && Interlocked.Exchange(ref _loupeKind, kind) == kind)
            return;
        Volatile.Write(ref _loupeKind, kind);
        Invalidate();
    }

    /// <summary>
    /// Timeline hover time (null when the pointer leaves; updated up to ~30 Hz). While paused, pre-decodes its frame and, once at
    /// rest (GpuHintWriter.HoverRest), a short play-ahead. Ignored while playing or scrubbing.
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
    /// Pre-opens a likely drop target on a fast local drive (UI thread, MainWindow.Drops): adds it to the source table and lets the
    /// worker decode its first frames at lowest priority for up to <see cref="PrewarmSeconds"/>; null releases it. No-op while playing.
    /// </summary>
    public void Prewarm(string? path)
    {
        _uiOwner.Check();
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

    /// <summary>
    /// Pre-rolls likely next tabs (next in Ctrl+Tab order, hovered tab): each visible video's playhead frame plus
    /// <see cref="TabPreRollSeconds"/>, decoded at idle priority (never while playing) so a switch finds frames and decoders ready.
    /// Requests expire after <see cref="PrewarmSeconds"/>; an empty list releases them. UI thread (owns the source table).
    /// </summary>
    /// <param name="frames">Per video: file, video stream index (0 first) and file time at the tab's playhead.</param>
    public void PrewarmTabs(IReadOnlyList<(string Path, int Stream, double MediaSeconds)> frames)
    {
        _uiOwner.Check();
        var hints = new List<GpuSeekHint>(frames.Count);
        foreach (var (path, stream, media) in frames)
        {
            var source = _broker.SourceFor(path, stream);
            if (source >= 0 && double.IsFinite(media))
            {
                // Idle-priority worker work, only while the player is quiet.
                hints.Add(new GpuSeekHint
                {
                    SourceId = source, Kind = (int)GpuSeekHintKind.TabRoll, PathHash = _broker.PathHashOf(source),
                    MediaSeconds = Math.Max(media, 0), Span = (float)TabPreRollSeconds,
                });
            }
        }

        Volatile.Write(ref _tabPrewarm, hints.Count == 0 ? null : new PrewarmedTabs([.. hints], Stopwatch.GetTimestamp() + (long)(PrewarmSeconds * Stopwatch.Frequency)));
        if (!_playing)
            _dirty.Set();
    }

    // ----- The clock -----

    // ----- Transport: Play, Pause, Seek, priming hand-off, end of Sequence -----
    // UI thread calls Play/Pause/Seek; the pump auto-pauses at the end (AvSync relies on this without a dispatcher) and starts audio when
    // priming ends. All transitions run briefly under _transport (no I/O; log lines are written after release) and bump _transportSerial.
    // The pump reads the serial before measuring the end, so a later user transition wins. Lock order: _transport, then audio engine locks.
    private readonly Lock _transport = new();
    private long _transportSerial;

    // ----- Lock-free scrub seeks (Pass 5.6.4A) -----
    // CL-24 put Seek under _transport, so a 1-4 kHz scrub contended with the pump and a synchronous audio seek on every pointer move. Seek
    // now takes no lock: it writes the paused clock, bumps the serial (Interlocked) and posts its target to a one-word mail-slot (latest
    // wins); the pump drains the slot under _transport (DrainSeek), so the audio engine is still only moved inside a serialized transport
    // transition. Play, Pause and GhostPause fold a pending target in themselves.
    private long _pendingSeekBits = SeekMailSlot.Empty;

    /// <summary>
    /// Sends the playhead to a moment (exact; a scrub calls this at the pointer's rate, no pacing needed). Lock-free: never waits on the
    /// pump or the audio engine; the pump applies it within a turn.
    /// </summary>
    public void Seek(double sequenceSeconds)
    {
        var seconds = Math.Clamp(sequenceSeconds, 0, Math.Max(SequenceSeconds, 0));
        if (!double.IsFinite(seconds))
            return;

        // A paused drag of the playhead: its heading, for the decode hint a little ahead of it. These fields are Seek's own (one caller
        // thread); the pump reads them as hints only.
        var now = Stopwatch.GetTimestamp();
        var holdBackground = false;
        if (!_playing && !double.IsNaN(_lastSeekAt))
        {
            var elapsed = (now - Volatile.Read(ref _lastSeekTicks)) / (double)Stopwatch.Frequency;
            if (elapsed is > 0.001 and < 0.25)
                _scrubVelocity = 0.6 * _scrubVelocity + 0.4 * ((seconds - _lastSeekAt) / elapsed);
            if (elapsed < ScrubbingFor)
            {
                Volatile.Write(ref _scrubMovedTicks, now);
                holdBackground = true;
            }
            else
            {
                _scrubVelocity = 0;
            }
        }

        _lastSeekAt = seconds;
        Volatile.Write(ref _lastSeekTicks, now);

        // The clock reads the target at once (Position); the serial cancels an end-of-Sequence pause measured before it (PauseAtEnd). Posted
        // before the serial moves: a pump turn that reads the new serial also sees the target pending, so it never measures the end on the
        // audio clock the seek has not moved yet.
        Volatile.Write(ref _pausedAt, seconds);
        SeekMailSlot.Post(ref _pendingSeekBits, seconds);
        Interlocked.Increment(ref _transportSerial);

        // A scrub's move holds background work back a moment (the strip workers stop between pictures meanwhile); extended at most every
        // ScrubHoldEveryMs, never taking WorkScheduler's gate at the pointer's 500 Hz.
        if (holdBackground && ScrubHoldDue(now, _scrubHeldAt, Stopwatch.Frequency))
        {
            _scrubHeldAt = now;
            WorkScheduler.HoldBackground(ScrubHold);
        }

        Invalidate();
    }

    /// <summary>Least time, ms, between two scrub extensions of the background hold (<see cref="ScrubHold"/>, 400 ms, outlasts it).</summary>
    internal const int ScrubHoldEveryMs = 50;

    // Stopwatch ticks of Seek's last background hold (0 = none); Seek's own.
    private long _scrubHeldAt;

    /// <summary>True when a scrub move should extend the background hold: none yet, or <see cref="ScrubHoldEveryMs"/> since the last. Pure.</summary>
    internal static bool ScrubHoldDue(long now, long lastHeldAt, long frequency) =>
        lastHeldAt == 0 || (now - lastHeldAt) * 1000 >= ScrubHoldEveryMs * frequency;

    /// <summary>
    /// Applies the newest posted <see cref="Seek"/> to the transport and the audio engine (pump thread, under <see cref="_transport"/>);
    /// a newer post simply wins on the next turn. No lock when nothing is pending.
    /// </summary>
    private void DrainSeek()
    {
        if (!SeekMailSlot.IsPending(ref _pendingSeekBits))
            return;

        lock (_transport)
        {
            var bits = Volatile.Read(ref _pendingSeekBits);
            if (bits == SeekMailSlot.Empty)
                return;
            var target = Math.Clamp(BitConverter.Int64BitsToDouble(bits), 0, Math.Max(SequenceSeconds, 0));

            // A Seek racing PauseAtEnd may have written its paused clock before PauseCore wrote the end over it: the drained target is the
            // newest one, so the paused clock follows it (never left at a stale end).
            if (!_playing)
                Volatile.Write(ref _pausedAt, target);
            _audio.Seek(target);

            // Emptied only once the audio clock has moved (Position shows the target until then), and only if no newer target was posted
            // meanwhile (that one is applied next turn).
            Interlocked.CompareExchange(ref _pendingSeekBits, SeekMailSlot.Empty, bits);
        }
    }

    /// <summary>The playing clock: a posted target not yet applied to the audio clock (read first), else the audio clock.</summary>
    private double PlayingPosition()
    {
        var bits = Volatile.Read(ref _pendingSeekBits);
        return bits != SeekMailSlot.Empty ? BitConverter.Int64BitsToDouble(bits) : _audio.Position;
    }

    // ----- Ghost-pause play-scrubbing (Pass 5.6.4A) -----
    // A playhead drag while playing holds the transport (GhostPause): audio clock paused at the scrub target, frames drawn as paused ones
    // (whole, no CountFrame penalty, the scaled run ended). The UI keeps showing play; a pointer rest of GhostDwell plays on from there until
    // the next move, and release resumes playback (GhostResume). Pause or Play in the meantime drops the hold.
    private volatile bool _ghostPaused;

    // Asked by GhostPause or EndScrubScaling (any thread), honoured by the pump at the top of its turn: EndScaledRun, so the next frame is
    // whole and a playing frame starts a fresh run.
    private int _scaleResetOwed;

    /// <summary>Pointer rest (seconds) after which a held play-scrub plays again until the next move (user decision: 80 ms, CrispAfter).</summary>
    public const double GhostDwell = CrispAfter;

    // A GhostPause posted by the UI (1), applied by the pump under _transport (ApplyGhostPause).
    private int _ghostOwed;

    /// <summary>
    /// True while a play-scrub holds a playing transport (<see cref="GhostPause"/>); <see cref="IsPlaying"/> turns false once the pump has
    /// applied it (within a turn).
    /// </summary>
    public bool IsGhostPaused => _ghostPaused;

    /// <summary>
    /// Holds a playing transport for a fast play-scrub. Lock-free (UI thread, never waits on the pump or the audio engine): it marks the
    /// hold, posts the request and cancels an end-of-Sequence pause measured before it; the pump pauses the audio at the pending scrub target
    /// (ApplyGhostPause). No <see cref="Ended"/>, no play-session log line. False when not playing or already held.
    /// </summary>
    public bool GhostPause()
    {
        if (!_playing || _ghostPaused)
            return false;

        _ghostPaused = true;
        Interlocked.Exchange(ref _ghostOwed, 1);
        Interlocked.Increment(ref _transportSerial);
        Interlocked.Exchange(ref _scaleResetOwed, 1);
        Invalidate();
        return true;
    }

    /// <summary>
    /// Applies a posted <see cref="GhostPause"/> (pump thread, under <see cref="_transport"/>): the audio pauses at the pending scrub target
    /// or the clock, unless Play, Pause or GhostResume dropped the hold first.
    /// </summary>
    private void ApplyGhostPause()
    {
        if (Interlocked.Exchange(ref _ghostOwed, 0) == 0)
            return;

        lock (_transport)
        {
            if (!_playing || !_ghostPaused)
                return;
            Interlocked.Increment(ref _transportSerial);

            // The play-scrub's paused seeks prime the voices short of GhostDwell (WasapiAudioEngine.GhostPrimeQuietMs); Play or Pause restores.
            _audio.GhostPrime = true;
            _ = PauseCore(SeekMailSlot.TryTake(ref _pendingSeekBits, out var pending) ? pending : _audio.Position);
        }
    }

    /// <summary>Ends a ghost pause: playback resumes from the paused clock (the scrubbed moment). False when none is held.</summary>
    public bool GhostResume()
    {
        lock (_transport)
        {
            if (!_ghostPaused)
                return false;
            _ghostPaused = false;
        }

        Play();
        return true;
    }

    /// <summary>
    /// Scrub released: the pump ends the Dynamic Preview Scaling run (EndScaledRun) before its next frame, so resumed playback starts whole
    /// instead of staying at a scrub's reduced step until the next pause.
    /// </summary>
    public void EndScrubScaling()
    {
        Interlocked.Exchange(ref _scaleResetOwed, 1);
        Invalidate();
    }

    public void Play()
    {
        lock (_transport)
        {
            _ghostPaused = false;
            _audio.GhostPrime = false;
            if (_playing)
                return;
            Interlocked.Increment(ref _transportSerial);

            // A pending Seek is folded in: play starts from its target (newer than a paused clock an earlier drain may have written).
            var seekPending = SeekMailSlot.IsPending(ref _pendingSeekBits);
            var from = SeekMailSlot.ResumeFrom(ref _pendingSeekBits, Volatile.Read(ref _pausedAt), SequenceSeconds, _rate);
            _audio.Seek(from);
            _scrubVelocity = 0;

            // Multiple videos: hold the clock for their first frames (PlayStartWait) so none starts late or out of sync. One video whose
            // playhead frame is not on screen exact yet (cold resume, GhostResume): a brief hold (SingleStartWait) for its exact frame.
            // No drawable surface (hidden player, headless): no scene can be presented, so nothing is primed.
            var videos = _state is { } state && _viewport.PixelSize is var (surfaceW, surfaceH) && surfaceW >= 8 && surfaceH >= 8 ? VideosAt(state, from) : 0;
            _primingSingle = videos == 1;
            _primingSince = PrimesAtPlay(videos, alreadyExact: !seekPending && PausedSceneExact(from)) ? Stopwatch.GetTimestamp() : 0;
            _primingScene = 0;
            if (_primingSince == 0)
                _audio.Play();
            BeginPlaySession();
            _playing = true;
        }

        Invalidate();
    }

    /// <summary>
    /// Maximum play-start wait for several videos' first frames (ms); videos already decoded at and just after the playhead never wait
    /// (<see cref="PlayStartReady"/>). A single video waits at most <see cref="SingleStartWait"/>.
    /// </summary>
    public const int PlayStartWait = 350;

    /// <summary>
    /// Maximum play-start wait (ms) for a single video's exact playhead frame when it is not on screen yet (cold Play, GhostResume): audio
    /// never runs ahead of stand-in frames nor trips Dynamic Preview Scaling. No wait when that frame is already presented exact.
    /// </summary>
    public const int SingleStartWait = 120;

    // When play was pressed with videos to prime, and the first scene drawn since (0: none yet).
    private long _primingSince, _primingScene;

    // The priming press had one video on screen (SingleStartWait, exact frame only), else several (PlayStartWait).
    private bool _primingSingle;

    /// <summary>
    /// True when Play holds the clock for first frames: several videos on screen, or one whose playhead frame is not yet presented exact
    /// (<paramref name="alreadyExact"/>: the newest presented scene is exact and no seek is pending). Pure.
    /// </summary>
    internal static bool PrimesAtPlay(int videos, bool alreadyExact) => videos > 1 || videos == 1 && !alreadyExact;

    // The newest paused scene published (pump thread; 0 once a playing scene is published) and its playhead. Written scene=0, at, scene so
    // a reader that sees the same non-zero scene before and after reading the time has a matching pair (sequences only grow).
    private long _pausedScene;
    private double _pausedSceneAt;

    private void NotePublished(long sequence, double seconds, bool playing)
    {
        if (sequence <= 0)
            return;
        Volatile.Write(ref _pausedScene, 0);
        if (playing)
            return;
        Volatile.Write(ref _pausedSceneAt, seconds);
        Volatile.Write(ref _pausedScene, sequence);
    }

    /// <summary>
    /// True when the paused scene at <paramref name="from"/> is the one presented, exact, with no redraw owed: a drained seek or ghost pause
    /// whose scene is not on screen yet never counts the previous frame's exact flag.
    /// </summary>
    private bool PausedSceneExact(double from)
    {
        var scene = Volatile.Read(ref _pausedScene);
        var at = Volatile.Read(ref _pausedSceneAt);
        ref var control = ref _client.Memory.Control;
        return PausedSceneExact(Volatile.Read(ref _sceneDirty) != 0, scene, Volatile.Read(ref _pausedScene) == scene,
            Volatile.Read(ref control.PresentedSequence), at, from, Volatile.Read(ref control.PresentedExact) != 0);
    }

    /// <summary>Resume-target exactness from its inputs (see <see cref="PausedSceneExact(double)"/>). Pure.</summary>
    internal static bool PausedSceneExact(bool dirty, long scene, bool stable, long presented, double sceneAt, double from, bool exact) =>
        !dirty && scene > 0 && stable && presented >= scene && Math.Abs(sceneAt - from) < 1e-9 && exact;

    /// <summary>
    /// Single-video play-start readiness: the first scene presented since the press (<paramref name="scene"/>) is exact, or
    /// <see cref="SingleStartWait"/> has passed since <paramref name="since"/>. Pure over the shared control block.
    /// </summary>
    public static bool SingleStartReady(CompositorClient client, long since, long scene)
    {
        ref var control = ref client.Memory.Control;
        return SingleStartReady(scene > 0 && Volatile.Read(ref control.PresentedSequence) >= scene, Volatile.Read(ref control.PresentedExact) != 0,
            Stopwatch.GetElapsedTime(since).TotalMilliseconds);
    }

    /// <summary>Single-video play-start readiness from its inputs: exact scene presented, or <see cref="SingleStartWait"/> elapsed. Pure.</summary>
    internal static bool SingleStartReady(bool presented, bool exact, double elapsedMs) => presented && exact || elapsedMs >= SingleStartWait;

    // Play-start readiness of the current priming press (pump thread).
    private bool PrimingReady() =>
        _primingSingle ? SingleStartReady(_client, _primingSince, _primingScene) : PlayStartReady(_client, _primingSince, _primingScene);

    /// <summary>How many different videos are on screen at a moment of the timeline.</summary>
    public static int VideosAt(SequenceExportState state, double at) =>
        state.Timeline.Tracks.Where(t => !t.IsHidden).SelectMany(t => t.Clips)
            .Where(c => c.Start <= at && c.End > at && !c.IsHidden && c.ContentKind == Models.LayerKind.VideoFile)
            .Select(c => c.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// True when every visible video has its playhead frame and the next two cached, per the first scene presented since the press
    /// (worker PresentedExact/PresentedAhead). Lane busyness is ignored (pre-roll keeps lanes busy). Only answers for
    /// <paramref name="scene"/> or later count.
    /// </summary>
    /// <param name="since">When play was pressed (Stopwatch ticks).</param>
    /// <param name="scene">First scene presented since the press; 0 while none.</param>
    public static bool PlayStartReady(CompositorClient client, long since, long scene)
    {
        ref var control = ref client.Memory.Control;
        return (scene > 0 && Volatile.Read(ref control.PresentedSequence) >= scene && Volatile.Read(ref control.PresentedExact) != 0 && Volatile.Read(ref control.PresentedAhead) != 0)
            || Stopwatch.GetElapsedTime(since).TotalMilliseconds >= PlayStartWait;
    }

    public void Pause()
    {
        string? session;
        lock (_transport)
        {
            _ghostPaused = false;
            _audio.GhostPrime = false;
            if (!_playing)
                return;
            Interlocked.Increment(ref _transportSerial);

            // A Seek posted while playing and not yet drained is where the pause lands (the audio clock has not moved there yet).
            session = PauseCore(SeekMailSlot.TryTake(ref _pendingSeekBits, out var pending) ? pending : _audio.Position);
        }

        if (session is not null)
            AppLog.Write(session);
        Invalidate();
    }

    /// <summary>Pauses at <paramref name="at"/> (under <see cref="_transport"/>); returns the play session's log line, written after the lock.</summary>
    private string? PauseCore(double at)
    {
        _audio.Pause();
        _primingSince = 0;
        _playing = false;
        Volatile.Write(ref _pausedAt, Math.Clamp(at, 0, Math.Max(SequenceSeconds, 0)));
        _audio.Seek(Volatile.Read(ref _pausedAt));
        return EndPlaySession();
    }

    /// <summary>
    /// End of Sequence reached by the pump: pause and raise <see cref="Ended"/>, unless a transport change occurred after the end was
    /// measured (<paramref name="serial"/> mismatch), in which case nothing happens.
    /// </summary>
    private void PauseAtEnd(long serial, double end)
    {
        string? session;
        lock (_transport)
        {
            // One commit point against lock-free Seek (TryCommitEnd): a seek posted before it wins and is applied next turn (every pump
            // path, draw-ahead included); one posted after it lands on the paused clock (Position reads a pending target first).
            if (!_playing || !SeekMailSlot.TryCommitEnd(ref _transportSerial, serial, ref _pendingSeekBits))
                return;
            session = PauseCore(end);
        }

        if (session is not null)
            AppLog.Write(session);
        Invalidate();

        // The engine did pause at the end, so Ended is due even if a later seek moved the paused clock (its handler skips a queued Ended
        // when Play was pressed again first).
        Ended?.Invoke();
    }

    /// <summary>Priming finished (first frames ready): starts audio unless paused in the meantime.</summary>
    private void EndPriming()
    {
        lock (_transport)
        {
            if (!_playing || _primingSince == 0)
                return;
            _primingSince = 0;
            _audio.Play();
        }
    }

    // ----- The frame pump -----

    /// <summary>Last pump wait for the worker frame during playback (ms; AvSync trace).</summary>
    internal float LastFrameWaitMs;

    private void Pump()
    {
        var waits = _workerFrame is null ? new WaitHandle[] { _dirty, _stop } : new WaitHandle[] { _dirty, _stop, _workerFrame };
        var lastTurn = Stopwatch.GetTimestamp();
        var failedNoted = false;
        var (owedTurn, presentedLast) = (false, false);

        // Monitor refresh period sampled at play start: seed and bound for the refresh estimate.
        var (monitorPeriod, playBegun) = (double.NaN, false);
        Span<GpuSeekHint> hints = stackalloc GpuSeekHint[CompositorProtocol.MaxHints];
        while (!_stop.WaitOne(0))
        {
            try
            {
                // Only a supervisor give-up stops the player; single worker failures are taken over (~80 ms).
                // Deferred until a subscriber exists (the pump starts before MainWindow subscribes).
                if (_client.GaveUp && !failedNoted && Faulted is { } faulted)
                {
                    failedNoted = true;
                    faulted(_client.Message);
                }

                // Lock-free scrub requests land here, under _transport (ApplyGhostPause, DrainSeek); a scrub release or ghost pause ends the
                // scaled run.
                ApplyGhostPause();
                DrainSeek();
                if (Interlocked.Exchange(ref _scaleResetOwed, 0) != 0)
                    EndScaledRun();

                // Export Frame capture: rendered and read back here (the sole scene-writing thread).
                if (Interlocked.Exchange(ref _capture, null) is { } capture)
                    Capture(capture);

                // Read back the on-screen frame for a deactivating tab before anything overwrites it.
                TakeSnapshot();

                // Project save thumbnail: rendered small the same way; never while playing.
                if (Interlocked.Exchange(ref _thumbnail, null) is { } thumbnail)
                    CaptureThumbnail(thumbnail);

                // Attach/detach Live Video Preview mirrors here (the sole presenting thread).
                TakeMirrorRequests();

                if (_playing)
                {
                    if (!playBegun)
                    {
                        playBegun = true;
                        monitorPeriod = _viewport.MonitorRefreshSeconds;
                        _refresh = GpuViewport.StartRefresh(_refresh, monitorPeriod);
                        SeedLeadOnce(monitorPeriod);
                    }

                    // Swap-chain paced, max one queued frame. A turn without a present leaves the waitable unsignalled, so the next
                    // turn skips the wait. Refresh is measured only between two paced turns.
                    var paced = !owedTurn;
                    if (owedTurn)
                        Thread.Sleep(1);
                    else
                        _viewport.WaitForTurn(50);
                    var now = Stopwatch.GetTimestamp();
                    var interval = (now - lastTurn) / (double)Stopwatch.Frequency;
                    lastTurn = now;
                    // Accept intervals from 1.5 ms (500 Hz = 2 ms; a 4 ms floor once mis-measured 500 Hz as 60 Hz). Intervals over
                    // 1.5x the monitor refresh are stalls and are excluded (cold-play 37-52 ms turns would skew the estimate).
                    if (paced && presentedLast)
                        _refresh = GpuViewport.NextRefresh(_refresh, interval, monitorPeriod);
                    presentedLast = false;

                    if (_primingSince != 0 && PrimingReady())
                        EndPriming();

                    // A ghost pause or seek posted during the wait lands before this turn's moment is measured.
                    ApplyGhostPause();
                    if (!_playing)
                        continue;
                    DrainSeek();

                    // Read requested marks before the plan and playhead so this scene satisfies them (PresentedSince).
                    _turnMark = Volatile.Read(ref _sceneMark);
                    var plan = Volatile.Read(ref _plan);

                    // Draw-ahead (unified-memory GPUs, e.g. Adreno): present the frame rendered last refresh and submit the next immediately
                    // (DrawAheadTurn). Stalled turns record no display lead.
                    var origin = GpuViewport.LeadOrigin(now, interval, monitorPeriod, _refresh);
                    if (DrawsAhead)
                    {
                        DrawAheadTurn(plan, origin, hints, ref owedTurn, ref presentedLast);
                        continue;
                    }

                    // Evaluate at display time: measured lead to the presenting vblank (GpuViewport.DisplayLeadSeconds), else one refresh.
                    _viewport.FrameOrigin = origin;
                    var serial = Volatile.Read(ref _transportSerial);
                    // While priming the audio clock is held at the resume target: no display lead, so the requested start frame is the one primed.
                    var lead = _primingSince != 0 ? 0 : GpuViewport.PlayingLead(_refresh, _viewport.DisplayLeadSeconds);
                    var at = _audio.Position + lead * Math.Max(_rate, 0);

                    // A seek posted before the serial was read (Seek posts first) and not yet applied: the end is never measured on the old
                    // audio clock; the next turn drains it. One posted after the read fails PauseAtEnd's serial check.
                    var unapplied = SeekMailSlot.IsPending(ref _pendingSeekBits);
                    if (!unapplied && (_rate >= 0 && at >= plan.SequenceSeconds - 1e-3 || _rate < 0 && at <= 0))
                    {
                        PauseAtEnd(serial, _rate >= 0 ? plan.SequenceSeconds : 0);
                        continue;
                    }

                    // Same timeline frame as last turn (60 fps on 144 Hz, 3D at rest): skip the render unless something else changed.
                    // Worker redraws (exact frame after a stand-in) are still presented.
                    var frame = CanvasFrameOf(at);
                    var moved = false;
                    CameraMoving(ref moved);
                    if (frame == _lastPlayedFrame && frame != long.MinValue && Volatile.Read(ref _sceneDirty) == 0 && !moved)
                    {
                        // Re-present the last frame (copy, no render) to keep swap-chain cadence so a new frame lands on the first
                        // vblank after its time. Copies record no display lead.
                        _viewport.FrameOrigin = 0;
                        var kept = _viewport.PresentNewest(vsync: true, force: true);
                        (owedTurn, presentedLast) = (!kept, kept);
                        PresentMirrors();
                        continue;
                    }

                    Volatile.Write(ref _sceneDirty, 0);
                    _lastPlayedFrame = frame;
                    var sequence = PublishAt(plan, at, playing: true);
                    if (_primingSince != 0 && sequence == 0)
                        EndPriming(); // (no surface: nothing can be presented to wait for)
                    else if (_primingSince != 0 && _primingScene == 0)
                        _primingScene = sequence;

                    // The cuts ahead (rewritten only when one is passed or another comes into the horizon).
                    LeaveHints(plan, at, playing: true, hints);
                    var waitStarted = Stopwatch.GetTimestamp();
                    if (sequence > 0)
                        CountFrame(late: NoteFrame(!_client.WaitForFrame(sequence, Math.Max(12, (int)(_refresh * 1000)))));
                    LastFrameWaitMs = (float)Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
                    owedTurn = !(_viewport.PresentNewest(vsync: true) || _viewport.PresentNewest(vsync: true, force: true));
                    presentedLast = !owedTurn;
                    PresentMirrors();
                    continue;
                }

                _lastPlayedFrame = long.MinValue;
                playBegun = false;
                _viewport.FrameOrigin = 0; // (a paused present measures no lead: its frame's moment is not a turn's)
                _aheadSequence = 0; // (a frame drawn ahead for a play that stopped is never shown)

                // Paused with the camera moving (the 3D exploded view orbiting or opening): drawn every refresh, as playing.
                var moving = false;
                CameraMoving(ref moving);
                if (moving)
                {
                    if (owedTurn)
                        Thread.Sleep(1);
                    else
                        _viewport.WaitForTurn(50);
                    _turnMark = Volatile.Read(ref _sceneMark);
                    var sequence = PublishAt(Volatile.Read(ref _plan), Volatile.Read(ref _pausedAt), playing: false);
                    if (sequence > 0)
                        _ = _client.WaitForFrame(sequence, Math.Max(12, (int)(_refresh * 1000)));
                    owedTurn = !(_viewport.PresentNewest(vsync: true) || _viewport.PresentNewest(vsync: true, force: true));
                    PresentMirrors();
                    continue;
                }

                owedTurn = false;

                // Paused: render only changes; present worker redraws. The wait also ends when hover comes to rest (play-ahead hint).
                var woke = WaitHandle.WaitAny(waits, PausedWaitMs());
                if (woke == 1)
                    return;
                if (_playing)
                    continue;
                _turnMark = Volatile.Read(ref _sceneMark);
                var pausedPlan = Volatile.Read(ref _plan);
                var pausedAt = Volatile.Read(ref _pausedAt);

                // Scrub ended on a downscaled frame (Dynamic Preview Scaling): redraw at full scale.
                if (CrispOwed())
                {
                    woke = 0;
                    Volatile.Write(ref _sceneDirty, 1);
                }
                if (woke == 0 && Interlocked.Exchange(ref _sceneDirty, 0) != 0)
                    PublishAt(pausedPlan, pausedAt, playing: false);
                LeaveHints(pausedPlan, pausedAt, playing: false, hints);

                if (_viewport.HasNewFrame)
                    _viewport.PresentNewest(vsync: false);
                PresentMirrors();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never out of this thread: logged, and the next turn tries again.
                AppLog.Write("GPU frame pump", ex);
                _stop.WaitOne(100);
            }
        }
    }

    /// <summary>Writes and publishes the scene at a time; returns 0 when there is no surface (hidden player).</summary>
    // Timeline frame rate (0 unknown) and the last timeline frame rendered while playing.
    private double _canvasFps;
    private long _lastPlayedFrame = long.MinValue;

    /// <summary>Timeline frame index at the canvas rate; long.MinValue when unknown (render every refresh).</summary>
    private long CanvasFrameOf(double seconds)
    {
        var fps = Volatile.Read(ref _canvasFps);
        return fps > 0 && double.IsFinite(seconds) ? (long)Math.Floor(seconds * fps + 1e-6) : long.MinValue;
    }

    /// <summary>
    /// Scrub flags of a published scene: <see cref="FrameSceneHeader.FlagScrubbing"/> for a held drag while the transport is paused, plus
    /// <see cref="FrameSceneHeader.FlagGhostScrub"/> when that drag ghost-pauses playback; none once playing (Play, GhostResume). Pure.
    /// </summary>
    internal static int ScrubFlags(bool playing, bool held, bool ghostPaused) =>
        playing || !held ? 0 : FrameSceneHeader.FlagScrubbing | (ghostPaused ? FrameSceneHeader.FlagGhostScrub : 0);

    private long PublishAt(GpuScenePlan plan, double seconds, bool playing)
    {
        var (width, height) = _viewport.PixelSize;
        if (width < 8 || height < 8)
            return 0;

        // The end of the sequence shows its last frame, not what comes after it: nothing.
        var asked = seconds;
        if (plan.SequenceSeconds > 0.002 && seconds > plan.SequenceSeconds - 0.001)
            seconds = plan.SequenceSeconds - 0.001;

        // Snapshot UI display inputs once per scene so a mid-scene Show Source toggle or pinch cannot mix states (next turn redraws).
        var sourceView = _sourceView;
        var (viewScale, viewPanX, viewPanY) = (_viewScale, _viewPanX, _viewPanY);

        _client.ClearScene();
        ref var header = ref _client.Scene;
        var layers = MemoryMarshal.CreateSpan(ref _client.Layer(0), CompositorProtocol.MaxLayers);
        if (sourceView)
            plan.EvaluateSource(seconds, Volatile.Read(ref _sourceKey), ref header, layers);
        else
            plan.Evaluate(seconds, ref header, layers, PlayerCompare());
        header.PlayheadSeconds = seconds;
        header.IsPlaying = playing ? 1 : 0;
        (header.OutputWidth, header.OutputHeight) = (width, height);
        header.Flags |= FrameSceneHeader.FlagLetterbox;

        // A held paused drag tells the worker's decoders outright (no pre-roll or scrub-ahead lanes; EndScrubScaling redraws on release).
        // Play or GhostResume clears it at once (a dwell-play segment keeps the gate held); a ghost-paused play-scrub dwells short.
        if (ScrubFlags(playing || _playing, ScrubGate.IsHeld, _ghostPaused) is var scrub and not 0)
            header.Flags |= scrub;
        header.PreviewScale = PreviewScaleFor(playing, width, height, header.CanvasWidth > 0 ? header.CanvasWidth : 1920, header.CanvasHeight > 0 ? header.CanvasHeight : 1080);
        if (!sourceView)
            WriteAudioBands(ref header, seconds, playing);
        (header.ViewScale, header.ViewPanX, header.ViewPanY) = (viewScale, viewPanX, viewPanY);
        if (!sourceView && Volatile.Read(ref _loupeKey) is var loupe and not 0)
            (header.LoupeClipKey, header.LoupeKind, header.LoupeZoom) = (loupe, Volatile.Read(ref _loupeKind), LoupeZoom);
        if (header.CanvasWidth <= 0 || header.CanvasHeight <= 0)
            (header.CanvasWidth, header.CanvasHeight) = (1920, 1080);
        _shownCanvas = (header.CanvasWidth, header.CanvasHeight);
        if (!sourceView)
        {
            WriteCamera(ref header, layers);
            WriteLoopback(ref header);
        }

        var sequence = _client.Publish();
        NotePublished(sequence, asked, playing);

        // Downscaled scene: block still readback until a newer full frame is on screen (TakeSnapshot).
        if (sequence > 0 && _lastScale < 1)
            _scaledSequence = sequence;
        if (sequence > 0 && _turnMark > _markPublished)
        {
            Volatile.Write(ref _markedScene, sequence);
            Volatile.Write(ref _markPublished, _turnMark);
        }

        // A mark requested after this turn read the plan: redraw once more so it is satisfied.
        if (sequence > 0 && Volatile.Read(ref _sceneMark) > _turnMark)
        {
            Volatile.Write(ref _sceneDirty, 1);
            _dirty.Set();
        }

        return sequence;
    }

    // ----- Live Video Preview mirrors -----

    // Pending mirror attach (true) / detach (false) by HWND; drained on the pump thread (TakeMirrorRequests).
    private readonly System.Collections.Concurrent.ConcurrentQueue<(bool Attach, nint Window)> _mirrorRequests = new();

    // The mirrors, on the pump's thread only; the canvas the player's picture was last drawn at, and the shape last told.
    private readonly List<GpuViewport> _mirrors = [];
    private (int Width, int Height) _shownCanvas = (1920, 1080);
    private double _mirrorAspectTold;

    /// <summary>
    /// Mirrors the player into an HWND (Live Video Preview): from the next pump turn, the canvas region of each presented frame is
    /// GPU-copied to a swap chain on the player device and presented without waiting. Any thread; idempotent.
    /// </summary>
    public void AttachMirror(nint window)
    {
        if (window == 0)
            return;
        _mirrorRequests.Enqueue((true, window));
        _dirty.Set();
    }

    /// <summary>Stops mirroring into an HWND; its swap chain is released on the pump thread.</summary>
    public void DetachMirror(nint window)
    {
        if (window == 0)
            return;
        _mirrorRequests.Enqueue((false, window));
        _dirty.Set();
    }

    /// <summary>
    /// Raised on the pump thread when the mirrored aspect ratio changes, so side windows resize their surfaces to avoid distortion.
    /// </summary>
    public event Action<double>? MirrorAspectChanged;

    /// <summary>Raised on the UI thread when an installed scene plan's canvas size (the letterboxed picture) changes.</summary>
    public event Action? CanvasChanged;

    private void TakeMirrorRequests()
    {
        while (_mirrorRequests.TryDequeue(out var request))
        {
            var at = _mirrors.FindIndex(m => m.Window == request.Window);
            if (request.Attach && at < 0)
            {
                _mirrors.Add(new GpuViewport(_viewport, request.Window));
                _mirrorAspectTold = 0;
            }
            else if (!request.Attach && at >= 0)
            {
                _mirrors[at].Dispose();
                _mirrors.RemoveAt(at);
            }
        }
    }

    /// <summary>Presents each mirror after the player (non-blocking, GpuViewport.PresentMirror) and raises aspect changes.</summary>
    private void PresentMirrors()
    {
        if (_mirrors.Count == 0)
            return;

        var (width, height) = _shownCanvas;
        var aspect = width > 0 && height > 0 ? (double)width / height : 0;
        if (aspect > 0 && Math.Abs(aspect - _mirrorAspectTold) > 1e-6)
        {
            _mirrorAspectTold = aspect;
            MirrorAspectChanged?.Invoke(aspect);
        }

        foreach (var mirror in _mirrors)
            mirror.PresentMirror(width, height);
    }

    // ----- The source as it is (a rectangle drawn on it: Interactive Crop, a Draw Target) -----

    private volatile bool _sourceView;

    // The clip whose picture the source view shows (GpuHash.Of its id); 0 for the bottom video at the playhead.
    private long _sourceKey;

    /// <summary>
    /// Shows a clip's unprocessed source instead of the Sequence (Interactive Crop, Draw Target; GpuScenePlan.EvaluateSource).
    /// <paramref name="clipId"/> selects the clip; empty uses the bottom video at the playhead.
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

    /// <summary>
    /// Pending frame capture: result source plus its plan (originals plan built on the UI thread; null = player plan), swapped
    /// atomically so a request never renders with another's plan.
    /// </summary>
    private sealed record FrameRequest(TaskCompletionSource<(byte[] Bgra, int Width, int Height)?> Done, GpuScenePlan? Plan);

    private FrameRequest? _capture;

    // The state the plan was made from.
    private SequenceExportState? _state;

    // Set when Dispose begins: later captures complete with null immediately (no wait on a dead pump or its disposed wake event).
    private volatile bool _disposed;

    /// <summary>
    /// Captures the playhead frame as the export renders it: canvas size, no letterbox/zoom/3D, exact frames from originals (never
    /// proxies), BGRA8 (alpha undefined). Null on failure or when superseded. Call on the UI thread (builds the originals plan);
    /// rendered on the pump thread.
    /// </summary>
    public Task<(byte[] Bgra, int Width, int Height)?> CaptureFrameAsync()
    {
        if (_disposed)
            return Task.FromResult<(byte[] Bgra, int Width, int Height)?>(null);

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

        Interlocked.Exchange(ref _capture, new FrameRequest(request, originals))?.Done.TrySetResult(null);
        _dirty.Set();
        return request.Task;
    }

    private void Capture(FrameRequest asked)
    {
        var request = asked.Done;
        try
        {
            var plan = asked.Plan ?? Volatile.Read(ref _plan);
            var at = Math.Min(Position, Math.Max(plan.SequenceSeconds - 0.001, 0));
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

    // ----- Project thumbnail (ProjectThumbnail) -----

    /// <summary>
    /// Thumbnail request swapped as one record so the pump never mixes requests: completion source, plan (fade-free copy when the
    /// player's has fades; null = player's), requested short side, and whether to render as displayed (3D tab still) or flat.
    /// </summary>
    private sealed record ThumbnailRequest(TaskCompletionSource<(byte[] Bgra, int Width, int Height)?> Done, GpuScenePlan? Plan, int ShortSide, bool AsShown);

    private ThumbnailRequest? _thumbnail;

    /// <summary>Max long side (px) of an as-displayed still; very wide players render smaller.</summary>
    private const int ShownStillLongest = 4096;

    /// <summary>Thumbnail readback timeout (ms); on timeout the save keeps the previous thumbnail.</summary>
    private const int ThumbnailReadbackMs = 3000;

    /// <summary>
    /// Small playhead frame as the player composes it (proxies, captions) without fades, short side <paramref name="shortSide"/> (capped
    /// at the canvas), even dimensions, BGRA8 (alpha undefined), using its own readback buffer (ProjectThumbnail). Returns null immediately
    /// while playing (Law 3) or empty, and null on timeout. Call on the UI thread; rendered on the pump, completed off both.
    /// <para>With <paramref name="asShown"/> and 3D view on (tab still), renders the 3D view at its target camera in the player's shape
    /// (<see cref="ShownStillSize"/>). Project thumbnails are always flat.</para>
    /// </summary>
    public Task<(byte[] Bgra, int Width, int Height)?> CaptureThumbnailAsync(int shortSide = ProjectThumbnail.ShortSide, bool asShown = false)
    {
        if (_disposed || _playing || _state is not { } state)
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

        Interlocked.Exchange(ref _thumbnail, new ThumbnailRequest(request, unfaded, Math.Max(shortSide, 16), asShown))?.Done.TrySetResult(null);
        _dirty.Set();
        return request.Task;
    }

    /// <summary>True while a thumbnail request is pending; a newer request would supersede it.</summary>
    public bool IsThumbnailWaiting => Volatile.Read(ref _thumbnail) is not null;

    /// <summary>
    /// As-displayed still size (pure): player aspect (canvas aspect while the player is too small), short side at most
    /// <paramref name="shortSide"/> and the player's, even dimensions, long side at most <see cref="ShownStillLongest"/>.
    /// </summary>
    internal static (int Width, int Height) ShownStillSize(int viewWidth, int viewHeight, int canvasWidth, int canvasHeight, int shortSide)
    {
        var (width, height) = viewWidth < 8 || viewHeight < 8 ? (Math.Max(canvasWidth, 16), Math.Max(canvasHeight, 16)) : (viewWidth, viewHeight);
        var size = ProjectThumbnail.ThumbnailSize(width, height, shortSide);
        var longest = Math.Max(size.Width, size.Height);
        if (longest <= ShownStillLongest)
            return size;

        var shorter = (int)((long)Math.Min(size.Width, size.Height) * ShownStillLongest / longest);
        return ProjectThumbnail.ThumbnailSize(width, height, Math.Max(shorter, 2));
    }

    private void CaptureThumbnail(ThumbnailRequest asked)
    {
        var request = asked.Done;

        // Asked for while paused and playing by now: none, so that the played frame of this turn is not late for it.
        if (_playing)
        {
            request.TrySetResult(null);
            return;
        }

        try
        {
            var plan = asked.Plan ?? Volatile.Read(ref _plan);
            var at = Math.Min(Position, Math.Max(plan.SequenceSeconds - 0.001, 0));
            _client.ClearScene();
            ref var header = ref _client.Scene;
            var layers = MemoryMarshal.CreateSpan(ref _client.Layer(0), CompositorProtocol.MaxLayers);
            plan.Evaluate(at, ref header, layers, ThumbnailCompare(request.Task));
            header.PlayheadSeconds = at;

            if (asked.AsShown && ShownPose() is { } pose)
            {
                // As displayed: 3D view in the player's aspect, canvas letterboxed as in the player.
                if (header.CanvasWidth <= 0 || header.CanvasHeight <= 0)
                    (header.CanvasWidth, header.CanvasHeight) = (1920, 1080);
                var (viewWidth, viewHeight) = _viewport.PixelSize;
                (header.OutputWidth, header.OutputHeight) = ShownStillSize(viewWidth, viewHeight, header.CanvasWidth, header.CanvasHeight, asked.ShortSide);
                header.Flags |= FrameSceneHeader.FlagLetterbox;
                WriteCameraAt(ref header, layers, pose, plan);
            }
            else
            {
                // Smaller than the canvas, in its shape: without a letterbox the compositor stretches the canvas over the output.
                (header.OutputWidth, header.OutputHeight) = ProjectThumbnail.ThumbnailSize(header.CanvasWidth, header.CanvasHeight, asked.ShortSide);
            }

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

    /// <summary>Writes the 3D camera and layer depth/selection flags into each scene (GpuPreviewEngine.Exploded.cs).</summary>
    partial void WriteCamera(ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers);

    /// <summary>True while the 3D camera animates, forcing paused redraws every refresh (GpuPreviewEngine.Exploded.cs).</summary>
    partial void CameraMoving(ref bool moving);

    /// <summary>Scrub window (s): a seek within this interval counts as scrubbing and defers hover work.</summary>
    private const double ScrubbingFor = 0.25;

    /// <summary>
    /// Writes worker seek hints (GpuHintWriter), only when changed: cuts within the lookahead horizon always; when paused, the scrub
    /// target extrapolated 120 ms, or else the hover frame/play-ahead. The worker handles the playhead neighbourhood itself.
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
        var scrubbing = !playing && (now - Volatile.Read(ref _lastSeekTicks)) * 1.0 / Stopwatch.Frequency < ScrubbingFor;
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

        // Drop prewarm and tab pre-roll hints (GpuSeekHintKind.TabRoll, idle priority), emitted only while quiet: not playing, not
        // scrubbing, and DecoderPool.IdleQuietMs since the last seek.
        Span<GpuSeekHint> extras = stackalloc GpuSeekHint[MostExtras];
        var extraCount = 0;

        var quiet = ExtrasMayBeWritten(playing, scrubbing, (now - Volatile.Read(ref _lastSeekTicks)) * 1000.0 / Stopwatch.Frequency);
        if (quiet && Volatile.Read(ref _prewarm) is { } file && now < file.Until)
            extras[extraCount++] = new GpuSeekHint { SourceId = file.Source, Kind = (int)GpuSeekHintKind.TabRoll, PathHash = file.PathHash, Span = 0.2f };
        if (quiet && Volatile.Read(ref _tabPrewarm) is { } tabs && now < tabs.Until)
        {
            foreach (var hint in tabs.Hints)
            {
                if (extraCount < MostExtras)
                    extras[extraCount++] = hint;
            }
        }

        // The cuts ahead as if Space were pressed: not while a scrub runs (they would move with every step of it).
        var rate = playing ? _rate : scrubbing ? 0 : _rate > 0 ? _rate : 1;
        if (_hintWriter.Write(plan, at, rate, hints, out var count, scrubAhead, pointer, resting, extras[..extraCount], Volatile.Read(ref _loopFrom), Volatile.Read(ref _loopTo)))
            _client.SetHints(hints[..count]);
    }

    // Cap on prewarm hints: one drop file plus two tabs' videos (four each, per PrewarmTabs callers).
    private const int MostExtras = 9;

    // Where Loop takes playback back from and to (seconds; NaN: no Loop; from infinite: the sequence's end), for the hints.
    private double _loopFrom = double.NaN, _loopTo = double.NaN;

    /// <summary>
    /// Loop wrap from <paramref name="from"/> (infinity = Sequence end) to <paramref name="to"/> (NaN = Loop off), so cuts at the target
    /// are pre-decoded before the wrap (any thread).
    /// </summary>
    public void SetLoop(double from, double to)
    {
        Volatile.Write(ref _loopFrom, from);
        Volatile.Write(ref _loopTo, to);
    }

    /// <summary>Paused pump wait: until hover rest or scrub end, at most 250 ms.</summary>
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

        var scrub = ScrubbingFor - (now - Volatile.Read(ref _lastSeekTicks)) * 1.0 / Stopwatch.Frequency;
        if (scrub > 0)
            wait = Math.Min(wait, scrub);

        // Downscaled scrub frame: redraw at full scale CrispAfter after the last move (Dynamic Preview Scaling).
        if (_lastScale < 1)
            wait = Math.Min(wait, Math.Max(CrispAfter - (now - Volatile.Read(ref _scrubMovedTicks)) * 1.0 / Stopwatch.Frequency, 0.001));
        return Math.Clamp((int)Math.Ceiling(wait * 1000), 1, 250);
    }

    // ----- Audio-reactive shader bus -----
    // While any effect pulses (AudioReactive.Pulses), a band map of the export mix is built in the background and rebuilt only when the
    // mix changes (AudioReactive.KeyOf), so paused/scrubbed frames match the export. Uncovered times fall back to live engine bands.

    private readonly object _energiesGate = new();
    private AudioEnergyMap? _energies;
    private CancellationTokenSource? _energiesMaking;
    private int _energiesKey, _energiesVersion;
    private volatile bool _pulses;

    /// <summary>
    /// Updates the band map for a new state (UI thread, SetState): kept if the mix is unchanged, rebuilt in the background otherwise,
    /// null when nothing pulses. <paramref name="heard"/> is SetState's program when it equals the export mix, else null.
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
                // Filled progressively from the start (TryAt reports coverage). Published under the version gate only while still newest, so
                // a stale task never overwrites a newer map or a cleared one.
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

    // Bands at a frame time (pump): from the map where covered, else live engine bands while playing.
    private void WriteAudioBands(ref FrameSceneHeader header, double seconds, bool playing)
    {
        if (!_pulses || GpuExport.WriteBands(ref header, Volatile.Read(ref _energies), seconds) || !playing)
            return;
        (header.AudioBass, header.AudioMid, header.AudioHigh) = _audio.LiveBands;
    }

    // Last background teardown task (DisposeInBackground); StartAsync awaits it. UI thread only.
    private static Task _teardown = Task.CompletedTask;

    /// <summary>
    /// Disposes the engine on the thread pool, chained after earlier teardowns (<see cref="StartAsync"/> waits), so the pump join (up to
    /// 2 s) and compositor/audio shutdown never block the UI thread. The caller must drop all references first. UI thread.
    /// </summary>
    public Task DisposeInBackground()
    {
        var previous = Volatile.Read(ref _teardown);
        var teardown = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("GPU engine: its teardown on the pool failed", ex);
            }
        });
        Volatile.Write(ref _teardown, teardown);
        return teardown;
    }

    public void Dispose()
    {
        _disposed = true;
        if (ReferenceEquals(Current, this))
            Current = null;
        ForgetEnergies();

        // The stop event also aborts readback waits within one slice (TryReadback), so the pump exits well inside the join timeout.
        _stop.Set();
        var pumpEnded = ThreadStop.Join(_pump, 2000, "Player pump");

        // A frame or a thumbnail asked for and not drawn (the engine stopped first): none, so that what waits on it goes on.
        Interlocked.Exchange(ref _capture, null)?.Done.TrySetResult(null);
        Interlocked.Exchange(ref _thumbnail, null)?.Done.TrySetResult(null);
        if (!pumpEnded)
        {
            // Pump is stuck in a native call (e.g. a hung Present): freeing the mapping or swap chain under it would be an access
            // violation and disposing its events would throw. End workers and stop audio; leak the rest until process exit.
            AppLog.Write("GPU engine: the frame pump did not stop within 2 s; the compositor's memory, the viewport and the pump's events are kept");
            _audio.Dispose();
            _broker.Dispose();
            _client.DisposeKeepingMemory();
            return;
        }

        foreach (var mirror in _mirrors)
            mirror.Dispose();
        _mirrors.Clear();
        _viewport.Dispose();
        _audio.Dispose();
        _broker.Dispose();
        _client.Dispose();
        _workerFrame?.Dispose();
        _dirty.Dispose();
        _stop.Dispose();
    }
}
