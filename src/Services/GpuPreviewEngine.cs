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

    // The project tabs' pre-roll (PrewarmTabs): the frames at their playheads, and until when (Stopwatch ticks) they are asked for.
    private sealed record PrewarmedTabs(GpuSeekHint[] Hints, long Until);
    private PrewarmedTabs? _tabPrewarm;

    /// <summary>How far past a tab's playhead its pre-roll decodes, seconds of the file (5.4.2A: half a second, the worker's HoverRoll at most).</summary>
    public const double TabPreRollSeconds = 0.5;

    /// <summary>How long a file opened ahead of a drop is kept asked for, seconds (no drop came: let go).</summary>
    private const double PrewarmSeconds = 10;

    // Something the picture shows changed (1): the paused pump draws again. A pointer moving over the timeline only
    // changes the hints and wakes the pump without it (no frame drawn for a hover).
    private int _sceneDirty = 1;

    // The scrub's heading: where the last seeks went and when (a paused drag of the playhead). The time is written by Seek (the UI thread)
    // and read by the pump, through Volatile (5.4.4: the quiet window of the tabs' pre-roll).
    private double _lastSeekAt = double.NaN;
    private long _lastSeekTicks;
    private double _scrubVelocity;

    // The display's refresh interval while playing (measured between the swap chain's turns), seconds.
    private double _refresh = 1 / 60.0;

    private GpuPreviewEngine(CompositorClient client, WasapiAudioEngine audio, nint surface, bool player)
    {
        (_client, _audio) = (client, audio);

        // The player's tabs put behind are parked lean on unified memory or on battery (5.4.4): asked of the worker and its standby alike
        // (they share the control block); strip, export, bench and test clients never ask.
        // 5.5.2: the Playback & A/V Sync Test's engine (player: false) has no tabs and never asks.
        if (player)
            client.TabParking = 1;
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
        if (player)
            Current = this;
    }

    /// <summary>The engine the player runs on now (null while it is not started, or stopped): Settings' cache readout.</summary>
    public static GpuPreviewEngine? Current { get; private set; }

    /// <summary>The swap chain it presents on (the Playback &amp; A/V Sync Test's sampler reads its frame statistics).</summary>
    internal GpuViewportControl Viewport => _viewport;

    /// <summary>
    /// Starts the engine for a player surface: the worker and its standby (off the UI thread), the audio device, then the
    /// viewport and the pump. Null when the compositor cannot start (no Direct3D 11 card, a worker that fails at once), with
    /// the reason in <see cref="LastStartFailure"/>: the player then says so and tries again (MainWindow.OnGpuFailed). On the
    /// card of Settings' Compositor GPU role, or on <paramref name="adapter"/> (a DXGI index: the processor's WARP after the
    /// card failed). Call after Frame 1.
    /// <para><paramref name="player"/> false (Pass 5.5.2-Release, the Playback &amp; A/V Sync Test): an engine beside the player's, never
    /// <see cref="Current"/>, with no standby worker, a small frame cache, no tab parking, and the card it draws on not taken for Auto's.</para>
    /// </summary>
    public static async Task<GpuPreviewEngine?> StartAsync(nint surface, int? adapter = null, bool player = true)
    {
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

                // The card it draws on is what Auto means for the background card (5.4.4: never a second card on its own). Not WARP's.
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
            ReleaseUnusedKeptSources();
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
    /// let go when idle (DecoderPool.Tidy). Opening something gives it a state again (SetState). Instant Seek's frame cache is emptied
    /// with it (5.4D-TabPerf): the closed project's frames are no other's, and the cache's budget is back at its baseline, to grow again as
    /// the next project needs.
    /// </summary>
    public void Unload()
    {
        Empty();
        _client.ClearFrameCache();
    }

    /// <summary>The engine as it starts (UI thread): <see cref="Unload"/> and <see cref="Suspend"/> but for what each does to the worker's frames.</summary>
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

    /// <summary>How long after a <see cref="Suspend"/> the sound's idle decoders are let go once more, ms: voices retired by the empty program give theirs back as the feeders finish with them.</summary>
    private const int ReleaseAgainMs = 1500;

    /// <summary>
    /// A project tab put behind (UI thread; Pass 5.4A, MainWindow.ProjectTabs.cs): the engine emptied as <see cref="Unload"/> empties it,
    /// then everything of the project the engine still holds let go at once instead of when idle: every source cleared in the worker's
    /// table (its decoders disposed at its next turn: GpuResourceBroker.ReleaseAll), the worker trimmed (5.4D-TabPerf: its free frames,
    /// working textures and readback staging let go of between frames on its next turn, after the sources; the driver's kept allocations
    /// and its heap handed back to Windows once it is left alone a moment, so a run of quick switches pays for that once:
    /// CompositorClient.TrimWorker), and the sound's idle decoders disposed, now and once more a moment later.
    /// Instant Seek's frames are the worker's to keep or not (the TabPerf follow-up, the user's X1: CompositorWorker.TrimEmptiesCache): on
    /// unified memory or a small card the trim empties the cache (its budget back at its baseline); on a big card the frames stay, the
    /// least recently used going first as the next tab needs room, and the tab brought back finds its own (DecoderPool's parked sources).
    /// No clear is sent from here: a full cache let go of texture by texture is the worker's time, never the switch's. The engine, its
    /// worker and the audio device stay up for the tab that comes to the front, which gives it a state again (SetState). Nothing here
    /// waits for the worker: the calls leave counted requests.
    /// <para>5.4.4: the sources the tabs' pre-roll named are kept (the tab brought to the front finds the frames and decoders its pre-roll
    /// made; the first SetState after lets go of those it does not use), and the worker keeps its working set when a scene follows the trim
    /// (CompositorWorker: the tab brought to the front draws with it at once).</para>
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

    /// <summary>The newest scene the worker has presented (its sequence number, growing): a still shown over the player waits for one after the scene it was shown with.</summary>
    public long PresentedScene => Volatile.Read(ref _client.Memory.Control.PresentedSequence);

    /// <summary>Whether the newest scene presented showed every video's exact frame (not a stand-in from the cache).</summary>
    public bool PresentedExact => Volatile.Read(ref _client.Memory.Control.PresentedExact) != 0;

    /// <summary>
    /// Whether the worker's card shares the PC's memory (5.4.4: an integrated or ARM64 GPU; false until the worker is Ready). Read from
    /// any thread: the worker wrote it once, with its device.
    /// </summary>
    public bool UnifiedMemory => _client.UnifiedMemory;

    // Scene marks (5.4.2A): the newest asked for (UI thread), the one the pump's turn read before it read the plan and the playhead (pump
    // thread), and the newest a published scene was made after, with that scene's number (written by the pump, the number first).
    private long _sceneMark, _turnMark, _markPublished, _markedScene;

    /// <summary>
    /// A mark of now (UI thread), after a change the player has to show before anything stands in for it (a project tab brought back: its
    /// Sequence, playhead and view set): <see cref="PresentedSince"/> says when a scene made after it has been presented. Draws again.
    /// </summary>
    public long MarkScene()
    {
        var mark = Interlocked.Increment(ref _sceneMark);
        Invalidate();
        return mark;
    }

    /// <summary>
    /// Whether the viewport has put on the screen (IDXGISwapChain::Present, 5.4.3; the worker's drawing it is not enough) a scene the pump made after <paramref name="mark"/> (<see cref="MarkScene"/>): one of the state as it
    /// was then or newer, never a scene of before it still on its way (the frame of the tab put behind, 5.4.2A's one-frame jump on a switch).
    /// </summary>
    public bool PresentedSince(long mark) => mark > 0 && Volatile.Read(ref _markPublished) >= mark && _viewport.PresentedSequence >= Volatile.Read(ref _markedScene);

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
    /// Draw again for a change of where the picture lies (zoom, pan, the player resized), not of the picture: the Export Quality
    /// Preview's content stamp stays, so a paused frame is placed again without being encoded again.
    /// </summary>
    public void InvalidateView()
    {
        Volatile.Write(ref _sceneDirty, 1);
        _dirty.Set();
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

    /// <summary>
    /// The project tabs likely to come to the front next (5.4.2A: the next in Ctrl+Tab's order, and the one under the pointer): the frame
    /// at each one's playhead of every video it shows there, and <see cref="TabPreRollSeconds"/> after it, decoded by the worker after
    /// everything else it does (never while playing), so a switch, and Space or a scrub right after it, finds them decoded and the decoders
    /// open. Asked for <see cref="PrewarmSeconds"/> at most; an empty list lets go. UI thread (the source table is its).
    /// </summary>
    /// <param name="frames">Each video's file, its video stream (0 the first) and the moment of the file at its tab's playhead.</param>
    public void PrewarmTabs(IReadOnlyList<(string Path, int Stream, double MediaSeconds)> frames)
    {
        var hints = new List<GpuSeekHint>(frames.Count);
        foreach (var (path, stream, media) in frames)
        {
            var source = _broker.SourceFor(path, stream);
            if (source >= 0 && double.IsFinite(media))
            {
                // Idle work for the worker (5.4.4): after everything else, and only while the player is quiet.
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
            if (elapsed < ScrubbingFor)
            {
                Volatile.Write(ref _scrubMovedTicks, now);

                // A scrub's move holds background work back a moment (5.4.4: the strip workers stop between pictures meanwhile).
                WorkScheduler.HoldBackground(ScrubHold);
            }
            else
            {
                _scrubVelocity = 0;
            }
        }

        _lastSeekAt = seconds;
        Volatile.Write(ref _lastSeekTicks, now);
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
        BeginPlaySession();
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
        EndPlaySession();
        Invalidate();
    }

    // ----- The frame pump -----

    /// <summary>How long the playing pump last waited for the worker's frame, ms (Pass 5.5.2: AvSync's trace).</summary>
    internal float LastFrameWaitMs;

    private void Pump()
    {
        var waits = _workerFrame is null ? new WaitHandle[] { _dirty, _stop } : new WaitHandle[] { _dirty, _stop, _workerFrame };
        var lastTurn = Stopwatch.GetTimestamp();
        var failedNoted = false;
        var (owedTurn, presentedLast) = (false, false);

        // The monitor's refresh period, read as each play begins (Pass 5.5.2): where the play's refresh estimate starts and its bound.
        var (monitorPeriod, playBegun) = (double.NaN, false);
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

                // The picture on screen read back for a project tab put behind (5.4.3), before anything new is drawn over it.
                TakeSnapshot();

                // A project's thumbnail (a save, Pass 5.2D): drawn small the same way, and never while playing.
                if (Interlocked.Exchange(ref _thumbnail, null) is { } thumbnail)
                    CaptureThumbnail(thumbnail);

                // Side windows' Live Video Previews attached or let go (5.4C): made and dropped here, the one thread that presents.
                TakeMirrorRequests();

                if (_playing)
                {
                    if (!playBegun)
                    {
                        playBegun = true;
                        monitorPeriod = _viewport.MonitorRefreshSeconds;
                        _refresh = GpuViewportControl.StartRefresh(_refresh, monitorPeriod);
                    }

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
                    // measured at 1/60 s, the picture 15 ms ahead of the sound). A turn over 1.5 of the monitor's refresh is a stall, not
                    // a refresh, and is left out (5.5.2: a cold play's 37-52 ms turns took a 500 Hz estimate to ~18 ms, the frames that far ahead).
                    if (paced && presentedLast)
                        _refresh = GpuViewportControl.NextRefresh(_refresh, interval, monitorPeriod);
                    presentedLast = false;

                    if (_primingSince != 0 && PlayStartReady(_client, _primingSince, _primingScene))
                    {
                        _primingSince = 0;
                        _audio.Play();
                    }

                    // The marks asked for so far, read before the plan and the playhead: the scene made of them comes after them (PresentedSince).
                    _turnMark = Volatile.Read(ref _sceneMark);
                    var plan = Volatile.Read(ref _plan);

                    // Drawing ahead (5.4.4-Final, the user's Q2 a; unified memory, the XPS): the frame drawn during the last refresh shown now,
                    // and the next one handed to the worker at once, drawn while this one is on the screen (DrawAheadTurn).
                    // A stalled turn (a cold play's first second) measures no lead: its frame's long way to the screen is not the display's (5.5.2).
                    var origin = GpuViewportControl.LeadOrigin(now, interval, monitorPeriod, _refresh);
                    if (DrawsAhead)
                    {
                        DrawAheadTurn(plan, origin, hints, ref owedTurn, ref presentedLast);
                        continue;
                    }

                    // The frame is drawn for the moment it will be on the screen (5.4.4-Final, the user's Q4 a): the swap chain's measured lead
                    // from this turn to the vblank that shows it (GpuViewportControl.DisplayLeadSeconds), else one refresh as before.
                    _viewport.FrameOrigin = origin;
                    var at = _audio.Position + GpuViewportControl.PlayingLead(_refresh, _viewport.DisplayLeadSeconds) * Math.Max(_rate, 0);
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
                        // next turn wakes at the next refresh and a new frame is on screen at the first one after its time. A copy measures no
                        // lead (5.5.2): it waits for no draw, and the lead is what a new frame takes to the screen.
                        _viewport.FrameOrigin = 0;
                        var kept = _viewport.PresentNewest(vsync: true, force: true);
                        (owedTurn, presentedLast) = (!kept, kept);
                        PresentMirrors();
                        continue;
                    }

                    Volatile.Write(ref _sceneDirty, 0);
                    _lastPlayedFrame = frame;
                    var sequence = PublishAt(plan, at, playing: true);
                    if (_primingSince != 0 && _primingScene == 0)
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

                // Paused: only what changed is drawn; the worker's own redraws (an exact frame after a stand-in) are shown.
                // The wait ends when the pointer comes to rest, for its play-ahead.
                var woke = WaitHandle.WaitAny(waits, PausedWaitMs());
                if (woke == 1)
                    return;
                if (_playing)
                    continue;
                _turnMark = Volatile.Read(ref _sceneMark);
                var pausedPlan = Volatile.Read(ref _plan);
                var pausedAt = Volatile.Read(ref _pausedAt);

                // A scrub that ended on a picture drawn smaller (Dynamic Preview Scaling): drawn whole again.
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
        header.PreviewScale = PreviewScaleFor(playing, width, height, header.CanvasWidth > 0 ? header.CanvasWidth : 1920, header.CanvasHeight > 0 ? header.CanvasHeight : 1080);
        if (!_sourceView)
            WriteAudioBands(ref header, seconds, playing);
        (header.ViewScale, header.ViewPanX, header.ViewPanY) = (_viewScale, _viewPanX, _viewPanY);
        if (!_sourceView && Volatile.Read(ref _loupeKey) is var loupe and not 0)
            (header.LoupeClipKey, header.LoupeKind, header.LoupeZoom) = (loupe, Volatile.Read(ref _loupeKind), LoupeZoom);
        if (header.CanvasWidth <= 0 || header.CanvasHeight <= 0)
            (header.CanvasWidth, header.CanvasHeight) = (1920, 1080);
        _shownCanvas = (header.CanvasWidth, header.CanvasHeight);
        if (!_sourceView)
        {
            WriteCamera(ref header, layers);
            WriteLoopback(ref header);
        }

        var sequence = _client.Publish();

        // A scene drawn smaller (5.4.4): no still is read until a newer frame is on screen (TakeSnapshot).
        if (sequence > 0 && _lastScale < 1)
            _scaledSequence = sequence;
        if (sequence > 0 && _turnMark > _markPublished)
        {
            Volatile.Write(ref _markedScene, sequence);
            Volatile.Write(ref _markPublished, _turnMark);
        }

        // A mark asked for after this turn read the plan (its own redraw taken by this scene): drawn once more, so the mark is met.
        if (sequence > 0 && Volatile.Read(ref _sceneMark) > _turnMark)
        {
            Volatile.Write(ref _sceneDirty, 1);
            _dirty.Set();
        }

        return sequence;
    }

    // ----- Side windows' Live Video Previews (5.4C): mirrors of the player's picture -----

    // Mirrors asked for (true) or let go (false), by native window: taken on the pump's thread (TakeMirrorRequests).
    private readonly System.Collections.Concurrent.ConcurrentQueue<(bool Attach, nint Window)> _mirrorRequests = new();

    // The mirrors, on the pump's thread only; the canvas the player's picture was last drawn at, and the shape last told.
    private readonly List<GpuViewportControl> _mirrors = [];
    private (int Width, int Height) _shownCanvas = (1920, 1080);
    private double _mirrorAspectTold;

    /// <summary>
    /// Mirrors the player's picture into a native window (5.4C: a side window's Live Video Preview): from the next turn of the pump on,
    /// the canvas's part of every frame the player presents is copied there on the card and presented without waiting (a swap chain
    /// on the player's own device). From any thread; a window already mirrored is left as it is.
    /// </summary>
    public void AttachMirror(nint window)
    {
        if (window == 0)
            return;
        _mirrorRequests.Enqueue((true, window));
        _dirty.Set();
    }

    /// <summary>Stops mirroring into a native window (its side window closing, or a Pane Docker now); its swap chain goes on the pump's thread.</summary>
    public void DetachMirror(nint window)
    {
        if (window == 0)
            return;
        _mirrorRequests.Enqueue((false, window));
        _dirty.Set();
    }

    /// <summary>
    /// Raised on the pump's thread when the shape the mirrors show changes (the canvas's width over its height, as the player lays
    /// it): the side windows fit their surfaces to it, so the swap chains' stretch is the same both ways.
    /// </summary>
    public event Action<double>? MirrorAspectChanged;

    private void TakeMirrorRequests()
    {
        while (_mirrorRequests.TryDequeue(out var request))
        {
            var at = _mirrors.FindIndex(m => m.Window == request.Window);
            if (request.Attach && at < 0)
            {
                _mirrors.Add(new GpuViewportControl(_viewport, request.Window));
                _mirrorAspectTold = 0;
            }
            else if (!request.Attach && at >= 0)
            {
                _mirrors[at].Dispose();
                _mirrors.RemoveAt(at);
            }
        }
    }

    /// <summary>Each mirror's turn, after the player's (never waits: GpuViewportControl.PresentMirror), and the shape told when it changed.</summary>
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
    /// Show a clip's own picture, whole and unfinished, instead of the sequence (true while a crop or a Cutout's place is drawn
    /// on a clip's source: Interactive Crop, a Draw Target): its file itself (5.2-AuditB: any clip's, where it was the primary
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

    /// <summary>
    /// A thumbnail asked for, with all it is drawn as (5.4D review: one record, exchanged as a whole, so a request the pump has taken is
    /// never drawn as a newer one asks, a project's thumbnail never in 3D, a tab's 3D still never flat): what completes it, the plan it
    /// draws when the player's has fades (the same state without them; null: the player's), the short side asked for, and whether it is
    /// the picture as the player shows it (5.4D: in the 3D view, a project tab's still).
    /// </summary>
    private sealed record ThumbnailRequest(TaskCompletionSource<(byte[] Bgra, int Width, int Height)?> Done, GpuScenePlan? Plan, int ShortSide, bool AsShown);

    private ThumbnailRequest? _thumbnail;

    /// <summary>The longest side of a still drawn as it is shown, in pixels (a player many times wider than tall is drawn smaller).</summary>
    private const int ShownStillLongest = 4096;

    /// <summary>How long the pump waits for a thumbnail to be read back, ms: one that takes longer gives none (the save keeps the last one).</summary>
    private const int ThumbnailReadbackMs = 3000;

    /// <summary>
    /// The frame at the playhead as the player composes it (its proxies, its captions), without the fades (a project that fades in is not
    /// black at its start), drawn small: the short side <paramref name="shortSide"/> (never more than the canvas's), both sides even, BGRA8
    /// (alpha meaningless). Its own readback, the size of that picture: the player's is not grown to the canvas for it. A project file's
    /// thumbnail (ProjectThumbnail). Null at once while playing (no played frame ever waits for one: Law 3) or with nothing loaded, and
    /// null when it could not be drawn in time. Called on the UI thread (a plan without the fades is made there when the player's has
    /// them); drawn on the pump's thread, and completed off both.
    /// <para>5.4D: with <paramref name="asShown"/> (a project tab's still, MainWindow.ProjectTabs.cs) and the 3D view on, the picture as
    /// the player shows it: the 3D view where its camera is going, in the player's shape (<see cref="ShownStillSize"/>). The project's own
    /// thumbnail is never asked for so: it stays flat.</para>
    /// </summary>
    public Task<(byte[] Bgra, int Width, int Height)?> CaptureThumbnailAsync(int shortSide = ProjectThumbnail.ShortSide, bool asShown = false)
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

        Interlocked.Exchange(ref _thumbnail, new ThumbnailRequest(request, unfaded, Math.Max(shortSide, 16), asShown))?.Done.TrySetResult(null);
        _dirty.Set();
        return request.Task;
    }

    /// <summary>Whether a thumbnail asked for is still waiting for the pump (5.4C review): a newer request would give that one none.</summary>
    public bool IsThumbnailWaiting => Volatile.Read(ref _thumbnail) is not null;

    /// <summary>
    /// The size of a still drawn as it is shown (5.4D, pure: the goldens' project-tabs-exploded-pose): the player's shape (its pixels;
    /// the canvas's while it is too small to draw on), the short side <paramref name="shortSide"/> at most (never more than the player's
    /// own), both sides even, and the long side at most <see cref="ShownStillLongest"/>.
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
            plan.Evaluate(at, ref header, layers);
            header.PlayheadSeconds = at;

            if (asked.AsShown && ShownPose() is { } pose)
            {
                // As the player shows it (5.4D): the 3D view in the player's shape, the canvas laid in it as the player lays it.
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

        // A file opened ahead of a drop: its first frames; the project tabs likely next (5.4.2A): their playheads' half second. Idle work
        // (5.4.4: GpuSeekHintKind.TabRoll, after everything else the worker does), named only while the player is quiet: not playing, not
        // scrubbing, and the playhead at rest DecoderPool.IdleQuietMs since its last seek.
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

    // The most hints of the caller's ahead of the rest: a drop's file, and the two tabs' videos (four each at most, PrewarmTabs' callers).
    private const int MostExtras = 9;

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

        var scrub = ScrubbingFor - (now - Volatile.Read(ref _lastSeekTicks)) * 1.0 / Stopwatch.Frequency;
        if (scrub > 0)
            wait = Math.Min(wait, scrub);

        // A picture drawn smaller for a scrub: drawn whole CrispAfter after its last move (Dynamic Preview Scaling).
        if (_lastScale < 1)
            wait = Math.Min(wait, Math.Max(CrispAfter - (now - Volatile.Read(ref _scrubMovedTicks)) * 1.0 / Stopwatch.Frequency, 0.001));
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
        Interlocked.Exchange(ref _thumbnail, null)?.Done.TrySetResult(null);
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
