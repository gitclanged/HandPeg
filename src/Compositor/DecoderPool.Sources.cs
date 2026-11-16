using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// DecoderPool source table: resync, retiring and parking sources, file stamps; below it, client-provided textures (ResourceCache).
internal sealed partial class DecoderPool
{
    // ----- Source table -----

    /// <summary>
    /// Re-reads the source table: changed entries lose their decoders and cached frames (with a large cache their frames stay and their
    /// serials are parked for re-registration of the same file). All released entries are retired before any new one is read, so a file
    /// that moved to another id in the same change (one tab deactivated, another showing it activated) reclaims its serial. No file I/O
    /// here: a new entry's stamp is read on the thread pool (ReadStamp), and one that may reclaim a parked serial waits outside the table
    /// (<see cref="SettleStamps"/>).
    /// </summary>
    private void Resync()
    {
        lock (_gate)
        {
            Span<int> generations = stackalloc int[CompositorProtocol.MaxSources];
            for (var id = 0; id < CompositorProtocol.MaxSources; id++)
            {
                generations[id] = Volatile.Read(ref _shared.Source(id).Generation);
                if (_sources.TryGetValue(id, out var known) && known.Generation != generations[id])
                {
                    Retire(known);
                    _sources.Remove(id);
                }

                // A registration released while still awaiting its stamp: return its candidate serial to the parked list.
                if (_deciding.TryGetValue(id, out var waiting) && waiting.Generation != generations[id])
                {
                    waiting.Retired = true;
                    if (waiting.Candidate is { } candidate)
                        Repark(candidate);
                    waiting.Candidate = null;
                    _deciding.Remove(id);
                    Volatile.Write(ref _decidingCount, _deciding.Count);
                }
            }

            for (var id = 0; id < CompositorProtocol.MaxSources; id++)
            {
                var generation = generations[id];
                if (generation == 0 || _sources.ContainsKey(id) || _deciding.ContainsKey(id))
                    continue;

                ref var entry = ref _shared.Source(id);
                var path = CompositorSharedMemory.ReadString(entry.Path);
                var (streamIndex, pathHash, iFrameCount) = (entry.StreamIndex, entry.PathHash, entry.IFrameCount);
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref entry.Generation) != generation)
                    continue;

                var source = new SourceState
                {
                    Generation = generation,
                    Path = path,
                    StreamIndex = streamIndex,
                    PathHash = pathHash,
                    IFrames = iFrameCount > 0 ? ReadIFrames(id, generation, iFrameCount) : null,
                    Registered = Stopwatch.GetTimestamp(),
                };

                // With a large cache, a recently released file (tab deactivated and reactivated) reclaims its serial and cached frames
                // once its pool-read stamp shows it unchanged; until then it waits outside the table. Also when the client requests lean
                // parking at all (unplugging later makes existing parks lean). Network-share files are never stamped, so never parked.
                var stamped = (_cache.BigCard || Lean || Volatile.Read(ref _shared.Control.TabParkingRequest) != 0) && _cache.Enabled && !IsNetworkPath(path);
                if (stamped && TakeParked(path, streamIndex) is { } candidate)
                {
                    source.Candidate = candidate;
                    _deciding[id] = source;
                    Volatile.Write(ref _decidingCount, _deciding.Count);
                }
                else
                {
                    source.Serial = ++_serials;
                    _sources[id] = source;
                }

                if (stamped)
                    ReadStamp(source);
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

        // Stamp still pending (released right after registration): not parked, forgotten. Lean parking (unified memory or battery) keeps
        // only the frames at its tab's playhead.
        if ((_cache.BigCard || Lean) && source.Stamp != default && !source.Failed)
            Park(source);
        else
            _cache.Forget(source.Serial);
    }

    // ----- Parked sources -----
    //
    // Deactivating a tab releases all its sources (GpuResourceBroker.ReleaseAll -> CompositorClient.ClearSource). Instead of forgetting a
    // retired source's frames (FrameCache.Forget), a large cache parks its serial keyed by file (case-insensitive path + video stream, the
    // GpuResourceBroker.SourceFor identity) and the file's size and write time at registration. Re-registering the unchanged file reclaims
    // the serial and every frame still cached; until then those frames are least recently used and evicted first. A file changed on disk,
    // or a serial pushed out by newer parks (ParkedKept), is forgotten. Decoders are disposed regardless; only cached frames (VRAM) stay.
    // Under the gate.
    //
    // Stamps are read on the thread pool, never under the gate or on the worker thread (a sleeping network share once held the gate for
    // seconds, stalling frames and the heartbeat until the client killed the worker). A new source's stamp is read at registration
    // (ReadStamp); one released before its stamp returns is forgotten. A file with a parked serial waits outside the table (_deciding:
    // excluded from frames drawn meanwhile) until its stamp returns, at most StampWaitMs on the worker thread while paused, and reclaims the
    // serial when the stamp matches; a stamp late past StampGraceMs, or an exact-frame scene that cannot wait, gets a fresh serial. Files on
    // network shares (UNC paths or mapped drives) are never stamped, so never parked.

    /// <summary>Maximum parked serials; the oldest is forgotten beyond this.</summary>
    private const int ParkedKept = 256;

    /// <summary>Maximum worker-thread wait for a reactivated tab's stamps while paused (ms).</summary>
    private const int StampWaitMs = 20;

    /// <summary>Maximum wait for a registration's stamp before it takes a fresh serial (ms).</summary>
    private const int StampGraceMs = 100;

    private readonly List<ParkedSource> _parked = [];

    /// <summary>
    /// A serial parked by file, with the playhead spans its tab showed when deactivated (kept by lean parks; full parks are trimmed to them
    /// on unplug) and whether only those frames are kept (lean).
    /// </summary>
    private sealed record ParkedSource(string Path, int Stream, (long Length, long Written) Stamp, int Serial)
    {
        public (double From, double To)[] Moments { get; init; } = [];
        public bool Lean { get; set; }
    }

    // Registrations awaiting stamps to reclaim a parked serial (by id), and their count (read without the gate).
    private readonly Dictionary<int, SourceState> _deciding = [];
    private int _decidingCount;

    // Signalled when a stamp returns; stamp count and last reader thread (goldens); goldens' simulated slow-file delay.
    private readonly AutoResetEvent _stampBack = new(false);
    private int _stampsRead;
    private int _stampReadOn;
    private int _stampDelayForTests;

    /// <summary>
    /// True for a UNC path (\\server\share, \\?\UNC\...), false for a local drive device path (\\?\C:\, \\.\C:\). Mapped network drives are
    /// detected on the pool (StampOf). Network files are never stamped, so never parked.
    /// </summary>
    internal static bool IsNetworkPath(string path)
    {
        static bool Separator(char c) => c is '\\' or '/';
        if (path.Length < 2 || !Separator(path[0]) || !Separator(path[1]))
            return false;
        return !(path.Length >= 6 && path[2] is '?' or '.' && Separator(path[3]) && char.IsAsciiLetter(path[4]) && path[5] == ':');
    }

    /// <summary>File size and write time (pool thread); default when unreadable or on a mapped network drive (then never parked).</summary>
    private static (long Length, long Written) StampOf(string path)
    {
        try
        {
            if (IsNetworkPath(path) || Path.GetPathRoot(path) is not { Length: > 0 } root || new DriveInfo(root).DriveType == DriveType.Network)
                return default;
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return default;
        }
    }

    /// <summary>Queues a new source's stamp read on the pool (called under the gate; only queues work) and settles it on return.</summary>
    private void ReadStamp(SourceState source)
    {
        source.Stamping = true;
        var path = source.Path;

        // A registration excluded from frames while waiting: wake the worker after the grace period (fresh serial then), even if the
        // stamp never returns and nothing else triggers a frame.
        if (source.Candidate is not null)
            _ = Task.Delay(StampGraceMs + 5).ContinueWith(_ => WakeIfDeciding(source), TaskScheduler.Default);

        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            var delay = Volatile.Read(ref _stampDelayForTests);
            if (delay > 0)
                Thread.Sleep(delay);
            var stamp = StampOf(path);
            Interlocked.Increment(ref _stampsRead);
            Volatile.Write(ref _stampReadOn, Environment.CurrentManagedThreadId);
            lock (_gate)
                (source.Stamp, source.Stamping) = (stamp, false);

            try
            {
                _stampBack.Set();
            }
            catch (ObjectDisposedException)
            {
                // Pool disposed meanwhile: nothing is waiting.
            }

            // A registration excluded from frames meanwhile: wake the worker to settle it and redraw.
            WakeIfDeciding(source);
        }, null);
    }

    /// <summary>Wakes the worker to redraw (FramesArrived) while <paramref name="source"/> is still unsettled.</summary>
    private void WakeIfDeciding(SourceState source)
    {
        lock (_gate)
        {
            if (source.Candidate is null || source.Retired)
                return;
        }

        try
        {
            _arrived.Set();
        }
        catch (ObjectDisposedException)
        {
            // Pool disposed meanwhile.
        }
    }

    /// <summary>
    /// Settles registrations awaiting stamps (worker thread): when <paramref name="wait"/>, first waits up to <see cref="StampWaitMs"/>
    /// outside the gate; then each returned stamp reclaims its parked serial if the file is unchanged (otherwise a fresh serial, parked
    /// frames forgotten), and each still pending past <see cref="StampGraceMs"/>, or all when <paramref name="force"/> (exact-frame scene),
    /// takes a fresh serial. Hints are re-read for settled sources.
    /// </summary>
    private void SettleStamps(bool wait, bool force)
    {
        if (Volatile.Read(ref _decidingCount) == 0)
            return;
        if (wait)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                lock (_gate)
                {
                    if (!_deciding.Values.Any(source => source.Stamping))
                        break;
                }

                var left = StampWaitMs - (int)clock.ElapsedMilliseconds;
                if (left <= 0)
                    break;
                _stampBack.WaitOne(left);
            }
        }

        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var settled = false;
            foreach (var (id, source) in _deciding.ToList())
            {
                if (source.Stamping && !force && Stopwatch.GetElapsedTime(source.Registered, now).TotalMilliseconds < StampGraceMs)
                    continue;
                var candidate = source.Candidate!;
                if (!source.Stamping && source.Stamp != default && source.Stamp == candidate.Stamp)
                {
                    // Frames kept at the tab's playhead (lean park) become ordinary cache entries again.
                    source.Serial = candidate.Serial;
                    _cache.Unkeep(candidate.Serial);
                }
                else
                {
                    // File rewritten (cached frames are stale) or stamp late: fresh serial.
                    _cache.Forget(candidate.Serial);
                    source.Serial = ++_serials;
                }

                source.Candidate = null;
                _sources[id] = source;
                _deciding.Remove(id);
                settled = true;
            }

            Volatile.Write(ref _decidingCount, _deciding.Count);

            // Hints read meanwhile excluded these sources (cut first frames, scrub direction): force a re-read.
            if (settled)
                _hintsVersion = -1;
        }
    }

    private void Park(SourceState source)
    {
        for (var i = _parked.Count - 1; i >= 0; i--)
        {
            if (_parked[i].Stream == source.StreamIndex && string.Equals(_parked[i].Path, source.Path, StringComparison.OrdinalIgnoreCase))
            {
                _cache.Forget(_parked[i].Serial);
                _parked.RemoveAt(i);
            }
        }

        // Lean: keep only frames at the tab's playhead and evict the rest now; with none there, nothing is parked.
        var moments = PlayheadMoments(source);
        var lean = Lean;
        if (lean && (moments.Length == 0 || _cache.KeepOnly(source.Serial, moments, MostKeptPerLayer) == 0))
        {
            _cache.Forget(source.Serial);
            return;
        }

        _parked.Add(new ParkedSource(source.Path, source.StreamIndex, source.Stamp, source.Serial) { Moments = moments, Lean = lean });
        if (_parked.Count > ParkedKept)
        {
            _cache.Forget(_parked[0].Serial);
            _parked.RemoveAt(0);
        }

        if (lean)
            HoldKeptToCap(source.Serial);
    }

    // ----- Lean parking (unified memory and battery: background tabs cost the active tab nothing) -----
    //
    // On unified memory or battery (requested by the player's client via ControlBlock.TabParkingRequest), a deactivated tab parks each file's
    // serial with only the frames its playhead showed: each layer's frame in the last drawn frame, plus the following frame when drawn while
    // playing (the paused playhead may land one frame on), at most three per layer. All other frames and its decoders are released at once,
    // so the reactivated tab finds its exact frame cached and nothing else is held. Kept frames are evicted last (FrameCache.KeepOnly) within
    // KeptCap, oldest parked tab first; a later budget drop evicts those beyond its share first (FrameCache.KeptShare), and a low-memory Clear
    // evicts them all. Discrete GPUs on AC park whole ranges.

    /// <summary>Maximum frames kept per layer at a tab's playhead.</summary>
    private const int MostKeptPerLayer = 3;

    /// <summary>
    /// Byte cap for frames kept at background tabs' playheads: FrameCache.KeptShare, max(96 MB, budget / 8), never above budget / 4.
    /// </summary>
    internal static long KeptCap(long budget) => FrameCache.KeptShare(budget);

    // Goldens' lean-parking override (LeanForTests): -1 none, 0 off, 1 lean.
    private int _leanForTests = -1;

    // On-battery state as last sampled by Tidy (every two seconds).
    private volatile bool _onBattery = PowerStatus.OnBattery;

    /// <summary>
    /// True when released sources park lean: the client requests it (<see cref="ControlBlock.TabParkingRequest"/> 1, the player) and the GPU
    /// uses unified memory or the PC is on battery; or always (2, goldens); or per the goldens' override.
    /// </summary>
    private bool Lean => Volatile.Read(ref _leanForTests) switch
    {
        1 => true,
        0 => false,
        _ => Volatile.Read(ref _shared.Control.TabParkingRequest) switch { 2 => true, 1 => _gpu.UnifiedMemory || _onBattery, _ => false },
    };

    /// <summary>Goldens: forces lean parking (true), disables it (false), or follows the client (null). Never called by HandPeg.</summary>
    internal void LeanForTests(bool? lean) => Volatile.Write(ref _leanForTests, lean is { } forced ? forced ? 1 : 0 : -1);

    /// <summary>
    /// Goldens: records the layers of source <paramref name="id"/> drawn in one new frame (as Resolve does) at their times, while
    /// <paramref name="playing"/> or paused. Never called by HandPeg.
    /// </summary>
    internal void ShownForTests(int id, bool playing, params (long ClipKey, double Seconds)[] layers)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out var source))
                return;
            _frame++;
            foreach (var (clipKey, seconds) in layers)
                _layers[clipKey] = new LayerMemory { Source = source.Serial, Media = seconds, Playhead = seconds, LastFrame = _frame, Playing = playing };
        }
    }

    /// <summary>
    /// File-time spans of a source shown at its tab's playhead (under the gate): for each of its layers in the last frame that drew it, its
    /// time (or the stand-in shown when its own frame is not cached), extended by one frame when drawn while playing.
    /// </summary>
    private (double From, double To)[] PlayheadMoments(SourceState source)
    {
        var last = long.MinValue;
        foreach (var memory in _layers.Values)
        {
            if (memory.Source == source.Serial)
                last = Math.Max(last, memory.LastFrame);
        }

        var frame = source.FrameDuration > 0 ? source.FrameDuration : 1 / 30.0;
        var moments = new List<(double From, double To)>();
        foreach (var memory in _layers.Values)
        {
            if (memory.Source != source.Serial || memory.LastFrame != last || double.IsNaN(memory.Media) || moments.Count >= 16)
                continue;
            var at = memory.Media;
            if (!_cache.Has(source.Serial, at) && memory.Shown is { } shown && shown.Source == source.Serial)
                at = shown.Time;
            moments.Add(memory.Playing ? (at, at + frame) : (at, at));
        }

        return [.. moments];
    }

    /// <summary>Enforces <see cref="KeptCap"/> on background-tab playhead frames, evicting the oldest parked tab first, never <paramref name="newest"/> (under the gate).</summary>
    private void HoldKeptToCap(int newest)
    {
        var cap = KeptCap(_cache.BudgetBytes);
        for (var i = 0; i < _parked.Count && _cache.KeptBytes > cap;)
        {
            if (_parked[i].Lean && _parked[i].Serial != newest)
            {
                _cache.Forget(_parked[i].Serial);
                _parked.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// On switching to battery (Tidy): trims every fully parked source (large cache on AC) to its playhead frames as a lean park would; those
    /// with none are forgotten (under the gate).
    /// </summary>
    private void TrimParkedToPlayheads()
    {
        for (var i = _parked.Count - 1; i >= 0; i--)
        {
            var parked = _parked[i];
            if (parked.Lean)
                continue;
            if (parked.Moments.Length == 0 || _cache.KeepOnly(parked.Serial, parked.Moments, MostKeptPerLayer) == 0)
            {
                _cache.Forget(parked.Serial);
                _parked.RemoveAt(i);
                continue;
            }

            parked.Lean = true;
        }

        HoldKeptToCap(0);
    }

    /// <summary>Samples battery state (Tidy, every two seconds); on an AC-to-battery switch, full parks are trimmed.</summary>
    private void LookAtPower()
    {
        var battery = PowerStatus.OnBattery;
        if (battery == _onBattery)
            return;
        lock (_gate)
        {
            var wasLean = Lean;
            _onBattery = battery;
            if (!wasLean && Lean)
                TrimParkedToPlayheads();
        }
    }

    /// <summary>Removes and returns the parked serial for this file and stream (stamp compared once the file's is read); null when none.</summary>
    private ParkedSource? TakeParked(string path, int stream)
    {
        for (var i = _parked.Count - 1; i >= 0; i--)
        {
            if (_parked[i].Stream != stream || !string.Equals(_parked[i].Path, path, StringComparison.OrdinalIgnoreCase))
                continue;
            var parked = _parked[i];
            _parked.RemoveAt(i);
            return parked;
        }

        return null;
    }

    /// <summary>Re-parks a candidate serial whose registration was released before settling, unless the same file was parked meanwhile.</summary>
    private void Repark(ParkedSource candidate)
    {
        if (_parked.Any(p => p.Stream == candidate.Stream && string.Equals(p.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)))
        {
            _cache.Forget(candidate.Serial);
            return;
        }

        _parked.Add(candidate);
        if (_parked.Count > ParkedKept)
        {
            _cache.Forget(_parked[0].Serial);
            _parked.RemoveAt(0);
        }
    }

    /// <summary>Goldens: serial of source <paramref name="id"/> as last read (0 when none).</summary>
    internal int SerialOf(int id)
    {
        lock (_gate)
            return _sources.TryGetValue(id, out var source) ? source.Serial : 0;
    }

    /// <summary>Goldens: number of parked serials.</summary>
    internal int ParkedCount
    {
        get
        {
            lock (_gate)
                return _parked.Count;
        }
    }

    /// <summary>Goldens: number of file stamps read and the managed thread id of the last read.</summary>
    internal (int Count, int Thread) StampsRead => (Volatile.Read(ref _stampsRead), Volatile.Read(ref _stampReadOn));

    /// <summary>Goldens: simulated slow file; each stamp read sleeps this long first (0 = none).</summary>
    internal void StampDelayForTests(int milliseconds) => Volatile.Write(ref _stampDelayForTests, milliseconds);

    /// <summary>Goldens: current demand kinds in order, comma-separated ("Idle" for a tab's pre-roll).</summary>
    internal string DemandKindsForTests()
    {
        lock (_gate)
            return string.Join(",", _demands.Select(demand => demand.Kind.ToString()));
    }

    /// <summary>
    /// Goldens: waits up to <paramref name="milliseconds"/> for pending stamp reads, then settles waiting registrations as the worker's next
    /// turn would. False when a read was still pending.
    /// </summary>
    internal bool StampsBackForTests(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                if (!_sources.Values.Concat(_deciding.Values).Any(source => source.Stamping))
                    break;
            }

            if (clock.ElapsedMilliseconds > milliseconds)
                return false;
            Thread.Sleep(2);
        }

        SettleStamps(wait: false, force: false);
        return true;
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
}

/// <summary>
/// Client-provided textures (masks, LUTs, curves, stills: ResourceEntry), uploaded once per generation from their shared sections and
/// kept until the entry changes.
/// </summary>
internal sealed unsafe class ResourceCache(GpuDevice gpu, CompositorSharedMemory shared) : IDisposable
{
    private readonly Dictionary<int, (int Generation, GpuTexture? Texture, nint Lut3D, nint LutSrv, int Size)> _loaded = [];

    // Generations whose GPU upload failed (e.g. a LUT above the device's maximum resource size): not retried every frame until the
    // client provides a new generation.
    private readonly Dictionary<int, int> _failed = [];
    private int _version = -1;

    /// <summary>Client resource table version: a cached 3D plane is redrawn when it changes.</summary>
    public int Version => Volatile.Read(ref shared.Control.ResourcesVersion);

    /// <summary>Applies client changes to the resource table; called once at frame start, before any lookup.</summary>
    public void BeginFrame() => Sync();

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

        foreach (var id in _failed.Keys.ToList())
        {
            if (Volatile.Read(ref shared.Resource(id).Generation) != _failed[id])
                _failed.Remove(id);
        }
    }

    public GpuTexture? Texture2D(int id)
    {
        if (id is < 0 or >= CompositorProtocol.MaxResources)
            return null;
        if (_loaded.TryGetValue(id, out var known))
            return known.Texture;
        Load(id);
        return _loaded.TryGetValue(id, out known) ? known.Texture : null;
    }

    public (nint Srv, int Size)? Lut(int id)
    {
        if (id is < 0 or >= CompositorProtocol.MaxResources)
            return null;
        if (!_loaded.ContainsKey(id))
            Load(id);
        return _loaded.TryGetValue(id, out var known) && known.LutSrv != 0 ? (known.LutSrv, known.Size) : null;
    }

    private void Load(int id)
    {
        ref var entry = ref shared.Resource(id);
        var generation = Volatile.Read(ref entry.Generation);
        var (kind0, width0, height0, depth0, bytes0) = ((GpuResourceKind)entry.Kind, entry.Width, entry.Height, entry.Depth, entry.Bytes);

        // Seqlock read: the copy above is valid only if the generation is unchanged after it (the client zeroes it while writing fields).
        // The full fence orders the field reads before the second read on ARM64. The section must hold every byte the texture reads.
        Interlocked.MemoryBarrier();
        if (generation == 0 || Volatile.Read(ref entry.Generation) != generation || (_failed.TryGetValue(id, out var failed) && failed == generation))
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
                    nint srv;
                    try
                    {
                        srv = gpu.Device.CreateShaderResourceView(texture);
                    }
                    catch
                    {
                        Com.Release(ref texture);
                        throw;
                    }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Section not created yet: retried next frame.
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            // Device rejected this generation (too large, unsupported format): not retried until the client provides another.
            _failed[id] = generation;
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
