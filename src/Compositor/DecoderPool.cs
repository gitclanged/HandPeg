using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// Compositor media layer: source table, warm decoders, frame cache, and the decode lanes that fill it.
/// <para><b>Instant Seek.</b> The renderer never waits for a decoder. For each media layer it calls <see cref="Resolve"/> for the frame at
/// the layer's time: either cached (exact), or the nearest available frame is drawn meanwhile and a demand for the exact frame goes to the
/// lanes; when it lands, <see cref="FramesArrived"/> wakes the worker to redraw. Demands, most urgent first:</para>
/// <list type="number">
/// <item>Visible: frames the newest scene shows but lacks (a scrub moves them every frame).</item>
/// <item>Play-ahead: while playing, ~1/3 s past what is shown, so playback never waits.</item>
/// <item>Hinted: what the client expects next: scrub direction, then the first frames of every cut within the look-ahead horizon,
/// earliest first, held in the cache until the hint is removed.</item>
/// <item>Pre-roll: while paused, the range around the playhead, so frame steps and short scrubs never decode.</item>
/// <item>Hover: the timeline pointer (its frame, or a short play-ahead once it rests), lowest priority.</item>
/// </list>
/// <para>Each lane (a thread) takes the most urgent unserved demand, picks the source decoder reaching it in the fewest frames (opening
/// another, up to the per-source limit, rather than stealing the one parked at the playhead for a hint), and decodes frame by frame,
/// keeping every frame, until the target is cached; between frames it yields to anything more urgent. A source whose file fails to open
/// is not retried until the client changes it.</para>
/// </summary>
internal sealed partial class DecoderPool : IDisposable
{
    public const int Capacity = 12;
    private const int MostPerSource = 4;

    /// <summary>
    /// Default and maximum lane counts: each lane sleeps until there is a frame to decode, so several tracks chase a seek in parallel rather
    /// than taking turns.
    /// </summary>
    private const int AutoLanes = 6, MostLanes = 8;

    /// <summary>
    /// Frame commit barrier, ms: with two or more videos on screen, a picture mixing new-time and old-time frames is not shown; the last
    /// complete picture stays until all are ready or this elapses. A single video needs no barrier (its nearest frame is Instant Seek).
    /// </summary>
    private const double SeekBarrierMs = 80;

    /// <summary>
    /// Per-source decoder limit with upcoming cuts: one reading the visible frame plus one parked at each cut within the horizon (a quick-cut
    /// sequence of one recording has 3-4 cuts in 3 s).
    /// </summary>
    private const int MostPerSourceForCuts = 6;

    /// <summary>How far behind a requested frame the frames decoded on the way are kept (scrub back, frame step back), seconds.</summary>
    private const double KeepBehind = 1.5;

    /// <summary>Play-ahead decode distance beyond the shown frame (file time at its own speed), seconds.</summary>
    private const double PlayAhead = 0.35;

    /// <summary>Paused pre-roll range before and after the playhead, seconds.</summary>
    private const double PreRollBefore = 1.0, PreRollAfter = 2.0;

    /// <summary>
    /// Exact-frame scenes (exports, filmstrips, tests): frames kept behind the requested frame, and lane read-ahead while it is drawn and read
    /// back. Short because exports never step back and 1/3 s of a 420 fps 1440p recording would overflow the cache (evicting frames about
    /// to be needed).
    /// </summary>
    private const double ExactKeepBehind = 0.1, ExactPlayAhead = 0.1;

    /// <summary>Maximum timestamp deviation from true frame times, seconds (Matroska's millisecond clock).</summary>
    private const double StampHair = 0.002;

    /// <summary>Frames of an upcoming cut's clip decoded ahead of the playhead (the first 6; a shorter clip entirely).</summary>
    private const int CutEntryFrames = 6;

    /// <summary>Maximum play-ahead from a resting timeline pointer, seconds of file time.</summary>
    private const double HoverRollMost = 0.5;

    private readonly GpuDevice _gpu;
    private readonly CompositorSharedMemory _shared;
    private readonly FrameCache _cache;
    private readonly object _gate = new();
    private readonly Dictionary<int, SourceState> _sources = [];
    private readonly List<Demand> _demands = [];
    private readonly Dictionary<long, LayerMemory> _layers = [];
    private readonly List<CachedFrame> _pinned = [];
    private readonly AutoResetEvent _work = new(false);
    private readonly AutoResetEvent _arrived = new(false);
    private readonly ManualResetEvent _stopping = new(false);
    private readonly List<Thread> _lanes = [];
    private readonly long[] _laneBusySince;
    private int _sourcesVersion = -1;
    private int _hintsVersion = -1;
    private long _frame;
    private int _serials;
    private volatile Exception? _laneFault;
    private int _waitingExact;
    private bool _lastWasPlaying;
    private double _lastPlayhead = double.NaN;

    public DecoderPool(GpuDevice gpu, CompositorSharedMemory shared)
    {
        (_gpu, _shared) = (gpu, shared);
        _cache = new FrameCache(gpu);
        _cache.SetBudget(Volatile.Read(ref shared.Control.CacheBudgetRequest));
        var requested = Volatile.Read(ref shared.Control.DecodeThreadsRequest);
        var lanes = requested > 0 ? Math.Min(requested, MostLanes) : gpu.IsWarp ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 2, AutoLanes);
        _laneBusySince = new long[lanes];
        for (var i = 0; i < lanes; i++)
        {
            var lane = i;
            var thread = new Thread(() => RunLane(lane)) { Name = $"Decode lane {lane}", IsBackground = true };
            thread.SetApartmentState(ApartmentState.MTA);
            _lanes.Add(thread);
            thread.Start();
        }
    }

    /// <summary>Last error (e.g. a file that fails to open), published in the control block message.</summary>
    public string LastError { get; private set; } = "";

    public int Opens => Volatile.Read(ref _opens);

    private int _seeks, _decoded;

    /// <summary>Seeks and decoded frames since worker start (lane counters, lock-free).</summary>
    public int Seeks => Volatile.Read(ref _seeks);

    public int FramesDecoded => Volatile.Read(ref _decoded);

    /// <summary>Signalled by a lane when a frame the scene awaits is decoded (or any frame for a layer with nothing yet).</summary>
    public WaitHandle FramesArrived => _arrived;

    /// <summary>True when every media layer of the last drawn frame had its exact frame.</summary>
    public bool LastFrameExact { get; private set; } = true;

    public FrameCache Cache => _cache;

    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }
    public long PredictedHits { get; private set; }
    public float LastExactLatencyMs { get; private set; }
    public float WorstExactLatencyMs { get; private set; }

    public int BusyLanes
    {
        get
        {
            var busy = 0;
            for (var i = 0; i < _laneBusySince.Length; i++)
                busy += Volatile.Read(ref _laneBusySince[i]) != 0 ? 1 : 0;
            return busy;
        }
    }

    /// <summary>True when a lane has been stuck in one call longer than <paramref name="seconds"/> (hung driver): the worker stops its heartbeat and is replaced.</summary>
    public bool LaneStuckFor(double seconds)
    {
        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < _laneBusySince.Length; i++)
        {
            var since = Volatile.Read(ref _laneBusySince[i]);
            if (since != 0 && (now - since) > seconds * Stopwatch.Frequency)
                return true;
        }

        return false;
    }

    /// <summary>A lane fault that must terminate the worker (device lost), or null.</summary>
    public Exception? LaneFault => _laneFault;

    // ----- Per-frame renderer interface -----

    public void BeginFrame(long frame, bool playing, double playhead, bool exactOnly = false, bool scrubbing = false, bool ghost = false)
    {
        // Scene arrival time (play-rate sample), read before any wait below.
        var arrived = Stopwatch.GetTimestamp();
        _frame = frame;
        _exactOnly = exactOnly;

        // The editor's held playhead drag (FrameSceneHeader.FlagScrubbing): paused live frames only. Its release (flag cleared) chases the
        // exact frame at once until the next held drag.
        var held = scrubbing && !exactOnly && !playing;
        if (Volatile.Read(ref _scrubHeld) && !held)
            Volatile.Write(ref _scrubReleased, true);
        else if (held)
            Volatile.Write(ref _scrubReleased, false);
        Volatile.Write(ref _scrubHeld, held);
        Volatile.Write(ref _scrubGhost, held && ghost);

        // Number of videos in the last picture: each gets a cache share and its own lane for deferrable work.
        var (videos, seen) = (0, 0UL);
        foreach (var (serial, _) in _resolvedNow)
        {
            var bit = 1UL << (serial & 63);
            videos += (seen & bit) == 0 ? 1 : 0;
            seen |= bit;
        }

        _videosShown = Math.Max(videos, 1);
        _resolvedNow.Clear();
        _pendingNow = 0;
        SyncSources();

        // Exact-frame scenes (export, capture) never skip a layer for a pending stamp: settle now.
        if (exactOnly)
            SettleStamps(wait: true, force: true);

        // Settling: paused, or just jumped while playing; multi-video pictures then wait for all videos (HoldsForOthers).
        var jumped = !double.IsNaN(_lastPlayhead) && Math.Abs(playhead - _lastPlayhead) > SeekJump;
        _settling = !exactOnly && (!playing || jumped || (_settling && !LastFrameExact));
        if (LastFrameExact || jumped)
            _barrierFrom = 0;

        // A moving playhead (playing, seek, scrub step) keeps idle work off the lanes for IdleQuietMs.
        if (!exactOnly && (playing || !double.IsNaN(_lastPlayhead) && Math.Abs(playhead - _lastPlayhead) > 1e-9))
            Volatile.Write(ref _activeUntil, Stopwatch.GetTimestamp() + IdleQuietMs * Stopwatch.Frequency / 1000);

        // Paused playhead moves (scrub steps, seeks): their rhythm tells an active scrub (ScrubbingAt) from single steps.
        if (!exactOnly && !playing && !double.IsNaN(_lastPlayhead) && Math.Abs(playhead - _lastPlayhead) > 1e-9)
        {
            var now = Stopwatch.GetTimestamp();
            Volatile.Write(ref _scrubMoves, NextScrubMoves(Volatile.Read(ref _scrubMoves), now, Volatile.Read(ref _lastMoveAt), Stopwatch.Frequency));
            Volatile.Write(ref _lastMoveAt, now);
        }

        // Cold play: a jump whose first picture missed a frame (LastFrameExact still refers to that picture here) is a cold seek and arms
        // the window, as a source change does (SyncSources); the first playing frame opens it, a pause closes it. Exact-frame scenes
        // (export, capture) never use it.
        if (!exactOnly)
        {
            _coldArmed |= _jumpProbe && !LastFrameExact;
            _jumpProbe = jumped;
            if (!playing)
            {
                Volatile.Write(ref _coldPlayUntil, 0);
            }
            else if (_coldArmed)
            {
                _coldArmed = false;
                Volatile.Write(ref _coldPlayUntil, Stopwatch.GetTimestamp() + (long)(ColdPlaySeconds * Stopwatch.Frequency));
            }

            // Sample the play rate the window's lead follows (pause, jump or new sources reset it to 1).
            SamplePlayRate(playing, jumped, playhead, arrived);
        }

        // Mark visible and play-ahead demands stale; the frame's Resolve calls refresh the ones still needed.
        lock (_gate)
        {
            foreach (var demand in _demands)
            {
                if (demand.Kind is DemandKind.Visible or DemandKind.PlayAhead)
                    demand.Stale = true;
            }
        }

        (_lastWasPlaying, _lastPlayhead) = (playing, playhead);
        LastFrameExact = true;
        _cache.CheckWindowsBudget(Stopwatch.GetTimestamp());
    }

    // Playhead move treated as a jump (not a frame step), seconds; whether the display is settling after one; barrier start time.
    private const double SeekJump = 0.25;
    private bool _settling;
    private long _barrierFrom;

    /// <summary>
    /// True when the just-resolved picture should be withheld: a settling multi-video picture with some videos not yet at their frame,
    /// within the barrier time. Awaited frames wake the worker (<see cref="FramesArrived"/>) to redraw.
    /// </summary>
    public bool HoldsForOthers()
    {
        if (LastFrameExact || !_settling || _exactOnly)
            return false;

        var (first, several) = (-1, false);
        foreach (var (serial, _) in _resolvedNow)
        {
            several |= first >= 0 && serial != first;
            first = first < 0 ? serial : first;
        }

        // A layer still awaiting its source stamp (_pendingNow) is a required layer with no drawable fallback: the last complete picture
        // stays up as for another video not yet at its frame.
        if (!several && _pendingNow == 0)
            return false;

        var now = Stopwatch.GetTimestamp();
        if (_barrierFrom == 0)
            _barrierFrom = now;
        return (now - _barrierFrom) * 1000.0 / Stopwatch.Frequency < SeekBarrierMs;
    }

    // Frames after the displayed one that must be decoded before play starts.
    private const int PlayStartAhead = 2;

    /// <summary>True when the last picture was drawn while playing.</summary>
    public bool Playing => _lastWasPlaying;

    /// <summary>
    /// True when every video in the last picture also has its next frames decoded (single-video pictures never wait: true). Files ending
    /// sooner have nothing to wait for. Worker thread only (the thread that fills what it reads).
    /// </summary>
    public bool NextFramesReady()
    {
        var (first, several) = (-1, false);
        foreach (var (serial, _) in _resolvedNow)
        {
            several |= first >= 0 && serial != first;
            first = first < 0 ? serial : first;
        }

        if (!several)
            return true;

        foreach (var (serial, at) in _resolvedNow)
        {
            SourceState? source = null;
            lock (_gate)
            {
                foreach (var known in _sources.Values)
                {
                    if (known.Serial == serial)
                    {
                        source = known;
                        break;
                    }
                }
            }

            if (source is null or { Failed: true })
                continue;

            var frame = source.FrameDuration > 0 ? source.FrameDuration : 1 / 30.0;
            var to = at + frame * PlayStartAhead;
            if (source.Duration > 0 && to >= source.Duration - frame)
                continue;
            if (!_cache.Covers(serial, at, to))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Frame to draw for a media layer: the exact frame when decoded, else the nearest available (plus a demand for the exact one), else null
    /// (nothing decoded for this source yet: the layer is skipped this frame). Pinned until <see cref="EndFrame"/>.
    /// </summary>
    public CachedFrame? Resolve(int id, double seconds, long pathHash, long clipKey)
    {
        if (id is < 0 or >= CompositorProtocol.MaxSources)
            return null;
        SourceState? source;
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out source) || source.Failed)
            {
                // An exact-frame scene with an undrawable media layer is not exact: the export fails over to FFmpeg rather than silently
                // dropping the layer. A source with a pending stamp is skipped briefly; the frame is inexact and redrawn when the stamp
                // returns (FramesArrived).
                var pending = source is null && _deciding.ContainsKey(id);
                if (_exactOnly && source is { Failed: true } || pending)
                    LastFrameExact = false;

                // A visible layer awaiting its stamp has nothing to draw: HoldsForOthers counts it like a source not yet at its frame.
                if (pending)
                    _pendingNow++;
                return null;
            }
        }

        if (pathHash != 0 && pathHash != source.PathHash)
            return null;
        seconds = Math.Max(seconds, 0);
        if (source.Duration > 0)
            seconds = Math.Min(seconds, source.Duration);
        source.LastWanted = Stopwatch.GetTimestamp();

        LayerMemory? memory;
        lock (_gate)
            _layers.TryGetValue(clipKey, out memory);

        // Layers of one source at one time (a video, its Cutouts, its blur fill twin) share one frame and its demands: only the first
        // in a frame issues them.
        var repeat = false;
        foreach (var (serial, at) in _resolvedNow)
            repeat |= serial == source.Serial && Math.Abs(at - seconds) <= 1e-6;
        if (!repeat && _resolvedNow.Count < 64)
            _resolvedNow.Add((source.Serial, seconds));

        // Exact-frame scenes (export, capture) leave an open frame's last millisecond to the next frame (CachedFrame.CoverEnd); the player
        // takes what is available.
        var exact = _cache.Exact(source.Serial, seconds, strictly: _exactOnly);
        if (exact is not null)
        {
            CacheHits++;
            if (exact.Ahead)
                PredictedHits++;
            if (!repeat)
                Satisfied(source, seconds);
        }
        else
        {
            CacheMisses++;
            LastFrameExact = false;
            if (!repeat)
                Ask(source, seconds, DemandKind.Visible, clipKey, keepFrom: seconds - Fit(source, KeepBehind, BehindShare));
            exact = _cache.Nearest(source.Serial, seconds, memory?.Shown);
        }

        // While playing, decode slightly ahead (at the layer's own speed through its file).
        if (_lastWasPlaying && !repeat)
        {
            // Media speed since the layer's last frame; a jump (cut, loop) is not a speed, so fall back to 1.
            var rate = 1.0;
            if (memory is { } m && !double.IsNaN(m.Playhead) && _lastPlayhead > m.Playhead + 1e-4)
            {
                var (media, played) = (seconds - m.Media, _lastPlayhead - m.Playhead);
                if (Math.Abs(media) <= played * 4 + 0.05)
                    rate = Math.Clamp(media / played, 0.1, 4);
            }
            Ask(source, seconds + Fit(source, (_exactOnly ? ExactPlayAhead : PlayAhead) * rate, AheadShare), DemandKind.PlayAhead, clipKey, keepFrom: seconds);
        }

        lock (_gate)
        {
            if (memory is null)
                _layers[clipKey] = memory = new LayerMemory();
            (memory.Media, memory.Playhead, memory.Shown, memory.LastFrame) = (seconds, _lastPlayhead, exact, _frame);
            (memory.Source, memory.Playing) = (source.Serial, _lastWasPlaying);
        }

        // The player keeps each drawn layer's frame held across frames, so a multi-layer scrub never leaves it without a stand-in.
        if (!_exactOnly)
            HoldShown(memory, exact);
        if (exact is not null)
            _pinned.Add(exact);
        return exact;
    }

    /// <summary>
    /// Player frames (Pass 5.6.4A): keeps a layer's shown frame pinned across frames (one extra pin, taken under the cache's lock), so the
    /// evictions of a multi-layer scrub (each source's demands crowding the others' frames) always leave FrameCache.Nearest its LayerMemory
    /// stand-in and a layer never drops out (black) while its exact frame decodes. Released when the layer shows another frame, or is not
    /// drawn in a frame (<see cref="ReleaseHolds"/>, EndFrame). Render thread.
    /// </summary>
    private void HoldShown(LayerMemory memory, CachedFrame? shown)
    {
        if (ReferenceEquals(memory.Held, shown))
            return;
        if (memory.Held is { } before)
        {
            memory.Held = null;
            _cache.Unpin(before);
        }

        if (shown is null)
            return;

        // The frame covering its own moment is itself while it is cached; anything else (just replaced) is not held.
        var again = _cache.Exact(shown.Source, shown.Time, strictly: false);
        if (ReferenceEquals(again, shown))
            memory.Held = shown;
        else if (again is not null)
            _cache.Unpin(again);
    }

    // Holds let go by ReleaseHolds, unpinned after the gate.
    private readonly List<CachedFrame> _letGo = [];

    /// <summary>Lets go of the held frames (<see cref="HoldShown"/>) of layers not drawn in this frame.</summary>
    private void ReleaseHolds()
    {
        lock (_gate)
        {
            foreach (var memory in _layers.Values)
            {
                if (memory.Held is { } held && memory.LastFrame != _frame)
                {
                    memory.Held = null;
                    _letGo.Add(held);
                }
            }
        }

        foreach (var frame in _letGo)
            _cache.Unpin(frame);
        _letGo.Clear();
    }

    /// <summary>
    /// Companion frame for a layer resolved this frame (Smooth Slow-Mo's next frame, SceneRenderer.Interpolate): found like
    /// <see cref="Resolve"/> and pinned until <see cref="EndFrame"/>, but without its own layer state. No LayerMemory is kept for the salted
    /// <paramref name="clipKey"/>, so no play rate, PlayAhead or extra pre-roll; a miss issues one Visible demand under the salted key
    /// (leaving the layer's own demand intact) and marks the frame inexact, so exports wait for it.
    /// </summary>
    public CachedFrame? ResolveNext(int id, double seconds, long pathHash, long clipKey)
    {
        if (id is < 0 or >= CompositorProtocol.MaxSources)
            return null;
        SourceState? source;
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out source) || source.Failed)
                return null;
        }

        if (pathHash != 0 && pathHash != source.PathHash)
            return null;
        seconds = Math.Max(seconds, 0);
        if (source.Duration > 0)
            seconds = Math.Min(seconds, source.Duration);
        source.LastWanted = Stopwatch.GetTimestamp();

        // Another layer at this time (another clip of the file) already requested it: no further demand.
        var repeat = false;
        foreach (var (serial, at) in _resolvedNow)
            repeat |= serial == source.Serial && Math.Abs(at - seconds) <= 1e-6;

        var exact = _cache.Exact(source.Serial, seconds, strictly: _exactOnly);
        if (exact is not null)
        {
            CacheHits++;
            if (!repeat)
                Satisfied(source, seconds);
        }
        else
        {
            CacheMisses++;
            LastFrameExact = false;
            if (!repeat)
                Ask(source, seconds, DemandKind.Visible, clipKey, keepFrom: seconds - Fit(source, KeepBehind, BehindShare));
            exact = _cache.Nearest(source.Serial, seconds, null);
        }

        if (exact is not null)
            _pinned.Add(exact);
        return exact;
    }

    /// <summary>
    /// The cached frame preceding <paramref name="frame"/> (shutter motion reference), pinned until <see cref="EndFrame"/>. No demand is
    /// issued, so nothing waits; exports keep <see cref="ExactKeepBehind"/> behind, so it exists for all but a clip's first frame.
    /// </summary>
    public CachedFrame? FrameBefore(CachedFrame frame)
    {
        var before = _cache.Before(frame, ExactKeepBehind);
        if (before is not null)
            _pinned.Add(before);
        return before;
    }

    /// <summary>
    /// Re-reads the source table when the client changed it. Called at every frame start and before exact-frame waits (the first scene after
    /// SetSource must know the new source, or it would wait for nothing).
    /// </summary>
    public void SyncSources()
    {
        var version = Volatile.Read(ref _shared.Control.SourcesVersion);
        if (version != _sourcesVersion)
        {
            _sourcesVersion = version;

            // New sources (tab activated, file opened, fresh worker) are cold: the next play defers far cuts and resamples the play rate.
            _coldArmed = true;
            _rateRestart = true;
            Resync();
            DisposeBuried();

            // Tab reactivated while paused: stamps are read on the pool and briefly awaited (StampWaitMs) so the first frame finds the
            // cached frames. While playing nothing waits; they settle as they return.
            SettleStamps(wait: !_lastWasPlaying, force: false);
        }
        else if (Volatile.Read(ref _decidingCount) > 0)
        {
            SettleStamps(wait: false, force: false);
        }
    }

    // Current frame is exact-frame (export, filmstrip, test): no pre-roll around it.
    private bool _exactOnly;

    // (source, time) pairs resolved this frame: a layer matching an existing pair issues no demands.
    private readonly List<(int Serial, double Seconds)> _resolvedNow = new(16);

    // Visible layers this frame whose source awaits its stamp (Resolve returned null before _resolvedNow); reset with it in BeginFrame.
    private int _pendingNow;

    // Times no frame covers (stream holes), remembered for a few seconds: not re-decoded or waited for.
    private readonly List<(int Serial, double Seconds, long Until)> _missing = [];

    private int _stretches, _lateLandings, _missingFrames;

    /// <summary>Diagnostics: frames stretched over dropped frames, re-seeks from further back, and times with no frame.</summary>
    public int Stretches => Volatile.Read(ref _stretches);

    public int LateLandings => Volatile.Read(ref _lateLandings);

    public int MissingFrames => Volatile.Read(ref _missingFrames);

    /// <summary>True when a source time is known to have no frame (under the lock).</summary>
    private bool IsMissing(int serial, double seconds)
    {
        if (_missing.Count == 0)
            return false;
        var now = Stopwatch.GetTimestamp();
        _missing.RemoveAll(m => m.Until < now);
        foreach (var (s, at, _) in _missing)
        {
            if (s == serial && Math.Abs(at - seconds) < 1e-4)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Requests a media layer's exact frame without drawing (scenes that wait for exact frames: exports, filmstrips, tests). True when already
    /// cached.
    /// </summary>
    public bool Request(int id, double seconds, long pathHash, long clipKey)
    {
        if (id is < 0 or >= CompositorProtocol.MaxSources)
            return true;
        SyncSources();
        SettleStamps(wait: true, force: true);
        SourceState? source;
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out source) || source.Failed)
                return true;
        }

        if (pathHash != 0 && pathHash != source.PathHash)
            return true;
        seconds = Math.Max(seconds, 0);
        if (source.Duration > 0)
            seconds = Math.Min(seconds, source.Duration);
        source.LastWanted = Stopwatch.GetTimestamp();
        if (_cache.Has(source.Serial, seconds))
            return true;
        lock (_gate)
        {
            if (IsMissing(source.Serial, seconds))
                return true;
        }

        // Only exact-frame scenes use this path: keep little behind the frame (ExactKeepBehind).
        Ask(source, seconds, DemandKind.Visible, clipKey, keepFrom: seconds - ExactKeepBehind);
        return false;
    }

    /// <summary>Frame drawn: unpins its frames, drops stale demands and (when paused) issues pre-roll demands.</summary>
    public void EndFrame()
    {
        foreach (var frame in _pinned)
            _cache.Unpin(frame);
        _pinned.Clear();

        // Layers not drawn in this frame let their held frames go (before the orphans are collected, so a retired one is freed now).
        ReleaseHolds();
        _cache.CollectOrphans();
        NoteDrawn();

        lock (_gate)
        {
            // Forget layers unseen for a while.
            if (_layers.Count > 256)
            {
                foreach (var key in _layers.Where(p => _frame - p.Value.LastFrame > 120).Select(p => p.Key).ToList())
                    _layers.Remove(key);
            }

            _demands.RemoveAll(d => d.Stale && d.Lane < 0 || d.Done && d.Kind is not (DemandKind.PreRoll or DemandKind.Hinted or DemandKind.Hover or DemandKind.Idle));
            foreach (var demand in _demands)
            {
                if (demand.Stale)
                    demand.Cancelled = true;
            }

            _demands.RemoveAll(d => d.Cancelled && d.Lane < 0);

            // Paused: decode each layer's neighbourhood in idle time (player frames only; exports and filmstrips request what they
            // need).
            if (!_lastWasPlaying && !_exactOnly)
            {
                // Drop pre-roll ranges of clips no longer drawn (they would crowd the cache interests).
                _demands.RemoveAll(d => d.Kind == DemandKind.PreRoll && d.Lane < 0 && (!_layers.TryGetValue(d.ClipKey, out var seen) || seen.LastFrame != _frame));

                // Playhead still moving (scrub, seek): no range is read until PreRollQuietMs past the last move (MostUrgent), and one already
                // under way stops (it holds a hardware decoder and the device lock the scrub's own frames need).
                // A held drag (FlagScrubbing) also stops a scrub-ahead read under way: one decode engine and device lock serve the drag.
                var held = Volatile.Read(ref _scrubHeld);
                if (!PreRollMayRun(Stopwatch.GetTimestamp(), Volatile.Read(ref _lastMoveAt), Stopwatch.Frequency, held))
                {
                    foreach (var busy in _demands)
                    {
                        if (busy.Lane >= 0 && (busy.Kind == DemandKind.PreRoll || held && busy.HintKind == GpuSeekHintKind.ScrubAhead))
                            busy.Cancelled = true;
                    }
                }

                foreach (var (clipKey, memory) in _layers)
                {
                    if (memory.LastFrame != _frame)
                        continue;
                    var source = SourceBySerial(memory.Source);
                    if (source is null)
                        continue;
                    // Clamped to this source's cache share (1/3 before, 2/3 after). Larger caches widen it up to 4x each way.
                    // Multiple videos split the share so they do not evict each other's frames.
                    var stretch = Fit(source, (PreRollBefore + PreRollAfter) * Math.Clamp(_cache.BudgetBytes / (double)(3L << 30), 1, 4), PreRollShare / _videosShown);
                    // Multiple videos: nearly all ahead, since ranges decode from their start and play needs the ahead part first.
                    var behind = _videosShown > 1 ? 0.1 : PreRollBefore / (PreRollBefore + PreRollAfter);
                    var (before, after) = (stretch * behind, stretch * (1 - behind));
                    var from = Math.Max(memory.Media - before, 0);
                    var to = source.Duration > 0 ? Math.Min(memory.Media + after, source.Duration) : memory.Media + after;

                    // One range per layer (two clips of one file at different times each keep their own).
                    if (!HasPreRoll(clipKey, source, to))
                    {
                        // Cancel the old range, including one in progress (it would tie a lane to an unseen range).
                        foreach (var stale in _demands)
                        {
                            if (stale.Kind == DemandKind.PreRoll && stale.ClipKey == clipKey && stale.Lane >= 0)
                                stale.Cancelled = true;
                        }

                        _demands.RemoveAll(d => d.Kind == DemandKind.PreRoll && d.ClipKey == clipKey && d.Lane < 0);
                        _demands.Add(new Demand { Source = source, Target = to, KeepFrom = from, Kind = DemandKind.PreRoll, Stretch = true, ClipKey = clipKey, Asked = Stopwatch.GetTimestamp() });
                    }
                }
            }
            else
            {
                _demands.RemoveAll(d => d.Kind == DemandKind.PreRoll && d.Lane < 0);
            }

            UpdateInterests();
        }

        _work.Set();
    }

    /// <summary>Waits (with heartbeat) until every frame missed by the last <see cref="Resolve"/> calls is decoded: exports, filmstrips, tests.</summary>
    public bool WaitForExact(Func<bool> allExact, int milliseconds, Action beat)
    {
        var clock = Stopwatch.StartNew();
        Interlocked.Increment(ref _waitingExact);
        try
        {
            while (!allExact())
            {
                if (_laneFault is not null || clock.ElapsedMilliseconds > milliseconds)
                    return false;
                beat();
                _arrived.WaitOne(20);
            }

            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _waitingExact);
        }
    }

    /// <summary>True when source <paramref name="id"/>'s frame at <paramref name="seconds"/> is decoded (no pin).</summary>
    public bool HasExact(int id, double seconds)
    {
        SourceState? source;
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out source))
                return !_deciding.ContainsKey(id);
            if (source.Failed)
                return true;
        }

        seconds = Math.Max(seconds, 0);
        if (source.Duration > 0)
            seconds = Math.Min(seconds, source.Duration);
        if (_cache.Has(source.Serial, seconds))
            return true;

        // Times with no frame are not awaited: drawn with the nearest frame and counted as approximate.
        lock (_gate)
            return IsMissing(source.Serial, seconds);
    }

    // ----- Hints -----

    /// <summary>
    /// Re-reads client hints when changed, turning each into a demand (scrub direction, first frames of upcoming cuts, pointer time or its
    /// play-ahead). Existing hints keep their demands (an active lane continues; satisfied ones stay satisfied); others are dropped. Cut
    /// ranges are held in the cache until their hints are removed.
    /// </summary>
    public void TakeHints()
    {
        ref var control = ref _shared.Control;
        var version = Volatile.Read(ref control.HintsVersion);
        if (version == _hintsVersion)
            return;
        _hintsVersion = version;

        // Hints may reference sources added since the last frame (a plan's new file, a file opened ahead of a drop): sync the table
        // first or they would be dropped as unknown.
        SyncSources();
        var count = Math.Clamp(Volatile.Read(ref control.HintCount), 0, CompositorProtocol.MaxHints);
        lock (_gate)
        {
            foreach (var demand in _demands)
                demand.Kept = false;
            _held.Clear();
            var heldBytes = 0L;
            for (var i = 0; i < count; i++)
            {
                var hint = control.Hints[i];
                if (!_sources.TryGetValue(hint.SourceId, out var source) || source.Failed || hint.PathHash != 0 && hint.PathHash != source.PathHash)
                    continue;
                var seconds = Math.Max(hint.MediaSeconds, 0);
                if (source.Duration > 0)
                    seconds = Math.Min(seconds, source.Duration);
                var kind = (GpuSeekHintKind)hint.Kind;
                var frame = source.FrameDuration > 0 ? source.FrameDuration : 1 / 30.0;
                source.LastWanted = Stopwatch.GetTimestamp();
                var span = float.IsFinite(hint.Span) ? Math.Max(hint.Span, 0) : 0;

                // Per kind: a single frame (with some kept behind) or a range starting at the hint time.
                var (demandKind, urgency, stretch, keepFrom, target) = kind switch
                {
                    GpuSeekHintKind.ScrubAhead => (DemandKind.Hinted, 0, false, seconds - Fit(source, KeepBehind, BehindShare), seconds),
                    GpuSeekHintKind.CutEntry => (DemandKind.Hinted, 1, true, seconds, seconds + Math.Min(span, Fit(source, (CutEntryFrames - 0.5) * frame, CutShare))),
                    GpuSeekHintKind.Pointer => (DemandKind.Hover, 0, false, seconds - Fit(source, 0.5, BehindShare), seconds),
                    GpuSeekHintKind.HoverRoll => (DemandKind.Hover, 1, true, seconds, seconds + Math.Min(span, Fit(source, HoverRollMost, HoverShare))),
                    GpuSeekHintKind.TabRoll => (DemandKind.Idle, 0, true, seconds, seconds + Math.Min(span, Fit(source, HoverRollMost, HoverShare))),
                    _ => (DemandKind.Hinted, 2, false, seconds - Fit(source, 0.5, BehindShare), seconds),
                };
                if (source.Duration > 0)
                    target = Math.Min(target, source.Duration);

                // Hold a cut's first frames until its hint is removed (playhead passed it or it left the horizon), within a budget
                // share; beyond the share, only its first frame.
                if (kind == GpuSeekHintKind.CutEntry)
                {
                    var bytes = source.FrameBytes * (long)Math.Ceiling((target - keepFrom) / frame + 1);
                    if (source.FrameBytes > 0 && heldBytes + bytes > _cache.BudgetBytes * HeldShare)
                        (target, bytes) = (keepFrom, source.FrameBytes);
                    if (source.FrameBytes <= 0 || heldBytes + bytes <= _cache.BudgetBytes * HeldShare)
                    {
                        heldBytes += bytes;
                        _held.Add((source.Serial, keepFrom, target + frame * 0.5));
                    }
                }

                var at = float.IsFinite(hint.At) ? hint.At : 0;
                var known = false;
                foreach (var demand in _demands)
                {
                    if (demand.Kind != demandKind || demand.HintKind != kind || demand.Source != source || demand.Cancelled || Math.Abs(demand.HintMedia - seconds) > 1e-6)
                        continue;
                    (demand.Kept, demand.At, known) = (true, at, true);

                    // Range length changed (frame duration now known): re-queue unless a lane holds it.
                    if (Math.Abs(demand.Target - target) > 1e-6 && demand.Lane < 0)
                        (demand.Target, demand.Done) = (target, false);
                    break;
                }

                if (!known)
                {
                    _demands.Add(new Demand
                    {
                        Source = source, Target = target, KeepFrom = keepFrom, Kind = demandKind, Urgency = urgency, Stretch = stretch,
                        HintKind = kind, HintMedia = seconds, At = at, Kept = true, Asked = Stopwatch.GetTimestamp(),
                    });
                }
            }

            foreach (var demand in _demands)
            {
                if (IsHint(demand.Kind) && !demand.Kept)
                    demand.Cancelled = true;
            }

            _demands.RemoveAll(d => d.Cancelled && d.Lane < 0);
            _cache.SetHeld(_held);
            UpdateInterests();
        }

        _work.Set();
    }

    // ----- Playback pacing -----
    // A full-speed chase starves the renderer of the device (decoder calls and frame copies share the device lock with every draw): the
    // bench measured 20-50 ms per drawn frame while a cut chase ran beside playback. So during playback, look-ahead work (cut first
    // frames) decodes for a time slice after each drawn frame and then waits for the next, unless the cut is so near it needs full speed.

    private readonly object _drawnGate = new();
    private long _drawnCount;
    private long _lastDrawn = Stopwatch.GetTimestamp();
    private double _drawInterval = 1 / 30.0;

    /// <summary>Assumed full-speed chase rate, fps (1440p HEVC on a current GPU: ~480).</summary>
    private const double FullSpeedFps = 400;

    /// <summary>Records a drawn frame (EndFrame) and releases paced lanes.</summary>
    private void NoteDrawn()
    {
        var now = Stopwatch.GetTimestamp();
        var interval = (now - Volatile.Read(ref _lastDrawn)) / (double)Stopwatch.Frequency;
        if (interval is > 0.002 and < 0.1)
            Volatile.Write(ref _drawInterval, 0.8 * Volatile.Read(ref _drawInterval) + 0.2 * interval);
        Volatile.Write(ref _lastDrawn, now);
        lock (_drawnGate)
        {
            _drawnCount++;
            Monitor.PulseAll(_drawnGate);
        }
    }

    /// <summary>
    /// True when a lane should wait for the next drawn frame before continuing (lane thread): during playback, for look-ahead work (not
    /// visible/play-ahead or export work) whose slice since the last draw is used up, unless it is a cut near enough to need full speed.
    /// </summary>
    private bool Paced(Demand demand, MfVideoDecoder decoder)
    {
        if (!_lastWasPlaying || _exactOnly || demand.Kind is DemandKind.Visible or DemandKind.PlayAhead)
            return false;
        var since = (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastDrawn)) / (double)Stopwatch.Frequency;
        if (since < Math.Clamp(Volatile.Read(ref _drawInterval) * 0.4, 0.004, 0.02))
            return false;
        if (demand.HintKind == GpuSeekHintKind.CutEntry)
        {
            var read = decoder.ReadTime;
            var from = double.IsNaN(read) ? decoder.KeyAtOrBefore(demand.KeepFrom) : read;
            if (double.IsNaN(from))
                from = demand.KeepFrom - 4;
            var frames = Math.Max(demand.Target - from, 0) / Math.Max(decoder.FrameDuration, 1e-3);
            if (demand.At - _lastPlayhead < frames / FullSpeedFps + 0.5)
                return false;
        }

        return true;
    }

    /// <summary>Waits for the next drawn frame, at most 40 ms (lane thread).</summary>
    private void WaitForNextDraw()
    {
        lock (_drawnGate)
        {
            var seen = _drawnCount;
            var clock = Stopwatch.StartNew();
            while (_drawnCount == seen && clock.ElapsedMilliseconds < 40 && !_stopping.WaitOne(0))
                Monitor.Wait(_drawnGate, 40 - (int)clock.ElapsedMilliseconds);
        }
    }

    // Renderer priority: unpaced chases near the playhead run at full speed, and a preview frame drawn meanwhile queued for the device
    // lock behind decoder calls and copies (13-21 ms per frame instead of 0.3 ms on the first plays of a quick-cut project, showing frames
    // late). So while a preview frame is drawn (output acquire to flush), lanes start no new decoder call (open, seek, frame); only a call
    // already in progress overlaps. Exports and readbacks draw exact frames with lanes reading ahead in parallel, never held.
    private volatile bool _drawing;

    /// <summary>Marks the start (true) or end (false) of a preview frame draw; lanes hold back meanwhile.</summary>
    public void Drawing(bool drawing)
    {
        lock (_drawnGate)
        {
            _drawing = drawing;
            if (!drawing)
                Monitor.PulseAll(_drawnGate);
        }
    }

    /// <summary>Waits while a preview frame is being drawn, at most 20 ms (lane thread, before each decoder call).</summary>
    private void WaitWhileDrawing()
    {
        if (!_drawing)
            return;
        lock (_drawnGate)
        {
            var clock = Stopwatch.StartNew();
            while (_drawing && clock.ElapsedMilliseconds < 20 && !_stopping.WaitOne(0))
                Monitor.Wait(_drawnGate, Math.Max(20 - (int)clock.ElapsedMilliseconds, 1));
        }
    }

    // Cut ranges held in the cache (from hints): source serial, from, to.
    private readonly List<(int Source, double From, double To)> _held = [];

    /// <summary>Unwanted time after which a source not drawn in the last frame has its idle decoders closed, seconds.</summary>
    private const double IdleDecoderSeconds = 15;

    private long _nextTidy;

    /// <summary>
    /// Closes idle decoders of long-unwanted sources (a file opened for a drop that never came, a deleted clip), freeing VRAM. Called every
    /// worker turn; checks every 2 s. Sources in the last drawn frame keep their decoders however long the player rests, as do sources
    /// still named by a hint (a cut ahead during a long pause keeps its decoder parked at the cut's first frames).
    /// </summary>
    public void Tidy()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextTidy)
            return;
        _nextTidy = now + 2 * Stopwatch.Frequency;
        LookAtPower();
        lock (_gate)
        {
            foreach (var source in _sources.Values)
            {
                if (source.Decoders.Count == 0 || now - source.LastWanted < IdleDecoderSeconds * Stopwatch.Frequency || Drawn(source))
                    continue;

                // Satisfied hint demands persist with their hint (TakeHints); unchanged hints are not re-read during a pause, so they
                // do not refresh LastWanted.
                var waiting = false;
                foreach (var demand in _demands)
                    waiting |= demand.Source == source && !demand.Cancelled && (!demand.Done || IsHint(demand.Kind));
                if (!waiting)
                    BuryIdle(source);
            }
        }

        DisposeBuried();
    }

    // True when a source was drawn in the last frame (under the lock).
    private bool Drawn(SourceState source)
    {
        foreach (var memory in _layers.Values)
        {
            if (memory.Source == source.Serial && memory.LastFrame == _frame)
                return true;
        }

        return false;
    }

    // Cache budget share all held cut ranges may use together.
    private const double HeldShare = 0.3;

    private static bool IsHint(DemandKind kind) => kind is DemandKind.Hinted or DemandKind.Hover or DemandKind.Idle;

    // ----- Active scrub (paused playhead moving fast: key frames first, no speculative reads) -----

    /// <summary>Paused moves closer together than this, ms, count as one active scrub (slower single steps never do).</summary>
    internal const int ScrubRapidMs = 120;

    /// <summary>Consecutive rapid paused moves that make an active scrub.</summary>
    internal const int ScrubRapidMoves = 2;

    /// <summary>Dwell past the last move, ms, after which a scrub has settled and the landing key frame's chase to the exact frame resumes.</summary>
    internal const int ScrubDwellMs = 70;

    /// <summary>Quiet time past the last paused playhead move, ms, before pre-roll ranges are read.</summary>
    internal const int PreRollQuietMs = 250;

    /// <summary>Landing key frame dwell, ms, while the editor holds a playhead drag (FrameSceneHeader.FlagScrubbing).</summary>
    internal const int HeldScrubDwellMs = 140;

    /// <summary>
    /// Landing key frame dwell, ms, while a held drag ghost-pauses playback (FrameSceneHeader.FlagGhostScrub): well under
    /// GpuPreviewEngine.GhostDwell (80 ms) so the exact-frame chase starts before the play-scrub resumes.
    /// </summary>
    internal const int GhostScrubDwellMs = 35;

    // Last paused playhead move (Stopwatch ticks, 0 = none) and the run of rapid moves ending there: written by BeginFrame, read by lanes.
    private long _lastMoveAt;
    private int _scrubMoves;

    // FlagScrubbing on the last scene (paused, not exact-only), and true once a held drag was released until the next one: written by
    // BeginFrame, read by lanes.
    private bool _scrubHeld, _scrubReleased;

    // The held drag ghost-pauses a playing transport (FlagGhostScrub): written by BeginFrame, read by lanes.
    private bool _scrubGhost;

    /// <summary>Rapid-move run after a paused move at <paramref name="now"/>: one more when within <see cref="ScrubRapidMs"/> of the last, else 1. Pure.</summary>
    internal static int NextScrubMoves(int moves, long now, long lastMoveAt, long frequency) =>
        lastMoveAt != 0 && (now - lastMoveAt) * 1000 < ScrubRapidMs * frequency ? Math.Min(moves + 1, 1_000) : 1;

    /// <summary>
    /// True while a paused scrub is active: <see cref="ScrubRapidMoves"/> rapid moves, the last within <see cref="ScrubDwellMs"/>. Never while
    /// <paramref name="playing"/> or in exact-frame scenes (<paramref name="exactOnly"/>: export, filmstrips, captures). Pure; checked by goldens.
    /// </summary>
    /// <remarks>
    /// With the editor's explicit drag flag: <paramref name="held"/> scrubs from the first move until <see cref="HeldScrubDwellMs"/> past
    /// the last one; <paramref name="released"/> (the drag ended, not held again) never scrubs, so the landing key frame chases at once.
    /// A held drag that ghost-pauses playback (<paramref name="ghost"/>) dwells only <see cref="GhostScrubDwellMs"/>.
    /// </remarks>
    internal static bool ScrubbingAt(bool exactOnly, bool playing, long now, long lastMoveAt, int moves, long frequency, bool held = false, bool released = false,
        bool ghost = false) =>
        !exactOnly && !playing && lastMoveAt != 0 && (held || !released) &&
        (held ? (now - lastMoveAt) * 1000 < HeldDwellMs(ghost) * frequency
              : moves >= ScrubRapidMoves && (now - lastMoveAt) * 1000 < ScrubDwellMs * frequency);

    /// <summary>Landing key frame dwell of a held drag, ms: <see cref="GhostScrubDwellMs"/> when it ghost-pauses playback, else <see cref="HeldScrubDwellMs"/>. Pure.</summary>
    internal static int HeldDwellMs(bool ghost) => ghost ? GhostScrubDwellMs : HeldScrubDwellMs;

    /// <summary>
    /// True when a visible frame's lane, having just read its seek-landing key frame (<paramref name="landed"/>, before the target), waits
    /// instead of decoding on through the GOP: an active scrub shows key frames and chases the exact frame once the pointer dwells. Pure.
    /// </summary>
    internal static bool ChaseWaits(bool exactOnly, bool playing, bool visible, bool landed, long now, long lastMoveAt, int moves, long frequency,
        bool held = false, bool released = false, bool ghost = false) =>
        visible && landed && ScrubbingAt(exactOnly, playing, now, lastMoveAt, moves, frequency, held, released, ghost);

    /// <summary>True when pre-roll ranges may be read: <see cref="PreRollQuietMs"/> past the last paused playhead move (or none yet). Pure.</summary>
    /// <remarks>Never while the editor holds a playhead drag (<paramref name="held"/>, FlagScrubbing).</remarks>
    internal static bool PreRollMayRun(long now, long lastMoveAt, long frequency, bool held = false) =>
        !held && (lastMoveAt == 0 || (now - lastMoveAt) * 1000 >= PreRollQuietMs * frequency);

    /// <summary>
    /// True when a scrub-ahead hint may take a lane: never while a visible frame is being decoded, and during an active scrub only when no
    /// other scrub-ahead read is under way (one hardware decode engine and device lock serve every lane). Pure.
    /// </summary>
    /// <remarks>Never while the editor holds a playhead drag (<paramref name="held"/>, FlagScrubbing): no extra lane contends with it.</remarks>
    internal static bool ScrubAheadMayRun(bool visibleDecoding, bool scrubbing, int scrubAheadBusy, bool held = false) =>
        !held && !visibleDecoding && (!scrubbing || scrubAheadBusy == 0);

    // ScrubbingAt for the pool now (any thread).
    private bool ScrubbingNow() =>
        ScrubbingAt(_exactOnly, _lastWasPlaying, Stopwatch.GetTimestamp(), Volatile.Read(ref _lastMoveAt), Volatile.Read(ref _scrubMoves), Stopwatch.Frequency,
            Volatile.Read(ref _scrubHeld), Volatile.Read(ref _scrubReleased), Volatile.Read(ref _scrubGhost));

    // ----- Idle work (speculative work never competes with the active tab) -----

    /// <summary>Quiet time after the last playhead move (seek, scrub step, played frame) before idle work may resume, ms.</summary>
    internal const int IdleQuietMs = 400;

    // Time (Stopwatch ticks) until which the playhead counts as moving: set by BeginFrame, read by lanes.
    private long _activeUntil;

    /// <summary>
    /// True when idle work (tab pre-roll, dropped file: <see cref="DemandKind.Idle"/>) may take, open or continue a decoder: not
    /// <paramref name="playing"/>, playhead quiet for <see cref="IdleQuietMs"/> (<paramref name="now"/> past <paramref name="activeUntil"/>),
    /// no visible or play-ahead frame pending (<paramref name="activePending"/>), and fewer idle reads (<paramref name="idleBusy"/>) than one
    /// lane on unified memory (two elsewhere). Pure; checked by goldens.
    /// </summary>
    internal static bool IdleMayRun(bool playing, long now, long activeUntil, bool activePending, int idleBusy, bool unified) =>
        !playing && now >= activeUntil && !activePending && idleBusy < (unified ? 1 : 2);

    /// <summary><see cref="IdleMayRun"/> for the current pool state (under the lock); <paramref name="countLanes"/> false for a lane already on idle work.</summary>
    private bool IdleMayRunNow(bool countLanes)
    {
        var (pending, busy) = (false, 0);
        foreach (var demand in _demands)
        {
            if (demand.Cancelled || demand.Done)
                continue;
            pending |= demand.Kind is DemandKind.Visible or DemandKind.PlayAhead;
            busy += demand.Kind == DemandKind.Idle && demand.Lane >= 0 ? 1 : 0;
        }

        return IdleMayRun(_lastWasPlaying, Stopwatch.GetTimestamp(), Volatile.Read(ref _activeUntil), pending, countLanes ? busy : 0, _gpu.UnifiedMemory);
    }

    // ----- Cold play (playhead frames first after a cold start) -----

    /// <summary>
    /// Cold-play window, seconds: after a tab switch or new sources (SyncSources), or a seek whose first picture missed a frame, the first
    /// second of playback decodes only the playhead range and cuts within <see cref="CutSoon"/>; farther cuts stay as demands (no memory used)
    /// and are read afterwards, earliest first. Applies on every memory tier.
    /// </summary>
    internal const double ColdPlaySeconds = 1.0;

    // Armed by new sources or a cold seek, plus the last frame's jump flag (worker thread only: SyncSources, BeginFrame).
    private bool _coldArmed, _jumpProbe;

    // Time (Stopwatch ticks, 0 = none) until which the cold-play window is open: written by BeginFrame, read by lanes.
    private long _coldPlayUntil;

    /// <summary>
    /// True when a cut at sequence time <paramref name="cutAt"/> is deferred: <paramref name="playing"/>, <paramref name="now"/> inside the
    /// window (before <paramref name="coldUntil"/>), and more than <see cref="CutSoon"/> of real time ahead of <paramref name="playhead"/> at
    /// <paramref name="rate"/> (CutSoon x rate in sequence seconds, never below CutSoon: 2x keeps 1.5 s real lead; slow play holds no more
    /// than 1x). NaN playhead or rate defers nothing (never stall on unknown values). Pure; checked by goldens.
    /// </summary>
    internal static bool ColdPlayHoldsBack(bool playing, long now, long coldUntil, double cutAt, double playhead, double rate) =>
        playing && now < coldUntil && cutAt - playhead > CutSoon * Math.Max(rate, 1);

    // True when a cut-entry demand is currently deferred by cold play (lane thread, under _gate). Never in exact-frame scenes.
    private bool HeldForColdPlay(Demand demand)
    {
        if (demand.HintKind != GpuSeekHintKind.CutEntry || _exactOnly)
            return false;
        var until = Volatile.Read(ref _coldPlayUntil);
        return until != 0 && ColdPlayHoldsBack(_lastWasPlaying, Stopwatch.GetTimestamp(), until, demand.At, _lastPlayhead, Volatile.Read(ref _playRate));
    }

    /// <summary>Goldens: cold-play window end (Stopwatch ticks, 0 = none) and whether the next play is armed.</summary>
    internal (long Until, bool Armed) ColdPlayForTests => (Volatile.Read(ref _coldPlayUntil), _coldArmed);

    // ----- Play rate (keeps the cold-play lead in real time) -----

    /// <summary>Play-rate sample length, seconds: a speed change during playback is picked up within one sample.</summary>
    internal const double PlayRateSampleSeconds = 0.1;

    // Play rate: playhead advance over wall-clock time across consecutive playing frames (the scene carries no speed). 1 until the first
    // sample of a play, and again after pause, jump or new sources. Written by BeginFrame, read by lanes (HeldForColdPlay).
    private double _playRate = 1;

    // Current sample start (worker thread only: Stopwatch ticks, 0 = none, and playhead then), and the restart flag set by SyncSources
    // (called from BeginFrame, TakeHints and TrimSources).
    private long _rateFromTicks;
    private double _rateFromPlayhead;
    private bool _rateRestart;

    /// <summary>
    /// Play rate from playhead movement over <paramref name="seconds"/> of wall time, clamped to 0.25-4 (loop wraps and stalls stay in
    /// range); 1 when non-finite or no time elapsed. Pure; checked by goldens.
    /// </summary>
    internal static double PlayRateOf(double playheadMoved, double seconds)
    {
        var rate = playheadMoved / seconds;
        return seconds > 0 && double.IsFinite(rate) ? Math.Max(0.25, Math.Min(rate, 4)) : 1;
    }

    /// <summary>
    /// Per-frame play-rate sampling (worker thread via BeginFrame; never for exact-frame scenes; goldens pass their own clock). A sample
    /// completes after <see cref="PlayRateSampleSeconds"/> since it began (<paramref name="now"/>, Stopwatch ticks): rate = playhead
    /// movement over that interval, and the next sample starts there.
    /// </summary>
    internal void SamplePlayRate(bool playing, bool jumped, double playhead, long now)
    {
        if (!playing || jumped || _rateRestart || double.IsNaN(playhead))
        {
            _rateRestart = false;
            Volatile.Write(ref _playRate, 1.0);
            (_rateFromTicks, _rateFromPlayhead) = (playing && !double.IsNaN(playhead) ? now : 0, playhead);
            return;
        }

        if (_rateFromTicks == 0)
        {
            (_rateFromTicks, _rateFromPlayhead) = (now, playhead);
            return;
        }

        var seconds = (now - _rateFromTicks) / (double)Stopwatch.Frequency;
        if (seconds < PlayRateSampleSeconds)
            return;
        Volatile.Write(ref _playRate, PlayRateOf(playhead - _rateFromPlayhead, seconds));
        (_rateFromTicks, _rateFromPlayhead) = (now, playhead);
    }

    /// <summary>Goldens: current play rate used for the cold-play lead (1 until the first sample).</summary>
    internal double PlayRateForTests => Volatile.Read(ref _playRate);

    // ----- Demands -----

    private void Ask(SourceState source, double seconds, DemandKind kind, long clipKey, double keepFrom)
    {
        // Skip a range that just failed to decode for a while (no retry loop).
        if (Math.Abs(seconds - source.FailedAt) < 1.0 && Stopwatch.GetTimestamp() < source.FailedUntil)
            return;

        lock (_gate)
        {
            if (IsMissing(source.Serial, seconds))
                return;

            // One demand per source frame regardless of requesting layer (a video, its Cutouts and blur fill twin read one time of one
            // file): reuse an existing demand for the same frame.
            if (kind is DemandKind.Visible or DemandKind.PlayAhead)
            {
                var sameFrame = source.FrameDuration > 0 ? source.FrameDuration * 0.25 : 1e-4;
                foreach (var demand in _demands)
                {
                    if (demand.Kind == kind && demand.Source == source && !demand.Cancelled && Math.Abs(demand.Target - seconds) <= sameFrame)
                    {
                        demand.Stale = false;
                        demand.KeepFrom = Math.Min(demand.KeepFrom, keepFrom);
                        Revive(demand);
                        return;
                    }
                }
            }

            // Click where the pointer rested (or playhead at a cut still decoding): convert the in-progress hint into this layer's
            // demand (no second chase).
            if (kind == DemandKind.Visible)
            {
                foreach (var demand in _demands)
                {
                    if (IsHint(demand.Kind) && demand.Lane >= 0 && demand.Source == source && !demand.Cancelled &&
                        Math.Abs((demand.Stretch ? demand.KeepFrom : demand.Target) - seconds) < 0.25)
                    {
                        (demand.Kind, demand.Urgency, demand.ClipKey, demand.Target, demand.KeepFrom) = (kind, 0, clipKey, seconds, keepFrom);
                        (demand.Stretch, demand.HintKind, demand.HintMedia, demand.At) = (false, GpuSeekHintKind.None, double.NaN, 0);
                        demand.Asked = Stopwatch.GetTimestamp();
                        return;
                    }
                }
            }

            // One demand per layer and kind: scrubbing retargets it instead of adding another (its lane follows).
            foreach (var demand in _demands)
            {
                if (demand.Kind == kind && demand.ClipKey == clipKey && demand.Source == source && !demand.Cancelled)
                {
                    if (Math.Abs(demand.Target - seconds) > 1e-6)
                    {
                        demand.Target = seconds;
                        demand.KeepFrom = keepFrom;
                        demand.Done = false;
                        demand.Asked = Stopwatch.GetTimestamp();
                    }
                    else
                    {
                        Revive(demand);
                    }

                    demand.Stale = false;
                    return;
                }
            }

            _demands.Add(new Demand { Source = source, Target = seconds, KeepFrom = keepFrom, Kind = kind, ClipKey = clipKey, Asked = Stopwatch.GetTimestamp() });

            // Register as a cache interest immediately: under a small budget (filmstrips) its frames must not be evicted before drawing.
            if (kind == DemandKind.Visible)
                UpdateInterests();
        }

        _work.Set();
    }

    /// <summary>
    /// Re-queues a recently satisfied demand whose frame is requested again but was evicted before drawing, so it never stays done while an
    /// exact-frame wait needs it (under the lock).
    /// </summary>
    private void Revive(Demand demand)
    {
        if (!demand.Done || demand.Kind == DemandKind.PreRoll || _cache.Has(demand.Source.Serial, demand.Target))
            return;
        demand.Done = false;
        demand.Asked = Stopwatch.GetTimestamp();
    }

    /// <summary>Completes a visible demand satisfied by a cached frame (recording latency when a lane produced it).</summary>
    private void Satisfied(SourceState source, double seconds)
    {
        lock (_gate)
        {
            foreach (var demand in _demands)
            {
                if (demand.Kind == DemandKind.Visible && demand.Source == source && Math.Abs(demand.Target - seconds) < 1e-6)
                {
                    demand.Stale = false;
                    demand.Done = true;
                }
            }
        }
    }

    private void UpdateInterests()
    {
        Span<(int, double, double)> interests = stackalloc (int, double, double)[64];
        var count = 0;
        foreach (var memory in _layers.Values)
        {
            if (count < interests.Length && _frame - memory.LastFrame < 4)
                interests[count++] = (memory.Source, memory.Media, 1.0);
        }

        foreach (var demand in _demands)
        {
            if (count >= interests.Length)
                break;
            var weight = demand.Kind switch { DemandKind.Visible => 1.0, DemandKind.PlayAhead => 1.0, DemandKind.Hinted => 0.6, DemandKind.Hover => 0.3, DemandKind.Idle => 0.2, _ => 0.4 };
            interests[count++] = (demand.Source.Serial, demand.Target, weight);
        }

        _cache.SetInterests(interests[..count]);
    }

    private SourceState? SourceBySerial(int serial)
    {
        foreach (var source in _sources.Values)
        {
            if (source.Serial == serial)
                return source;
        }

        return null;
    }

    public void Dispose()
    {
        _stopping.Set();
        _work.Set();
        var joined = ThreadStop.JoinAll(_lanes, 3000, "Decode lanes");
        DisposeBuried();
        lock (_gate)
        {
            // A lane that did not stop (stuck in a driver) keeps its decoder and the cache; the process exits around them.
            foreach (var source in _sources.Values)
            {
                foreach (var decoder in source.Decoders)
                {
                    if (joined || decoder.Owner < 0)
                        decoder.Dispose();
                }

                source.Decoders.Clear();
            }

            _sources.Clear();
            _demands.Clear();
        }

        if (!joined)
            return;
        ClosePaceTimers();
        _cache.Dispose();
        _work.Dispose();
        _arrived.Dispose();
        _stampBack.Dispose();
        _stopping.Dispose();
    }

    private enum DemandKind
    {
        Visible = 0,
        PlayAhead = 1,
        Hinted = 2,
        PreRoll = 3,

        /// <summary>Timeline pointer hover: below everything else, including the playhead's pre-roll.</summary>
        Hover = 4,

        /// <summary>
        /// Background work (GpuSeekHintKind.TabRoll: a background tab's pre-roll, a file opened ahead of a drop): lowest priority, only while
        /// the player is quiet (DecoderPool.IdleMayRun).
        /// </summary>
        Idle = 5,
    }

    /// <summary>A requested frame (or a range when <see cref="Stretch"/>: <see cref="KeepFrom"/> to <see cref="Target"/>).</summary>
    private sealed class Demand
    {
        public required SourceState Source;
        public double Target;
        public double KeepFrom;
        public DemandKind Kind;

        /// <summary>
        /// Urgency within the kind: Hinted 0 = scrub direction, 1 = upcoming cut; Hover 0 = pointer frame, 1 = its play-ahead. Within an
        /// urgency, the soonest needed (<see cref="At"/>) goes first.
        /// </summary>
        public int Urgency;

        /// <summary>True when every frame from <see cref="KeepFrom"/> to <see cref="Target"/> is wanted (pre-roll, cut first frames, pointer play-ahead).</summary>
        public bool Stretch;

        /// <summary>Source hint kind (None for renderer demands), its media time, and the sequence time it is needed at.</summary>
        public GpuSeekHintKind HintKind;
        public double HintMedia = double.NaN;
        public double At;

        /// <summary>True when its hint is in the last list read (TakeHints).</summary>
        public bool Kept;
        public long ClipKey;
        public long Asked;
        public int Lane = -1;

        /// <summary>Decoder used by the owning lane (null before opening or when unowned); under the lock.</summary>
        public MfVideoDecoder? Decoder;
        public bool Stale;
        public bool Done;
        public bool Cancelled;

        /// <summary>Pick counter in which no decoder could serve it (skipped for the rest of that pick, see Pick).</summary>
        public long PassedIn;

        public int Rank => (int)Kind * 8 + Urgency;
    }

    /// <summary>Per-layer (clip key) state: last shown frame (fallback while the next decodes) and data for its media speed.</summary>
    private sealed class LayerMemory
    {
        public int Source;
        public double Media = double.NaN;
        public double Playhead = double.NaN;
        public CachedFrame? Shown;
        public long LastFrame;

        /// <summary>The shown frame while pinned across frames (player only, HoldShown): never evicted while the layer stays drawn.</summary>
        public CachedFrame? Held;

        /// <summary>True when last drawn during playback (lean parking then also keeps the following frame).</summary>
        public bool Playing;
    }

    private sealed class SourceState
    {
        public int Serial;
        public int Generation;
        public string Path = "";
        public int StreamIndex;
        public long PathHash;
        public double[]? IFrames;

        /// <summary>
        /// File size and write time at registration when parking is possible (default otherwise, and until read on the pool): the parking key.
        /// </summary>
        public (long Length, long Written) Stamp;

        /// <summary>True while its stamp is being read on the pool (under the gate).</summary>
        public bool Stamping;

        /// <summary>Parked serial to reclaim if the stamp shows the file unchanged (while waiting in DecoderPool._deciding).</summary>
        public ParkedSource? Candidate;

        /// <summary>Registration time (Stopwatch ticks), for the stamp grace period.</summary>
        public long Registered;
        public double Duration;
        public volatile bool Failed;
        public volatile bool Retired;
        public int Opening;
        public double FailedAt = double.NaN;
        public long FailedUntil;

        /// <summary>Frame duration from its first decoder (0 before one opens): defines "the same frame" for its demands.</summary>
        public double FrameDuration;

        /// <summary>Cache bytes per frame (0 before a decoder opens).</summary>
        public long FrameBytes;

        /// <summary>True when the frame duration is reliable (learned or stream-reported); new decoders are seeded with it.</summary>
        public bool FrameDurationKnown;

        /// <summary>No new decoders before this time (after a failed open while others decode it).</summary>
        public long NoOpensUntil;

        /// <summary>Last time a layer or hint wanted a frame (Stopwatch ticks); Tidy closes idle decoders of long-unwanted sources.</summary>
        public long LastWanted = Stopwatch.GetTimestamp();
        public readonly List<MfVideoDecoder> Decoders = [];
    }
}
