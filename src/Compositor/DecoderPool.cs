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
internal sealed partial class DecoderPool : IDisposable
{
    public const int Capacity = 12;
    private const int MostPerSource = 4;

    /// <summary>
    /// How many videos seek at once by themselves (5.1-TrackEqualityAndVram, the user's directive): a lane is a thread that sleeps until
    /// there is a frame to decode, so several tracks chase a seek side by side rather than taking turns; and the most asked for.
    /// </summary>
    private const int AutoLanes = 6, MostLanes = 8;

    /// <summary>
    /// How long a seek's picture may wait for all its videos, ms (the frame commit barrier, the user's directive): with two videos or more on
    /// screen, a picture with one at the new moment and another still at an old one is not shown: the last whole picture stays
    /// until they are all there, or this long has passed. One video needs none of it: its nearest frame at once is Instant Seek.
    /// </summary>
    private const double SeekBarrierMs = 80;

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
    private const double ExactKeepBehind = 0.1, ExactPlayAhead = 0.1;

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
        // The clock when the scene came (5.5A review: the play rate's sample, read before any wait below).
        var arrived = Stopwatch.GetTimestamp();
        _frame = frame;
        _exactOnly = exactOnly;

        // How many videos the last picture showed: each gets its share of the room, and a lane of its own for what can wait.
        var (videos, seen) = (0, 0UL);
        foreach (var (serial, _) in _resolvedNow)
        {
            var bit = 1UL << (serial & 63);
            videos += (seen & bit) == 0 ? 1 : 0;
            seen |= bit;
        }

        _videosShown = Math.Max(videos, 1);
        _resolvedNow.Clear();
        SyncSources();

        // An exact-frame scene (an export, a capture) never leaves a layer out for a stamp still being read: settled now (P13).
        if (exactOnly)
            SettleStamps(wait: true, force: true);

        // Settling: paused, or just jumped while playing. A picture of several videos then waits for them all (HoldsForOthers).
        var jumped = !double.IsNaN(_lastPlayhead) && Math.Abs(playhead - _lastPlayhead) > SeekJump;
        _settling = !exactOnly && (!playing || jumped || (_settling && !LastFrameExact));
        if (LastFrameExact || jumped)
            _barrierFrom = 0;

        // A moving playhead (playing, a seek, a scrub's step) keeps idle work off the lanes for IdleQuietMs (5.4.4).
        if (!exactOnly && (playing || !double.IsNaN(_lastPlayhead) && Math.Abs(playhead - _lastPlayhead) > 1e-9))
            Volatile.Write(ref _activeUntil, Stopwatch.GetTimestamp() + IdleQuietMs * Stopwatch.Frequency / 1000);

        // Cold play (5.5A): a jump whose first picture missed a frame (LastFrameExact still tells of that picture here) is a cold seek and
        // arms the window, as a change of the sources does (SyncSources); the first playing frame opens it, a pause closes it. An
        // exact-frame scene (an export, a capture) neither arms, opens nor uses one.
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

            // 5.5A review: the play rate the window's lead follows (a pause, a jump or new sources start it again at 1).
            SamplePlayRate(playing, jumped, playhead, arrived);
        }

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

    // A move of the playhead that is no frame's step, seconds; whether what is on screen is settling after one; when it began waiting.
    private const double SeekJump = 0.25;
    private bool _settling;
    private long _barrierFrom;

    /// <summary>
    /// Whether the picture just resolved should not be shown yet: it is a settling picture of two videos or more, some not at their frame
    /// yet, and the barrier's time is not up. The frames it waits for wake the worker (<see cref="FramesArrived"/>), which draws it again.
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

        if (!several)
            return false;

        var now = Stopwatch.GetTimestamp();
        if (_barrierFrom == 0)
            _barrierFrom = now;
        return (now - _barrierFrom) * 1000.0 / Stopwatch.Frequency < SeekBarrierMs;
    }

    // How many frames after the one on screen a press of play wants decoded (5.1-DogfoodPolish, the user's Q1).
    private const int PlayStartAhead = 2;

    /// <summary>Whether the picture drawn last was drawn playing.</summary>
    public bool Playing => _lastWasPlaying;

    /// <summary>
    /// Whether every video of the picture drawn last has its next frames decoded too (one video never waits for them: true). A file that
    /// ends sooner has none to wait for. The worker's thread, which is the one that fills what it reads.
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
                // makes it) rather than leaving the layer out quietly. A source whose file's stamp is still being read (P13) is
                // left out for a moment: the frame is not exact, so it is drawn again when the stamp is back (FramesArrived).
                if (_exactOnly && source is { Failed: true } || source is null && _deciding.ContainsKey(id))
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

        // Layers of one source at one moment (a video, its Cutouts, its blur fill's twin) share one frame,
        // and its demands: only the first of them in a frame asks (5.0-Verify, the user's bug 5).
        var repeat = false;
        foreach (var (serial, at) in _resolvedNow)
            repeat |= serial == source.Serial && Math.Abs(at - seconds) <= 1e-6;
        if (!repeat && _resolvedNow.Count < 64)
            _resolvedNow.Add((source.Serial, seconds));

        // An exact-frame scene (an export, a capture) takes an open frame's last millisecond for the frame after it (CachedFrame.CoverEnd);
        // the player takes what there is, as it always has.
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
            (memory.Source, memory.Playing) = (source.Serial, _lastWasPlaying);
        }

        if (exact is not null)
            _pinned.Add(exact);
        return exact;
    }

    /// <summary>
    /// The frame before <paramref name="frame"/> when the cache holds it (a shutter's measure of motion, 5.1-GpuFx), pinned until <see cref="EndFrame"/>. No demand
    /// is made, so nothing waits for it; an export keeps that much behind it (<see cref="ExactKeepBehind"/>), so it finds it on all but a clip's first frame.
    /// </summary>
    public CachedFrame? FrameBefore(CachedFrame frame)
    {
        var before = _cache.Before(frame, ExactKeepBehind);
        if (before is not null)
            _pinned.Add(before);
        return before;
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

            // 5.5A: new sources (a tab brought to the front, a file opened, a fresh worker) are cold: the next play holds far cuts back,
            // its play rate sampled anew (5.5A review).
            _coldArmed = true;
            _rateRestart = true;
            Resync();
            DisposeBuried();

            // A tab brought back while paused: its files' stamps are read on the pool, and waited for a moment (StampWaitMs), so its
            // first frame finds what the cache kept of them. While playing, nothing waits: they are settled as they come back.
            SettleStamps(wait: !_lastWasPlaying, force: false);
        }
        else if (Volatile.Read(ref _decidingCount) > 0)
        {
            SettleStamps(wait: false, force: false);
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

            _demands.RemoveAll(d => d.Stale && d.Lane < 0 || d.Done && d.Kind is not (DemandKind.PreRoll or DemandKind.Hinted or DemandKind.Hover or DemandKind.Idle));
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
                    // A large cache keeps a wider neighbourhood (the super-cache), up to four times as far each way.
                    // Several videos share the room: five each taking its share would only take each other's frames away.
                    var stretch = Fit(source, (PreRollBefore + PreRollAfter) * Math.Clamp(_cache.BudgetBytes / (double)(3L << 30), 1, 4), PreRollShare / _videosShown);
                    // Several videos: nearly all of it ahead. A stretch is read from its start, and what play needs first would otherwise come last.
                    var behind = _videosShown > 1 ? 0.1 : PreRollBefore / (PreRollBefore + PreRollAfter);
                    var (before, after) = (stretch * behind, stretch * (1 - behind));
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
                return !_deciding.ContainsKey(id);
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
                    GpuSeekHintKind.TabRoll => (DemandKind.Idle, 0, true, seconds, seconds + Math.Min(span, Fit(source, HoverRollMost, HoverShare))),
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

    // The renderer's turn (5.2-Release, the user's Q1): pacing aside, a chase near the playhead runs at the card's full speed, and a
    // preview frame drawn meanwhile queued for the device's lock behind its decoder's calls and copies: 13-21 ms a frame where one
    // takes 0.3 (a 7950X3D and RX 7900 XTX desktop's first plays of a quick-cut project after opening it, AvSync session's trace), and the frame shown late.
    // So while a preview frame is drawn (from taking its output texture to the flush after it) a lane starts no call to its decoder
    // (an open, a seek, a frame): only the one under way when the draw began overlaps it. Exports and readbacks draw exact frames with
    // the lanes reading ahead beside them, never held.
    private volatile bool _drawing;

    /// <summary>The worker begins drawing a preview frame (true) or is done with it (false): the lanes hold back meanwhile.</summary>
    public void Drawing(bool drawing)
    {
        lock (_drawnGate)
        {
            _drawing = drawing;
            if (!drawing)
                Monitor.PulseAll(_drawnGate);
        }
    }

    /// <summary>Waits while a preview frame is drawn, 20 ms at most (a lane's own thread, before each call to its decoder).</summary>
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
        LookAtPower();
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

    private static bool IsHint(DemandKind kind) => kind is DemandKind.Hinted or DemandKind.Hover or DemandKind.Idle;

    // ----- Idle work (5.4.4: speculative work never competes with the tab in front) -----

    /// <summary>How long after the playhead last moved (a seek, a scrub's step, a frame played) idle work may start again, ms.</summary>
    internal const int IdleQuietMs = 400;

    // Until when (Stopwatch ticks) the playhead counts as moving: set by BeginFrame, read by the lanes.
    private long _activeUntil;

    /// <summary>
    /// Whether idle work (a tab's pre-roll, a drop's file: <see cref="DemandKind.Idle"/>) may take, open or go on with a decoder now: not
    /// <paramref name="playing"/>, the playhead still <see cref="IdleQuietMs"/> (<paramref name="now"/> past <paramref name="activeUntil"/>),
    /// no frame shown or played ahead still to decode (<paramref name="activePending"/>), and fewer idle reads than one lane on unified
    /// memory (two elsewhere) take (<paramref name="idleBusy"/>). Pure: the goldens check the rule itself.
    /// </summary>
    internal static bool IdleMayRun(bool playing, long now, long activeUntil, bool activePending, int idleBusy, bool unified) =>
        !playing && now >= activeUntil && !activePending && idleBusy < (unified ? 1 : 2);

    /// <summary><see cref="IdleMayRun"/> as the pool stands (under the lock); <paramref name="countLanes"/> false for a lane already on idle work.</summary>
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

    // ----- Cold play (5.5A, the user's Q4 b: the playhead's own frames first after a cold start) -----

    /// <summary>
    /// How long a cold play keeps far cuts off the lanes, seconds: after a tab switch or new sources (SyncSources), or a seek whose
    /// first picture missed a frame, the first second of playback reads only the playhead's stretch and the cuts within
    /// <see cref="CutSoon"/>; the cuts further ahead stay demands (no memory taken) and are read after it, earliest first. Every memory tier.
    /// </summary>
    internal const double ColdPlaySeconds = 1.0;

    // Armed by new sources or a cold seek, and the last frame's jump (worker thread only: SyncSources, BeginFrame).
    private bool _coldArmed, _jumpProbe;

    // Until when (Stopwatch ticks, 0 none) the cold play's window is open: written by BeginFrame, read by the lanes.
    private long _coldPlayUntil;

    /// <summary>
    /// Whether a cut at Sequence second <paramref name="cutAt"/> waits: <paramref name="playing"/>, <paramref name="now"/> inside the
    /// window (before <paramref name="coldUntil"/>), and more than <see cref="CutSoon"/> of real time ahead of <paramref name="playhead"/>
    /// at the play <paramref name="rate"/> (5.5A review: CutSoon x the rate in Sequence seconds, never less than CutSoon, so 2x keeps
    /// 1.5 s of real lead and a slow play holds no more than 1x). A NaN playhead or rate holds nothing back: never a stall on a value not
    /// known. Pure: the goldens check the rule itself.
    /// </summary>
    internal static bool ColdPlayHoldsBack(bool playing, long now, long coldUntil, double cutAt, double playhead, double rate) =>
        playing && now < coldUntil && cutAt - playhead > CutSoon * Math.Max(rate, 1);

    // A cut's entry held back by a cold play now (the lanes' thread, under _gate). Never in an exact-frame scene.
    private bool HeldForColdPlay(Demand demand)
    {
        if (demand.HintKind != GpuSeekHintKind.CutEntry || _exactOnly)
            return false;
        var until = Volatile.Read(ref _coldPlayUntil);
        return until != 0 && ColdPlayHoldsBack(_lastWasPlaying, Stopwatch.GetTimestamp(), until, demand.At, _lastPlayhead, Volatile.Read(ref _playRate));
    }

    /// <summary>The cold play's window (Stopwatch ticks, 0 none) and whether the next play is armed to open one. For the goldens.</summary>
    internal (long Until, bool Armed) ColdPlayForTests => (Volatile.Read(ref _coldPlayUntil), _coldArmed);

    // ----- The play rate (5.5A review: the cold play's lead kept in real time) -----

    /// <summary>How long a sample of the play rate runs, seconds: a speed changed while playing is caught within one.</summary>
    internal const double PlayRateSampleSeconds = 0.1;

    // The play rate: the playhead's pace over the clock's across consecutive playing frames (the scene carries no speed). 1 until a
    // play's first sample, and again after a pause, a jump or new sources. Written by BeginFrame, read by the lanes (HeldForColdPlay).
    private double _playRate = 1;

    // Where the current sample started (worker thread only: Stopwatch ticks, 0 none, and the playhead then), and new sources seen
    // by SyncSources (which BeginFrame, TakeHints and TrimSources call) that start it again.
    private long _rateFromTicks;
    private double _rateFromPlayhead;
    private bool _rateRestart;

    /// <summary>
    /// The play rate from the playhead's move over <paramref name="seconds"/> of the clock: 0.25 to 4 (a loop's wrap or a stall never
    /// gives a rate past them); 1 when either is not a number or no time passed. Pure: the goldens check it.
    /// </summary>
    internal static double PlayRateOf(double playheadMoved, double seconds)
    {
        var rate = playheadMoved / seconds;
        return seconds > 0 && double.IsFinite(rate) ? Math.Max(0.25, Math.Min(rate, 4)) : 1;
    }

    /// <summary>
    /// One frame's part of the play rate (the worker's thread, from BeginFrame; never for an exact-frame scene; the goldens call it
    /// with their own clock). A sample ends once <see cref="PlayRateSampleSeconds"/> have passed since it started (<paramref name="now"/>,
    /// Stopwatch ticks): the rate is the playhead's move over that stretch of the clock, and the next sample starts there.
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

    /// <summary>The play rate the cold play's lead uses now (1 until a play's first sample). For the goldens.</summary>
    internal double PlayRateForTests => Volatile.Read(ref _playRate);

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

            // One demand per frame of a source, whichever layers want it (a video, its Cutouts and its blur fill's twin
            // read one moment of one file): another layer's demand for the same frame is this one's too.
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
        _stampBack.Dispose();
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

        /// <summary>
        /// Work for what is not in front (5.4.4, GpuSeekHintKind.TabRoll: a project tab's pre-roll, a file opened ahead of a drop): after
        /// everything, and only while the player is quiet (DecoderPool.IdleMayRun).
        /// </summary>
        Idle = 5,
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

        /// <summary>Whether the frame it was last drawn in was drawn playing (5.4.4: a tab put behind keeps the frame after it too).</summary>
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
        /// The file's size and time written when it was registered, on a big card (default otherwise, and until it is read on the pool):
        /// what parking it keys by.
        /// </summary>
        public (long Length, long Written) Stamp;

        /// <summary>Whether its stamp is being read on the pool (under the gate).</summary>
        public bool Stamping;

        /// <summary>The parked serial it takes back when its stamp shows the file unchanged (while it waits in DecoderPool._deciding).</summary>
        public ParkedSource? Candidate;

        /// <summary>When it was registered (Stopwatch ticks): how long it has waited for its stamp.</summary>
        public long Registered;
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
