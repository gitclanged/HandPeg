using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// The media side of the compositor (Pass 5.0-Core, Instant Seek since 5.0-AudioAnd3D): the source table, the warm
/// decoders, the frame cache, and the decode lanes that fill it.
/// <para><b>Instant Seek.</b> The renderer never waits for a decoder. For each media layer it asks <see cref="Resolve"/>
/// for the frame at the layer's moment: the cache has it (exact), or it gets the nearest frame there is to draw in the
/// meantime and a demand for the exact one goes to the lanes; when that frame lands, <see cref="FramesArrived"/> wakes the
/// worker, which draws the scene again. Demands, most urgent first:</para>
/// <list type="number">
/// <item>Visible: the frames the newest scene shows and does not have yet (a scrub moves them every frame).</item>
/// <item>Play-ahead: while playing, a third of a second past what is shown, so playback never waits.</item>
/// <item>Hinted: what the client expects next: where a scrub is heading, then the first frames of every cut within the
/// lookahead horizon, earliest first (5.0-Release), held in the cache until the hint goes.</item>
/// <item>Pre-roll: while paused, the stretch round the playhead, so frame steps and short scrubs never decode.</item>
/// <item>Hover: the pointer over the timeline (its frame, or once it rests a short play-ahead from it), last of all.</item>
/// </list>
/// <para>Each lane (a thread, two by default: most cards decode two streams at once at full speed) takes the most urgent
/// demand no other lane works on, picks the decoder of that source that reaches it in the fewest frames (opening one more,
/// up to four a source, rather than dragging the one parked at the playhead away for a hint), and decodes frame by frame,
/// every frame kept, until the frame is there; between frames it gives way to anything more urgent. A source whose file
/// will not open is not tried again until the client changes it.</para>
/// </summary>
internal sealed class DecoderPool : IDisposable
{
    public const int Capacity = 12;
    private const int MostPerSource = 4;

    /// <summary>
    /// How many decoders of one source the cuts ahead may have (5.0-Release): one reading what is shown, one parked at each
    /// cut within the horizon that reads it (a quick-cut sequence of one recording has three or four in 3 s).
    /// </summary>
    private const int MostPerSourceForCuts = 6;

    /// <summary>How far before a frame asked for the frames decoded on the way are kept (a scrub back, a frame step back), seconds.</summary>
    private const double KeepBehind = 1.5;

    /// <summary>How far ahead of what is shown frames are decoded while playing (in the file's time, at its own speed), seconds.</summary>
    private const double PlayAhead = 0.35;

    /// <summary>The stretch decoded round the playhead while paused: before and after it, seconds.</summary>
    private const double PreRollBefore = 1.0, PreRollAfter = 2.0;

    /// <summary>
    /// For exact-frame scenes (exports, filmstrips, tests): the frames kept before a frame asked for, and how far ahead the
    /// lane reads while the frame is drawn and read back. Short: an export never steps back, and a third of a second of a
    /// 420 fps 1440p recording would not fit its cache (frames it is about to need would be let go and decoded again).
    /// </summary>
    private const double ExactKeepBehind = 0.05, ExactPlayAhead = 0.1;

    /// <summary>How far a container's time stamps may be from the moments of its frames, seconds (Matroska's millisecond clock).</summary>
    private const double StampHair = 0.002;

    /// <summary>The frames of a cut's clip decoded ahead of the playhead (the user's directive: the first 6; a shorter clip whole).</summary>
    private const int CutEntryFrames = 6;

    /// <summary>The longest play-ahead from the pointer resting on the timeline, seconds of the file.</summary>
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
        var lanes = requested > 0 ? Math.Min(requested, 4) : gpu.IsWarp ? 1 : 2;
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

    /// <summary>What went wrong last (a file that will not open): shown in the control block's message.</summary>
    public string LastError { get; private set; } = "";

    public int Opens => Volatile.Read(ref _opens);

    private int _seeks, _decoded;

    /// <summary>Seeks made and frames decoded since the worker started (counted by the lanes as they go: no lock).</summary>
    public int Seeks => Volatile.Read(ref _seeks);

    public int FramesDecoded => Volatile.Read(ref _decoded);

    /// <summary>Set by a lane when an exact frame the scene waits for (or any frame, for a layer with nothing yet) is decoded.</summary>
    public WaitHandle FramesArrived => _arrived;

    /// <summary>Every media layer of the frame drawn last had its exact frame.</summary>
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

    /// <summary>A lane stuck in one call for longer than <paramref name="seconds"/> (a hung driver): the worker stops beating, and is taken over.</summary>
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

    /// <summary>A lane's fault the worker must end on (a lost device), or null.</summary>
    public Exception? LaneFault => _laneFault;

    // ----- One frame of the renderer -----

    public void BeginFrame(long frame, bool playing, double playhead, bool exactOnly = false)
    {
        _frame = frame;
        _exactOnly = exactOnly;
        _resolvedNow.Clear();
        SyncSources();

        // A jump of the playhead (a seek, a click) or a start or stop of playback makes the frames of the old moment stale.
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

    /// <summary>
    /// The frame to draw for a media layer: the exact one when it is decoded, else the nearest there is (and a demand for
    /// the exact one), else null (nothing of this source decoded yet: the layer is left out of this frame). Pinned until
    /// <see cref="EndFrame"/>.
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
                // An exact-frame scene with a media layer it cannot draw is not exact: an export fails on it (and FFmpeg
                // makes it) rather than leaving the layer out quietly.
                if (_exactOnly && source is { Failed: true })
                    LastFrameExact = false;
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

        // Layers of one source at one moment (the main video, its HUD pieces, its blurred background) share one frame,
        // and its demands: only the first of them in a frame asks (5.0-Verify, the user's bug 5).
        var repeat = false;
        foreach (var (serial, at) in _resolvedNow)
            repeat |= serial == source.Serial && Math.Abs(at - seconds) <= 1e-6;
        if (!repeat && _resolvedNow.Count < 64)
            _resolvedNow.Add((source.Serial, seconds));

        var exact = _cache.Exact(source.Serial, seconds);
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

        // While playing, decode a little past it (at the layer's own speed through its file).
        if (_lastWasPlaying && !repeat)
        {
            // Its speed through its file since its last frame; a jump (a cut, a loop) is no speed: as played.
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
            memory.Source = source.Serial;
        }

        if (exact is not null)
            _pinned.Add(exact);
        return exact;
    }

    /// <summary>
    /// Reads the source table again when the client changed it. Called at the start of every frame and before an
    /// exact-frame wait (the first scene after a SetSource must already know the new source, or it would wait for nothing).
    /// </summary>
    public void SyncSources()
    {
        var version = Volatile.Read(ref _shared.Control.SourcesVersion);
        if (version != _sourcesVersion)
        {
            _sourcesVersion = version;
            Resync();
            DisposeBuried();
        }
    }

    // The frame drawn is an exact-frame one (an export, a filmstrip, a test): no pre-roll is asked for round it.
    private bool _exactOnly;

    // The (source, moment) pairs resolved in this frame: a layer at a pair already there asks for nothing more.
    private readonly List<(int Serial, double Seconds)> _resolvedNow = new(16);

    // Moments no frame covers (a hole in the stream), each for a few seconds: not decoded for again, and not waited for.
    private readonly List<(int Serial, double Seconds, long Until)> _missing = [];

    private int _stretches, _lateLandings, _missingFrames;

    /// <summary>Frames stretched back over a dropped frame, seeks made again from further back, moments with no frame (diagnostics).</summary>
    public int Stretches => Volatile.Read(ref _stretches);

    public int LateLandings => Volatile.Read(ref _lateLandings);

    public int MissingFrames => Volatile.Read(ref _missingFrames);

    /// <summary>Whether a moment of a source is known to have no frame (under the lock).</summary>
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
    /// Asks for a media layer's exact frame without drawing (a scene that must wait for its exact frames: exports,
    /// filmstrips, tests). True when it is there already.
    /// </summary>
    public bool Request(int id, double seconds, long pathHash, long clipKey)
    {
        if (id is < 0 or >= CompositorProtocol.MaxSources)
            return true;
        SyncSources();
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

        // Only exact-frame scenes ask this way: little is kept behind the frame (see ExactKeepBehind).
        Ask(source, seconds, DemandKind.Visible, clipKey, keepFrom: seconds - ExactKeepBehind);
        return false;
    }

    /// <summary>The frame is drawn: what it pinned is let go, stale demands go, and (paused, nothing missing) the pre-roll is asked for.</summary>
    public void EndFrame()
    {
        foreach (var frame in _pinned)
            _cache.Unpin(frame);
        _pinned.Clear();
        _cache.CollectOrphans();
        NoteDrawn();

        lock (_gate)
        {
            // Layers not seen for a while are forgotten.
            if (_layers.Count > 256)
            {
                foreach (var key in _layers.Where(p => _frame - p.Value.LastFrame > 120).Select(p => p.Key).ToList())
                    _layers.Remove(key);
            }

            _demands.RemoveAll(d => d.Stale && d.Lane < 0 || d.Done && d.Kind is not (DemandKind.PreRoll or DemandKind.Hinted or DemandKind.Hover));
            foreach (var demand in _demands)
            {
                if (demand.Stale)
                    demand.Cancelled = true;
            }

            _demands.RemoveAll(d => d.Cancelled && d.Lane < 0);

            // Paused with every frame there: the neighbourhood of each layer's moment, decoded in idle time (the player's
            // frames only: an export or a filmstrip asks for the frames it wants).
            if (!_lastWasPlaying && !_exactOnly)
            {
                // The stretches of clips no longer drawn go (they would crowd the cache's interests).
                _demands.RemoveAll(d => d.Kind == DemandKind.PreRoll && d.Lane < 0 && (!_layers.TryGetValue(d.ClipKey, out var seen) || seen.LastFrame != _frame));

                foreach (var (clipKey, memory) in _layers)
                {
                    if (memory.LastFrame != _frame)
                        continue;
                    var source = SourceBySerial(memory.Source);
                    if (source is null)
                        continue;
                    // Held to what the cache can keep of this source (a third of the stretch before, two after).
                    var stretch = Fit(source, PreRollBefore + PreRollAfter, PreRollShare);
                    var (before, after) = (stretch * PreRollBefore / (PreRollBefore + PreRollAfter), stretch * PreRollAfter / (PreRollBefore + PreRollAfter));
                    var from = Math.Max(memory.Media - before, 0);
                    var to = source.Duration > 0 ? Math.Min(memory.Media + after, source.Duration) : memory.Media + after;

                    // One stretch per layer (two clips of one file at two moments each keep their own).
                    if (!HasPreRoll(clipKey, source, to))
                    {
                        // The old stretch goes, the one being read too (it would keep a lane on a stretch no one shows).
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

    /// <summary>Waits (beating) until every frame the last <see cref="Resolve"/> calls missed is decoded: exports, filmstrips, tests.</summary>
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

    /// <summary>Whether the frame at <paramref name="seconds"/> of source <paramref name="id"/> is decoded (no pin).</summary>
    public bool HasExact(int id, double seconds)
    {
        SourceState? source;
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out source))
                return true;
            if (source.Failed)
                return true;
        }

        seconds = Math.Max(seconds, 0);
        if (source.Duration > 0)
            seconds = Math.Min(seconds, source.Duration);
        if (_cache.Has(source.Serial, seconds))
            return true;

        // A moment with no frame is not waited for: it is drawn with the nearest one, and the frame counts as approximate.
        lock (_gate)
            return IsMissing(source.Serial, seconds);
    }

    // ----- Hints -----

    /// <summary>
    /// Reads the client's hints again when they changed: each becomes a demand (a scrub's heading, the first frames of each
    /// cut ahead, the pointer's moment or its play-ahead). A hint that was there before keeps its demand (a lane on it reads
    /// on; one met stays met); the others' demands go. The cuts' stretches are held in the cache until their hints go.
    /// </summary>
    public void TakeHints()
    {
        ref var control = ref _shared.Control;
        var version = Volatile.Read(ref control.HintsVersion);
        if (version == _hintsVersion)
            return;
        _hintsVersion = version;

        // Hints may name a source the client put in the table since the last frame (a plan's new file, a file opened
        // ahead of a drop): the table is read first, or they would be dropped as naming nothing.
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

                // What each kind asks for: a frame (and some kept behind it), or a stretch from its moment on.
                var (demandKind, urgency, stretch, keepFrom, target) = kind switch
                {
                    GpuSeekHintKind.ScrubAhead => (DemandKind.Hinted, 0, false, seconds - Fit(source, KeepBehind, BehindShare), seconds),
                    GpuSeekHintKind.CutEntry => (DemandKind.Hinted, 1, true, seconds, seconds + Math.Min(span, Fit(source, (CutEntryFrames - 0.5) * frame, CutShare))),
                    GpuSeekHintKind.Pointer => (DemandKind.Hover, 0, false, seconds - Fit(source, 0.5, BehindShare), seconds),
                    GpuSeekHintKind.HoverRoll => (DemandKind.Hover, 1, true, seconds, seconds + Math.Min(span, Fit(source, HoverRollMost, HoverShare))),
                    _ => (DemandKind.Hinted, 2, false, seconds - Fit(source, 0.5, BehindShare), seconds),
                };
                if (source.Duration > 0)
                    target = Math.Min(target, source.Duration);

                // A cut's first frames stay until its hint goes (the playhead passed it, or it left the horizon): all of
                // them held within a share of the budget; past it, only the cut's first frame.
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

                    // The stretch learned another length (the source's frame length known now): worked again when not there.
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

    // ----- Pacing while playing (5.0-Release) -----
    // A chase at the card's full speed holds the device from the renderer (the decoder and the copies of its frames share
    // the device's lock with every draw): the bench measured 20-50 ms a frame drawn while a cut's chase ran beside
    // playback. So while playing, work ahead (a cut's first frames) decodes for a slice of the time after each frame drawn
    // and then waits for the next, unless its cut is so near that only the full speed makes it.

    private readonly object _drawnGate = new();
    private long _drawnCount;
    private long _lastDrawn = Stopwatch.GetTimestamp();
    private double _drawInterval = 1 / 30.0;

    /// <summary>How fast a chase runs at full speed, frames a second (a 1440p HEVC stream on a current card: about 480).</summary>
    private const double FullSpeedFps = 400;

    /// <summary>A frame was drawn (EndFrame): the paced lanes go on.</summary>
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
    /// Whether a lane should wait for the next frame drawn before it decodes on (a lane's own thread): while playing, for
    /// work ahead (not what is shown or read ahead of it, not an export's), once its slice since the last frame drawn is
    /// used, unless it is a cut so near that it needs the full speed.
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

    /// <summary>Waits for the next frame drawn, 40 ms at most (a lane's own thread).</summary>
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

    // The cuts' stretches the cache holds on to (from the hints): source serial, from, to.
    private readonly List<(int Source, double From, double To)> _held = [];

    /// <summary>A source nothing has wanted for this long, and not drawn in the last frame, has its idle decoders closed, seconds.</summary>
    private const double IdleDecoderSeconds = 15;

    private long _nextTidy;

    /// <summary>
    /// Closes the idle decoders of sources nothing has wanted for a while (Pass 5.0-Release: a file opened ahead of a drop
    /// that never came, a clip deleted): their video memory goes back. Called on the worker's every turn; looks every two
    /// seconds. A source in the frame drawn last keeps its decoders however long the player rests, and so does one a hint
    /// still names (a cut ahead of a long pause: its decoder waits parked at the cut's first frames for Space).
    /// </summary>
    public void Tidy()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextTidy)
            return;
        _nextTidy = now + 2 * Stopwatch.Frequency;
        lock (_gate)
        {
            foreach (var source in _sources.Values)
            {
                if (source.Decoders.Count == 0 || now - source.LastWanted < IdleDecoderSeconds * Stopwatch.Frequency || Drawn(source))
                    continue;

                // A hint's demand met stays as long as its hint (TakeHints): the hints, unchanged through a pause, are
                // not read again, so the source's LastWanted is not refreshed by them.
                var waiting = false;
                foreach (var demand in _demands)
                    waiting |= demand.Source == source && !demand.Cancelled && (!demand.Done || IsHint(demand.Kind));
                if (!waiting)
                    BuryIdle(source);
            }
        }

        DisposeBuried();
    }

    // Whether a source was drawn in the last frame (under the lock).
    private bool Drawn(SourceState source)
    {
        foreach (var memory in _layers.Values)
        {
            if (memory.Source == source.Serial && memory.LastFrame == _frame)
                return true;
        }

        return false;
    }

    // The share of the cache's frames all the cuts' held stretches may take together.
    private const double HeldShare = 0.3;

    private static bool IsHint(DemandKind kind) => kind is DemandKind.Hinted or DemandKind.Hover;

    // ----- Demands -----

    private void Ask(SourceState source, double seconds, DemandKind kind, long clipKey, double keepFrom)
    {
        // A stretch of the file that failed to decode a moment ago is left alone for a while (never a retry loop).
        if (Math.Abs(seconds - source.FailedAt) < 1.0 && Stopwatch.GetTimestamp() < source.FailedUntil)
            return;

        lock (_gate)
        {
            if (IsMissing(source.Serial, seconds))
                return;

            // One demand per frame of a source, whichever layers want it (the main video, its HUD pieces and its blurred
            // background read one moment of one file): another layer's demand for the same frame is this one's too.
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

            // A click where the pointer rested (or the playhead at a cut still being decoded): the hint already being
            // decoded becomes this layer's demand (no second chase).
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

            // One demand per layer and kind: a scrub moves it rather than adding another (the lane on it follows).
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

            // At once an interest of the cache: under a small budget (filmstrips) the frames decoded for it must not be the
            // first to go before the frame is drawn.
            if (kind == DemandKind.Visible)
                UpdateInterests();
        }

        _work.Set();
    }

    /// <summary>
    /// A demand met a moment ago whose frame is asked for again and is not there (the cache let it go before it was drawn):
    /// worked again, never left done while an exact-frame wait waits for it (under the lock).
    /// </summary>
    private void Revive(Demand demand)
    {
        if (!demand.Done || demand.Kind == DemandKind.PreRoll || _cache.Has(demand.Source.Serial, demand.Target))
            return;
        demand.Done = false;
        demand.Asked = Stopwatch.GetTimestamp();
    }

    /// <summary>A visible demand met by a frame already there: it goes (and its latency is counted when a lane made it).</summary>
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
            var weight = demand.Kind switch { DemandKind.Visible => 1.0, DemandKind.PlayAhead => 1.0, DemandKind.Hinted => 0.6, DemandKind.Hover => 0.3, _ => 0.4 };
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

    // ----- The lanes -----

    private void RunLane(int lane)
    {
        _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
        var waits = new WaitHandle[] { _work, _stopping };
        while (!_stopping.WaitOne(0))
        {
            Demand? demand;
            MfVideoDecoder? decoder;
            lock (_gate)
                (demand, decoder) = Pick(lane);

            if (demand is null)
            {
                if (WaitHandle.WaitAny(waits, 250) == 1)
                    return;
                continue;
            }

            try
            {
                if (decoder is null)
                {
                    decoder = OpenFor(demand.Source, lane);
                    lock (_gate)
                        demand.Decoder = decoder;
                }

                if (decoder is not null)
                    Work(lane, demand, decoder);
            }
            catch (GpuException ex) when (ex.IsDeviceLost)
            {
                _laneFault = ex;
                _arrived.Set();
                return;
            }
            catch (Exception ex) when (ex is GpuException or InvalidOperationException or IOException or ArgumentException)
            {
                // A file that fails part way (a broken stream): its decoder goes; the source is tried again from scratch,
                // that stretch of it only after three seconds.
                LastError = $"{Path.GetFileName(demand.Source.Path)}: {ex.Message}";
                (demand.Source.FailedAt, demand.Source.FailedUntil) = (demand.Target, Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency);
                lock (_gate)
                {
                    if (decoder is not null)
                    {
                        demand.Source.Decoders.Remove(decoder);
                        decoder.Owner = -1;
                    }

                    demand.Done = true;
                }

                decoder?.Dispose();
                decoder = null;
            }
            finally
            {
                Volatile.Write(ref _laneBusySince[lane], 0);
                lock (_gate)
                {
                    (demand.Lane, demand.Decoder) = (-1, null);
                    if (decoder is not null)
                        decoder.Owner = -1;

                    // A pre-roll or a hint met stays (done): its stretch stays one of the cache's interests, so what was
                    // decoded ahead is still there when it is asked for (the cuts' frames, until their hints go).
                    if (demand.Cancelled || demand.Done && demand.Kind is not (DemandKind.PreRoll or DemandKind.Hinted or DemandKind.Hover))
                        _demands.Remove(demand);
                    if (demand.Source.Retired)
                        BuryIdle(demand.Source);
                }
            }
        }
    }

    // Picks made (under the lock): a demand no decoder can come for in a pick is passed over for the rest of it (Demand.PassedIn).
    private long _picks;

    /// <summary>
    /// The most urgent demand no lane works on and whose frame is not there yet, with the decoder to work it with (the one
    /// of its source that reaches it in the fewest frames, not one another lane holds; null: open one). One no decoder can
    /// come for now (its source has as many as it may, each held by a lane or kept where it is wanted) is passed over for
    /// the next most urgent (5.0-Release review: a seventh cut of one file within the horizon left the lanes idle, and the
    /// pre-roll, the pointer's work and other files' cuts were never decoded while paused). Under the lock.
    /// </summary>
    private (Demand?, MfVideoDecoder?) Pick(int lane)
    {
        var pick = ++_picks;
        while (true)
        {
            var best = MostUrgent(pick);
            if (best is null)
                return (null, null);
            if (!DecoderFor(best, out var choice, out var open))
            {
                best.PassedIn = pick;
                continue;
            }

            if (open)
                best.Source.Opening++;
            best.Lane = lane;
            best.Decoder = choice;
            if (choice is not null)
            {
                choice.Owner = lane;
                choice.LastUsed = Stopwatch.GetTimestamp();
            }

            Volatile.Write(ref _laneBusySince[lane], Stopwatch.GetTimestamp());
            return (best, choice);
        }
    }

    /// <summary>The most urgent demand to work now, not one passed over in this pick (under the lock).</summary>
    private Demand? MostUrgent(long pick)
    {
        Demand? best = null;
        foreach (var demand in _demands)
        {
            if (demand.Lane >= 0 || demand.Cancelled || demand.Done || demand.Source.Failed || demand.Source.Retired || demand.PassedIn == pick)
                continue;

            // The most urgent kind first; among hints of one kind, the one wanted soonest (the earliest cut); then the oldest.
            if (best is not null && !(demand.Rank < best.Rank || demand.Rank == best.Rank && (demand.At < best.At || demand.At == best.At && demand.Asked < best.Asked)))
                continue;

            // Its frame is there already (another lane passed through it), or a stretch's whole stretch is: nothing to
            // decode, and no decoder opened for it (a pre-roll is never given the decoder parked at the playhead).
            if (!demand.Stretch ? _cache.Has(demand.Source.Serial, demand.Target) : _cache.Covers(demand.Source.Serial, demand.KeepFrom, demand.Target))
            {
                demand.Done = true;
                continue;
            }

            // Another lane reads this file forward through it (or up to just short of it): one reader per stretch of a
            // file, never a second decoder sought to the same key frame to decode the same frames (5.0-Verify).
            if (ReadByAnotherLane(demand) is not null)
                continue;

            // The pre-roll and the pointer's work wait while a frame on screen is being chased (and, paused, the cuts'): the
            // card's decoder is one, and a second stream on it halves the chase (SeekBench: cold seeks a third slower with
            // the pre-roll beside them). Playing, a cut ahead is not held up by one late frame.
            if ((demand.Kind is DemandKind.PreRoll or DemandKind.Hover || demand.HintKind == GpuSeekHintKind.CutEntry && !_lastWasPlaying) && VisibleBeingDecoded())
                continue;
            best = demand;
        }

        return best;
    }

    /// <summary>
    /// The decoder a demand would be worked with now (under the lock): the free one of its source that reaches it in the
    /// fewest frames and may be taken for it (<paramref name="choice"/>), or one more to be opened (<paramref name="open"/>).
    /// False when none can come: its source has as many as it may, each held by a lane or kept where it is wanted.
    /// </summary>
    private bool DecoderFor(Demand best, out MfVideoDecoder? choice, out bool open)
    {
        var source = best.Source;
        (choice, open) = (null, false);
        var cost = double.PositiveInfinity;
        foreach (var decoder in source.Decoders)
        {
            if (decoder.Owner >= 0 || !MayTake(best, decoder, out var penalty))
                continue;
            var c = decoder.CostTo(best.Stretch && best.Kind != DemandKind.PreRoll ? StartOf(best, decoder, best.Target) : best.Target) + penalty;
            if (c < cost)
                (choice, cost) = (decoder, c);
        }

        // No decoder free, or only those parked at a visible layer's moment or a cut: one more, while the source has room
        // (opening costs a file open, on this lane's thread; the cuts ahead may have a few more). With the pool full an open
        // closes another (TrimToCapacity): the pointer's work never opens one then, and the other work ahead only when one
        // parked nowhere it is wanted can go (never the playhead's, or a sooner cut's: those wait for it).
        if (choice is not null && cost < 1e6)
            return true;
        var most = best.HintKind == GpuSeekHintKind.CutEntry ? MostPerSourceForCuts : MostPerSource;
        if (source.Decoders.Count + source.Opening < most && Stopwatch.GetTimestamp() >= source.NoOpensUntil &&
            (!(IsHint(best.Kind) || best.Kind == DemandKind.PreRoll) || OpenDecoders() < Capacity || best.Kind != DemandKind.Hover && AnyIdleUnwanted()))
        {
            (choice, open) = (null, true);
            return true;
        }

        return choice is not null;
    }

    /// <summary>
    /// Whether a free decoder may be taken for a demand, and the cost added for taking it (1e6: only when no other can
    /// come). Under the lock.
    /// </summary>
    private bool MayTake(Demand best, MfVideoDecoder decoder, out double penalty)
    {
        // For a hint or the pre-roll, a decoder that goes on forward from exactly there (its stretch decoded up to the
        // decoder already) is the one: another would seek back to the same key frame and decode the same frames again.
        penalty = 0;
        if (!(IsHint(best.Kind) || best.Kind == DemandKind.PreRoll) || best.Stretch && ContinuesStretch(best, decoder))
            return true;

        // One parked at a visible layer's moment is never taken from it while playing (the frames shown next come from it:
        // taking it would make playback chase), nor for a cut ahead (it opens one of its own, or waits: the playhead's own
        // stretch is what Space plays first); paused, for the others only when no other can come (5.0-Release: with every
        // decoder open, a cut's hint took the playing one, and playback stuttered at every new cut).
        if (IsParkedAtVisible(decoder))
        {
            if (_lastWasPlaying || best.HintKind == GpuSeekHintKind.CutEntry)
                return false;
            penalty = 1e6;
        }

        // One parked at a cut's first frames waits there for the playhead (it reads on from there at real time): never
        // taken for the pointer, the pre-roll or a later cut; for an earlier cut, only when no other can come.
        switch (ParkedAtCut(decoder, best))
        {
            case 2:
                return false;
            case 1:
                penalty = 1e6;
                break;
        }

        return true;
    }

    /// <summary>The decoders open and being opened, every source's (under the lock).</summary>
    private int OpenDecoders()
    {
        var open = 0;
        foreach (var source in _sources.Values)
            open += source.Decoders.Count + source.Opening;
        return open;
    }

    /// <summary>Whether a free decoder of any source is parked nowhere it is wanted (TrimToCapacity closes such first; under the lock).</summary>
    private bool AnyIdleUnwanted()
    {
        foreach (var source in _sources.Values)
        {
            foreach (var decoder in source.Decoders)
            {
                if (decoder.Owner < 0 && !ParkedWhereWanted(source, decoder))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Whether a decoder stands inside a stretch with all of it up to there decoded: the stretch goes on from it (StartOf).</summary>
    private bool ContinuesStretch(Demand stretch, MfVideoDecoder decoder)
    {
        var read = decoder.ReadTime;
        return !double.IsNaN(read) && !decoder.AtEnd && read > stretch.KeepFrom && read < stretch.Target && _cache.Covers(stretch.Source.Serial, stretch.KeepFrom, read);
    }

    /// <summary>
    /// Whether a decoder waits at the end of a cut's first frames for the playhead (under the lock): 0 not; 1 taken for
    /// <paramref name="best"/> only when no other decoder can come (a scrub's heading, or a cut wanted sooner than the one it
    /// waits at); 2 never taken for it (the pointer's work, the pre-roll, a cut wanted later or as soon).
    /// </summary>
    private int ParkedAtCut(MfVideoDecoder decoder, Demand best)
    {
        var read = decoder.ReadTime;
        if (double.IsNaN(read) || decoder.AtEnd)
            return 0;
        var parked = 0;
        foreach (var demand in _demands)
        {
            if (ReferenceEquals(demand, best) || demand.HintKind != GpuSeekHintKind.CutEntry || demand.Cancelled || demand.Source != best.Source ||
                read < demand.KeepFrom - 1e-6 || read > demand.Target + decoder.FrameDuration * 1.5)
                continue;
            if (best.Kind is DemandKind.Hover or DemandKind.PreRoll)
                return 2;
            parked = Math.Max(parked, best.HintKind == GpuSeekHintKind.CutEntry && demand.At <= best.At ? 2 : 1);
        }

        return parked;
    }

    // The share of the cache's frames a stretch of one source may take: what is kept behind a frame asked for, the paused
    // pre-roll round it, what is read ahead while playing, a cut's first frames, the pointer's play-ahead.
    private const double BehindShare = 0.2, PreRollShare = 0.4, AheadShare = 0.25, CutShare = 0.05, HoverShare = 0.1;

    /// <summary>
    /// A stretch of a source, in seconds, held to the share of the cache's budget its frames may take (5.0-Verify: a 420 fps
    /// 1440p recording's 3 s pre-roll is 1260 frames, more than a 3 GB cache holds: decoded, let go and decoded again).
    /// </summary>
    private double Fit(SourceState source, double seconds, double share)
    {
        if (source.FrameBytes <= 0 || source.FrameDuration <= 0)
            return seconds;
        var frames = _cache.BudgetBytes * share / source.FrameBytes;
        return Math.Min(seconds, Math.Max(frames, 8) * source.FrameDuration);
    }

    /// <summary>Whether a lane is decoding toward a frame on screen now (under the lock).</summary>
    private bool VisibleBeingDecoded()
    {
        foreach (var demand in _demands)
        {
            if (demand.Lane >= 0 && demand.Kind == DemandKind.Visible && !demand.Cancelled)
                return true;
        }

        return false;
    }

    /// <summary>How far past a reader's own target a frame of its file is left to it (it is taken next, from where it stopped).</summary>
    private const double ReaderReach = 1.0;

    /// <summary>
    /// The demand of another lane whose decoder reads <paramref name="demand"/>'s file forward to, through, or up to a
    /// second short of its frame (under the lock); null when none does. A moment behind every reader, or farther ahead, gets
    /// a decoder of its own (two moments of one file: a crossfade's head, a cut far into it).
    /// </summary>
    private Demand? ReadByAnotherLane(Demand demand)
    {
        // A hint's stretch (a cut's first frames, the pointer's play-ahead) waits for a reader that passes its first frame.
        var at = demand.Stretch && demand.Kind != DemandKind.PreRoll ? demand.KeepFrom : demand.Target;
        foreach (var other in _demands)
        {
            if (other.Lane < 0 || ReferenceEquals(other, demand) || other.Source != demand.Source || other.Cancelled || other.Decoder is not { } reader)
                continue;
            if (Delivers(other, reader, at))
                return other;
        }

        return null;
    }

    /// <summary>
    /// Whether a lane's read (its demand <paramref name="read"/>, its decoder <paramref name="reader"/>) will hand over the
    /// frame at <paramref name="target"/> sooner than a chase of its own would (under the lock): the frame lies ahead of the
    /// reader (or of the key frame it is landing on), no farther than the read goes, among the frames it keeps, and the
    /// frames still to read before it are no more than a seek to the key frame before it and the chase from there (5.0-Verify
    /// review: a click past a pre-roll's end must not wait for it).
    /// </summary>
    private static bool Delivers(Demand read, MfVideoDecoder reader, double target)
    {
        var from = reader.ReadTime;
        if (double.IsNaN(from))
        {
            var landing = reader.ListedKeyAtOrBefore(read.Target);
            from = double.IsNaN(landing) ? read.Target - ReaderReach : landing;
        }

        if (target < from - 1e-6 || target > read.Target + 1e-6 || target < read.KeepFrom - 1e-6)
            return false;

        var frame = Math.Max(reader.FrameDuration, 1e-4);
        var key = reader.ListedKeyAtOrBefore(target);
        var chase = 4 + (double.IsNaN(key) ? ReaderReach : Math.Max(target - key, 0)) / frame;
        return (target - from) / frame <= chase;
    }

    private bool IsParkedAtVisible(MfVideoDecoder decoder)
    {
        if (double.IsNaN(decoder.ReadTime))
            return false;
        foreach (var demand in _demands)
        {
            if (demand.Kind is DemandKind.Visible or DemandKind.PlayAhead && demand.Source.Decoders.Contains(decoder) &&
                Math.Abs(demand.Target - decoder.ReadTime) < 2)
                return true;
        }

        foreach (var memory in _layers.Values)
        {
            if (_frame - memory.LastFrame < 4 && Math.Abs(memory.Media - decoder.ReadTime) < 2)
                return true;
        }

        return false;
    }

    /// <summary>Opens one more decoder for a source (on the lane's thread: a file open never holds up a frame).</summary>
    private MfVideoDecoder? OpenFor(SourceState source, int lane)
    {
        TrimToCapacity();
        try
        {
            // Opening a file on a sleeping disk or a share may take long: not the stuck-lane watchdog's business.
            Volatile.Write(ref _laneBusySince[lane], 0);
            // On the card unless there is none (WARP) or the client asks for the processor (a driver that shows broken frames).
            var hardware = !_gpu.IsWarp && Volatile.Read(ref _shared.Control.SoftwareDecodeRequest) == 0;
            var decoder = new MfVideoDecoder(_gpu, source.Path, source.StreamIndex, hardware, _cache, source.Serial);
            Volatile.Write(ref _laneBusySince[lane], Stopwatch.GetTimestamp());
            Interlocked.Add(ref _decoded, decoder.FramesDecoded);
            decoder.SetIFrames(source.IFrames);
            lock (_gate)
            {
                source.Opening = Math.Max(source.Opening - 1, 0);
                if (source.Retired)
                {
                    decoder.Dispose();
                    return null;
                }

                if (source.Duration <= 0)
                    source.Duration = Math.Max(decoder.DurationSeconds - decoder.FrameDuration * 0.5, 0);

                // The source's frame length: a known one is handed to the new decoder; the decoder's is taken only when known.
                if (source.FrameDurationKnown)
                    decoder.SeedFrameDuration(source.FrameDuration);
                else if (decoder.FrameDurationKnown)
                    (source.FrameDuration, source.FrameDurationKnown) = (decoder.FrameDuration, true);
                else if (source.FrameDuration <= 0)
                    source.FrameDuration = decoder.FrameDuration;
                source.FrameBytes = (long)decoder.Width * decoder.Height * 3 / 2 * (decoder.TenBit ? 2 : 1);
                (decoder.Owner, decoder.LastUsed) = (lane, Stopwatch.GetTimestamp());
                source.Decoders.Add(decoder);
            }

            Interlocked.Increment(ref _opens);
            _arrived.Set(); // its first frame is in the cache now
            return decoder;
        }
        catch (Exception ex) when (ex is GpuException { IsDeviceLost: false } or DllNotFoundException or EntryPointNotFoundException)
        {
            // A file Media Foundation cannot read (or no Media Foundation at all: a Windows N edition without its Media
            // Feature Pack): not tried again until the client changes the entry. Solid, still and pattern layers still draw.
            // A source that already decodes with another decoder is not failed for one more that would not open (a file
            // busy for a moment): no more opens of it for a few seconds, its open decoders go on (5.0-Verify review: an
            // export would otherwise leave the main video out from there on, its frames still counted exact).
            lock (_gate)
            {
                source.Opening = Math.Max(source.Opening - 1, 0);
                if (source.Decoders.Count == 0)
                    source.Failed = true;
                else
                    source.NoOpensUntil = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
            }

            LastError = $"{Path.GetFileName(source.Path)}: {ex.Message}";
            _arrived.Set();
            return null;
        }
        catch
        {
            lock (_gate)
                source.Opening = Math.Max(source.Opening - 1, 0);
            throw;
        }
    }

    private int _opens;

    /// <summary>Decodes toward a demand, frame by frame, until its frame is there, it is taken back, or something more urgent waits.</summary>
    private void Work(int lane, Demand demand, MfVideoDecoder decoder)
    {
        var source = demand.Source;
        var target = demand.Target;

        // A stretch that is all there already (frames stepped over, a chase that passed through it): nothing to do.
        if (demand.Stretch && _cache.Covers(source.Serial, demand.KeepFrom, target))
        {
            lock (_gate)
                demand.Done = true;
            return;
        }

        var seeksBefore = decoder.Seeks;
        try
        {
            WorkOn(lane, demand, decoder, source, target);
        }
        finally
        {
            Interlocked.Add(ref _seeks, decoder.Seeks - seeksBefore);
        }
    }

    private void WorkOn(int lane, Demand demand, MfVideoDecoder decoder, SourceState source, double target)
    {
        decoder.KeepingAhead = demand.Kind != DemandKind.Visible;
        decoder.Prepare(StartOf(demand, decoder, target));
        var frames = 0;

        // Seeks made again for this target because the last one landed after it, or because the reader had passed its frame
        // (reset when the demand moves).
        var (lateTries, refetches) = (0, 0);
        while (true)
        {
            Volatile.Write(ref _laneBusySince[lane], Stopwatch.GetTimestamp());
            double keepFrom;
            var retarget = double.NaN;
            lock (_gate)
            {
                if (demand.Cancelled || source.Retired || _stopping.WaitOne(0))
                    return;

                // A scrub moved the demand: forward within reach goes on, anything else starts again from its key frame
                // (the seek itself made outside the lock: the renderer never waits on a file).
                if (Math.Abs(demand.Target - target) > 1e-6)
                {
                    target = demand.Target;
                    decoder.KeepingAhead = demand.Kind != DemandKind.Visible;
                    retarget = StartOf(demand, decoder, target);
                    (lateTries, refetches) = (0, 0);
                }

                keepFrom = demand.KeepFrom;

                // Something more urgent that no lane serves (and that this read does not pass on its way): give way, after
                // at least a few frames, so nothing starves.
                if (frames >= 4 && demand.Rank > 0 && MoreUrgentWaiting(demand, decoder))
                    return;
            }

            if (!double.IsNaN(retarget))
                decoder.Prepare(retarget);

            if (!demand.Stretch && _cache.Has(source.Serial, target))
                break;

            // The frame wanted is the one the reader stands on (read before, then let go of by the cache): reading on can
            // never give it back, only a seek can (never the frame after it stretched over it).
            if (!demand.Stretch && PassedOver(decoder, target, source) && refetches++ < 2)
            {
                decoder.SeekTo(target);
                continue;
            }

            var before = decoder.ReadTime;
            if (demand.Stretch && !double.IsNaN(decoder.ReadTime) && decoder.ReadTime >= target - decoder.FrameDuration * 0.5)
                break;

            // A stretch keeps its frames from a hair before its first moment: the frame that shows that moment may end, as
            // first read, a hair short of it (a container's millisecond clock: the frame of 86.2667 s is stamped 86.233 s
            // and lasts 33.3 ms until the next one read says it reaches 86.267 s, FrameCache.CloseGap).
            if (!decoder.Step(demand.Stretch ? keepFrom - StampHair : keepFrom, out var time, out var until))
            {
                // The end of the stream: the last frame stands for every moment past it, and the source ends there (a
                // container longer than its video: a moment past the last frame is asked for as the last frame).
                if (decoder.AtEnd && !double.IsNaN(decoder.ReadTime))
                {
                    lock (_gate)
                    {
                        if (source.Duration <= 0 || decoder.ReadTime < source.Duration)
                            source.Duration = Math.Max(decoder.ReadTime, 0);
                    }
                }

                break;
            }

            frames++;
            Interlocked.Increment(ref _decoded);
            if (demand.Kind == DemandKind.Visible && until > target - 1e-6 && time <= target + 1e-6 || _waitingExact > 0 || ShownSoon(source, time, until))
                _arrived.Set();
            if (Paced(demand, decoder))
                WaitForNextDraw();

            // Past the target without a frame that covers it (FrameCache.CloseGap already joined frames read in a row):
            // - a seek that landed after it (a container's coarse index, an open GOP whose first pictures the decoder
            //   drops): sought again from further back, 1, 2 then 4 s before its key frame;
            // - else a frame or two the decoder dropped: the frame after stands for the moment;
            // - else a hole in the stream: the moment is missing (shown with the nearest frame; an export fails on it and
            //   is made by FFmpeg) and is not decoded for again for a while. Never a key frame stretched over frames
            //   nothing decoded (5.0-Verify: the user's export held each key frame).
            if (!double.IsNaN(time) && time > target + 1e-6 && !demand.Stretch && !_cache.Has(source.Serial, target))
            {
                if (decoder.LastWasLanding && lateTries < 3)
                {
                    var key = decoder.KeyAtOrBefore(target);
                    var back = 1 << lateTries++;
                    decoder.SeekTo(Math.Max((double.IsNaN(key) ? target : Math.Min(key, target)) - back, 0));
                    Interlocked.Increment(ref _lateLandings);
                    continue;
                }

                // The frame before this one was the one wanted and the cache no longer has it (it was read but not kept, or
                // let go of since): sought again. One still there would have been joined to this one (CloseGap): a hole.
                if (!double.IsNaN(before) && before <= target + 1e-6 && !_cache.Has(source.Serial, before) && refetches++ < 2)
                {
                    decoder.SeekTo(target);
                    continue;
                }

                if (_cache.StretchBack(source.Serial, time, target, decoder.FrameDuration * 2.5))
                {
                    Interlocked.Increment(ref _stretches);
                }
                else
                {
                    lock (_gate)
                        _missing.Add((source.Serial, target, Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency));
                    Interlocked.Increment(ref _missingFrames);
                }

                break;
            }

            if (frames > 20_000)
                break;
        }

        lock (_gate)
        {
            // The frame length as the frames space it (MfVideoDecoder.LearnSpacing), once known: what "the same frame" means from now on.
            if (decoder.FrameDurationKnown)
                (source.FrameDuration, source.FrameDurationKnown) = (decoder.FrameDuration, true);
            demand.Done = true;
            if (demand.Kind == DemandKind.Visible)
            {
                var ms = (float)Stopwatch.GetElapsedTime(demand.Asked).TotalMilliseconds;
                LastExactLatencyMs = ms;
                WorstExactLatencyMs = Math.Max(WorstExactLatencyMs, ms);
            }
        }

        _arrived.Set();
    }

    /// <summary>
    /// Whether the reader stands on (has just read) the frame that shows <paramref name="target"/>, and that frame is no
    /// longer in the cache: the next frame read comes after it. A frame still there says itself where it ends (and the next
    /// frame read joins it, FrameCache.CloseGap).
    /// </summary>
    private bool PassedOver(MfVideoDecoder decoder, double target, SourceState source) =>
        !double.IsNaN(decoder.ReadTime) && !decoder.AtEnd && decoder.ReadTime <= target + 1e-6 && target < decoder.ReadTime + decoder.FrameDuration * 0.98
        && !_cache.Has(source.Serial, decoder.ReadTime);

    /// <summary>
    /// Whether a frame just decoded is one a visible demand waits for that this lane does not serve (one it reads through
    /// on its way to its own target, ReadByAnotherLane): the renderer is woken for it as for its own.
    /// </summary>
    private bool ShownSoon(SourceState source, double time, double until)
    {
        if (double.IsNaN(time))
            return false;
        lock (_gate)
        {
            foreach (var d in _demands)
            {
                if (d.Kind == DemandKind.Visible && !d.Done && !d.Cancelled && d.Source == source && d.Target >= time - 1e-6 && d.Target < until + 1e-6)
                    return true;
            }
        }

        return false;
    }

    private bool HasPreRoll(long clipKey, SourceState source, double to)
    {
        foreach (var d in _demands)
        {
            if (d.Kind == DemandKind.PreRoll && d.ClipKey == clipKey && d.Source == source && Math.Abs(d.Target - to) < 0.25)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a more urgent demand waits that no lane serves (under the lock): not one of this file that this lane's
    /// read reaches on its way (the frame shown while it reads ahead is one it passes), nor one no decoder can come for
    /// (its source at its most, each decoder kept where it is wanted: Pick passes it over) that could not take this
    /// lane's either: giving way would only start this read again.
    /// </summary>
    private bool MoreUrgentWaiting(Demand demand, MfVideoDecoder decoder)
    {
        foreach (var d in _demands)
        {
            if (d.Lane >= 0 || d.Done || d.Cancelled || d.Rank >= demand.Rank || d.Source.Failed)
                continue;
            if (d.Source == demand.Source && Delivers(demand, decoder, d.Target))
                continue;
            if (!DecoderFor(d, out _, out _) && (d.Source != demand.Source || !MayTake(d, decoder, out _)))
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Where a lane starts decoding for a demand: the pre-roll from the first moment of its stretch not decoded yet
    /// (forward from the decoder when the cache already holds the stretch up to it), the others from their target.
    /// </summary>
    private double StartOf(Demand demand, MfVideoDecoder decoder, double target)
    {
        if (!demand.Stretch)
            return target;
        var read = decoder.ReadTime;
        if (!double.IsNaN(read) && read > demand.KeepFrom && read < target && _cache.Covers(demand.Source.Serial, demand.KeepFrom, read))
            return Math.Min(read + decoder.FrameDuration * 0.5, target);
        return demand.KeepFrom;
    }

    /// <summary>
    /// Makes room for one more decoder in the pool: the free ones used longest ago are closed, those parked where they are
    /// wanted (at a visible layer's moment, at a cut's first frames waiting for the playhead) last of all (5.0-Release
    /// review: the decoders of the soonest cuts, assigned first, were the first to go, and Space stuttered at them).
    /// </summary>
    private void TrimToCapacity()
    {
        lock (_gate)
        {
            var total = _sources.Values.Sum(s => s.Decoders.Count);
            if (total >= Capacity)
            {
                var all = _sources.Values.SelectMany(s => s.Decoders.Where(d => d.Owner < 0).Select(d => (Source: s, Decoder: d))).ToList();
                foreach (var (source, decoder) in all.OrderBy(x => ParkedWhereWanted(x.Source, x.Decoder)).ThenBy(x => x.Decoder.LastUsed).Take(total - Capacity + 1))
                {
                    source.Decoders.Remove(decoder);
                    _graveyard.Add(decoder);
                }
            }
        }

        DisposeBuried();
    }

    /// <summary>Whether a decoder is parked where it is wanted: at a visible layer's moment, or at a cut's first frames waiting for the playhead (under the lock).</summary>
    private bool ParkedWhereWanted(SourceState source, MfVideoDecoder decoder) => IsParkedAtVisible(decoder) || ParkedAtAnyCut(source, decoder);

    /// <summary>Whether a decoder waits at the end of the first frames of any cut ahead of its source (under the lock; see ParkedAtCut).</summary>
    private bool ParkedAtAnyCut(SourceState source, MfVideoDecoder decoder)
    {
        var read = decoder.ReadTime;
        if (double.IsNaN(read) || decoder.AtEnd)
            return false;
        foreach (var demand in _demands)
        {
            if (demand.HintKind == GpuSeekHintKind.CutEntry && !demand.Cancelled && demand.Source == source &&
                read >= demand.KeepFrom - 1e-6 && read <= demand.Target + decoder.FrameDuration * 1.5)
                return true;
        }

        return false;
    }

    // Decoders let go of under the lock and disposed after it (a reader's teardown is the media pipeline's: never under _gate).
    private readonly List<MfVideoDecoder> _graveyard = [];

    /// <summary>A source's idle decoders out of its list, to be disposed after the lock (under the lock).</summary>
    private void BuryIdle(SourceState source)
    {
        for (var i = source.Decoders.Count - 1; i >= 0; i--)
        {
            if (source.Decoders[i].Owner < 0)
            {
                _graveyard.Add(source.Decoders[i]);
                source.Decoders.RemoveAt(i);
            }
        }
    }

    /// <summary>Disposes the decoders let go of (outside the lock).</summary>
    private void DisposeBuried()
    {
        MfVideoDecoder[] buried;
        lock (_gate)
        {
            if (_graveyard.Count == 0)
                return;
            buried = [.. _graveyard];
            _graveyard.Clear();
        }

        foreach (var decoder in buried)
            decoder.Dispose();
    }

    // ----- The source table -----

    /// <summary>Reads the source table again: changed entries lose their decoders and their cached frames.</summary>
    private void Resync()
    {
        lock (_gate)
        {
            for (var id = 0; id < CompositorProtocol.MaxSources; id++)
            {
                ref var entry = ref _shared.Source(id);
                var generation = Volatile.Read(ref entry.Generation);
                _sources.TryGetValue(id, out var known);
                if (generation == 0)
                {
                    if (known is not null)
                    {
                        Retire(known);
                        _sources.Remove(id);
                    }

                    continue;
                }

                if (known is not null && known.Generation == generation)
                    continue;

                if (known is not null)
                    Retire(known);
                _sources[id] = new SourceState
                {
                    Serial = ++_serials,
                    Generation = generation,
                    Path = CompositorSharedMemory.ReadString(entry.Path),
                    StreamIndex = entry.StreamIndex,
                    PathHash = entry.PathHash,
                    IFrames = entry.IFrameCount > 0 ? ReadIFrames(id, generation, entry.IFrameCount) : null,
                };
            }
        }
    }

    private void Retire(SourceState source)
    {
        source.Retired = true;
        foreach (var demand in _demands)
        {
            if (demand.Source == source)
                demand.Cancelled = true;
        }

        _demands.RemoveAll(d => d.Source == source && d.Lane < 0);
        BuryIdle(source);
        _cache.Forget(source.Serial);
    }

    private double[]? ReadIFrames(int id, int generation, int count)
    {
        try
        {
            using var section = MemoryMappedFile.OpenExisting(CompositorProtocol.IFrameSection(_shared.BaseName, id, generation), MemoryMappedFileRights.Read);
            using var view = section.CreateViewAccessor(0, (long)count * sizeof(double), MemoryMappedFileAccess.Read);
            var times = new double[count];
            view.ReadArray(0, times, 0, count);
            return times;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stopping.Set();
        _work.Set();
        var joined = true;
        foreach (var thread in _lanes)
            joined &= thread.Join(3000);
        DisposeBuried();
        lock (_gate)
        {
            // A lane that did not stop (inside a driver) keeps its decoder and the cache: the process ends round them.
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
        _cache.Dispose();
        _work.Dispose();
        _arrived.Dispose();
        _stopping.Dispose();
    }

    private enum DemandKind
    {
        Visible = 0,
        PlayAhead = 1,
        Hinted = 2,
        PreRoll = 3,

        /// <summary>The pointer over the timeline (5.0-Release): after everything else, the playhead's own pre-roll too.</summary>
        Hover = 4,
    }

    /// <summary>A frame wanted (or a stretch, <see cref="Stretch"/>: from <see cref="KeepFrom"/> to <see cref="Target"/>).</summary>
    private sealed class Demand
    {
        public required SourceState Source;
        public double Target;
        public double KeepFrom;
        public DemandKind Kind;

        /// <summary>
        /// A hint's urgency within its kind: hinted, 0 a scrub's heading, 1 a cut ahead; the pointer's, 0 its frame, 1 its
        /// play-ahead. Among hints of one urgency the one wanted soonest goes first (<see cref="At"/>).
        /// </summary>
        public int Urgency;

        /// <summary>Every frame from <see cref="KeepFrom"/> to <see cref="Target"/> is wanted (the pre-roll, a cut's first frames, the pointer's play-ahead).</summary>
        public bool Stretch;

        /// <summary>The hint it was made from (None for the renderer's own), its moment as given, and the moment of the sequence it is wanted at.</summary>
        public GpuSeekHintKind HintKind;
        public double HintMedia = double.NaN;
        public double At;

        /// <summary>Its hint is in the list read last (TakeHints).</summary>
        public bool Kept;
        public long ClipKey;
        public long Asked;
        public int Lane = -1;

        /// <summary>The decoder the lane works it with (null before one is open, or while no lane has it): under the lock.</summary>
        public MfVideoDecoder? Decoder;
        public bool Stale;
        public bool Done;
        public bool Cancelled;

        /// <summary>The pick that found no decoder could come for it (it is passed over for the rest of that pick, see Pick).</summary>
        public long PassedIn;

        public int Rank => (int)Kind * 8 + Urgency;
    }

    /// <summary>What a layer (by its clip key) showed last: the fallback while its next frame is decoded, and its speed through its file.</summary>
    private sealed class LayerMemory
    {
        public int Source;
        public double Media = double.NaN;
        public double Playhead = double.NaN;
        public CachedFrame? Shown;
        public long LastFrame;
    }

    private sealed class SourceState
    {
        public int Serial;
        public int Generation;
        public string Path = "";
        public int StreamIndex;
        public long PathHash;
        public double[]? IFrames;
        public double Duration;
        public volatile bool Failed;
        public volatile bool Retired;
        public int Opening;
        public double FailedAt = double.NaN;
        public long FailedUntil;

        /// <summary>Seconds per frame, from its first decoder (0 before one is open): what "the same frame" means for its demands.</summary>
        public double FrameDuration;

        /// <summary>The bytes of one of its frames in the cache (0 before a decoder is open).</summary>
        public long FrameBytes;

        /// <summary>Whether its frame length was learned from its frames (or given by the stream): new decoders start from it.</summary>
        public bool FrameDurationKnown;

        /// <summary>No more decoders opened for it before this time (an open that failed while others decode it).</summary>
        public long NoOpensUntil;

        /// <summary>When a layer or a hint last wanted a frame of it (Stopwatch ticks): Tidy closes the idle decoders of one long unwanted.</summary>
        public long LastWanted = Stopwatch.GetTimestamp();
        public readonly List<MfVideoDecoder> Decoders = [];
    }
}

/// <summary>
/// The textures the client hands over (masks, LUTs, curves, stills: ResourceEntry), uploaded once per generation from
/// their own sections, and kept until the entry changes.
/// </summary>
internal sealed unsafe class ResourceCache(GpuDevice gpu, CompositorSharedMemory shared) : IDisposable
{
    private readonly Dictionary<int, (int Generation, GpuTexture? Texture, nint Lut3D, nint LutSrv, int Size)> _loaded = [];
    private int _version = -1;

    private void Sync()
    {
        var version = Volatile.Read(ref shared.Control.ResourcesVersion);
        if (version == _version)
            return;
        _version = version;
        foreach (var id in _loaded.Keys.ToList())
        {
            if (Volatile.Read(ref shared.Resource(id).Generation) != _loaded[id].Generation)
                Release(id);
        }
    }

    public GpuTexture? Texture2D(int id)
    {
        if (id is < 0 or >= CompositorProtocol.MaxResources)
            return null;
        Sync();
        if (_loaded.TryGetValue(id, out var known))
            return known.Texture;
        Load(id);
        return _loaded.TryGetValue(id, out known) ? known.Texture : null;
    }

    public (nint Srv, int Size)? Lut(int id)
    {
        if (id is < 0 or >= CompositorProtocol.MaxResources)
            return null;
        Sync();
        if (!_loaded.ContainsKey(id))
            Load(id);
        return _loaded.TryGetValue(id, out var known) && known.LutSrv != 0 ? (known.LutSrv, known.Size) : null;
    }

    private void Load(int id)
    {
        ref var entry = ref shared.Resource(id);
        var generation = Volatile.Read(ref entry.Generation);
        var (kind0, width0, height0, depth0, bytes0) = ((GpuResourceKind)entry.Kind, entry.Width, entry.Height, entry.Depth, entry.Bytes);

        // Read whole or not at all: the generation again after the fields (the client clears it while it writes them),
        // and the section must hold every byte the texture will read.
        if (generation == 0 || Volatile.Read(ref entry.Generation) != generation)
            return;
        var needed = CompositorClient.ResourceBytes(kind0, width0, height0, depth0);
        if (needed <= 0 || bytes0 < needed)
            return;

        try
        {
            using var section = MemoryMappedFile.OpenExisting(CompositorProtocol.ResourceSection(shared.BaseName, id, generation), MemoryMappedFileRights.Read);
            using var view = section.CreateViewAccessor(0, needed, MemoryMappedFileAccess.Read);
            byte* data = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref data);
            try
            {
                data += view.PointerOffset;
                var kind = kind0;
                if (kind == GpuResourceKind.Lut3D)
                {
                    var size = width0;
                    var initial = new SubresourceData { SysMem = data, SysMemPitch = (uint)(size * 16), SysMemSlicePitch = (uint)(size * size * 16) };
                    var texture = gpu.Device.CreateTexture3D(new Texture3DDesc
                    {
                        Width = (uint)size, Height = (uint)size, Depth = (uint)size, MipLevels = 1, Format = DxgiFormat.R32G32B32A32Float,
                        Usage = D3D11.UsageImmutable, BindFlags = D3D11.BindShaderResource,
                    }, &initial);
                    var srv = gpu.Device.CreateShaderResourceView(texture);
                    _loaded[id] = (generation, null, texture, srv, size);
                }
                else
                {
                    var format = kind == GpuResourceKind.Curve1D ? DxgiFormat.R32G32B32A32Float : DxgiFormat.B8G8R8A8Unorm;
                    var bytesPerPixel = kind == GpuResourceKind.Curve1D ? 16 : 4;
                    var initial = new SubresourceData { SysMem = data, SysMemPitch = (uint)(width0 * bytesPerPixel) };
                    var texture = gpu.CreateTexture(width0, height0, format, renderTarget: false, initial: &initial);
                    _loaded[id] = (generation, texture, 0, 0, 0);
                }
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or GpuException { IsDeviceLost: false })
        {
            // Not there (yet): asked again next frame.
        }
    }

    private void Release(int id)
    {
        if (!_loaded.Remove(id, out var known))
            return;
        known.Texture?.Dispose();
        Com.Release(ref known.LutSrv);
        Com.Release(ref known.Lut3D);
    }

    public void Dispose()
    {
        foreach (var id in _loaded.Keys.ToList())
            Release(id);
    }
}
