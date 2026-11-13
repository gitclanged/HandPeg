using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// The source table of the DecoderPool: reading it again (Resync), retiring and parking sources, their file stamps, and
// below it the textures the client hands over (ResourceCache).
internal sealed partial class DecoderPool
{
    // ----- The source table -----

    /// <summary>
    /// Reads the source table again: changed entries lose their decoders and their cached frames (on a big card their frames stay, their
    /// serials parked for the same file registered again). Every entry let go of is retired before any new one is read, so a file that
    /// moved to another id in the same change (a tab put behind and another brought to the front that shows it too) takes its serial back.
    /// No file is read here (the 5.4D-TabPerf review's P13): a new entry's stamp is read on the pool (ReadStamp), and one that may take a
    /// parked serial back waits for it outside the table (<see cref="SettleStamps"/>).
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

                // A registration still waiting for its stamp, let go of already: its parked serial goes back where it was.
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
                var source = new SourceState
                {
                    Generation = generation,
                    Path = path,
                    StreamIndex = entry.StreamIndex,
                    PathHash = entry.PathHash,
                    IFrames = entry.IFrameCount > 0 ? ReadIFrames(id, generation, entry.IFrameCount) : null,
                    Registered = Stopwatch.GetTimestamp(),
                };

                // On a big card a file let go of a moment ago (a tab put behind and brought back) takes its serial back, and with it the
                // frames the cache still holds of it (the TabPerf follow-up, the user's X1), once its stamp, read on the pool, shows it
                // unchanged: until then it waits outside the table. A file on a network share is never stamped, so never parked.
                // 5.4.4: also whenever the client asks for lean parking at all (the charger pulled later makes its parks lean).
                var stamped = (_cache.BigCard || Lean || Volatile.Read(ref _shared.Control.TabParkingRequest) != 0) && _cache.Enabled && !IsNetworkPath(path);
                if (stamped && TakeParked(path, entry.StreamIndex) is { } candidate)
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

        // Its stamp still being read (let go of a moment after it was registered): not parked, forgotten as before. Lean (5.4.4: the player's
        // tabs on unified memory or on battery), only the frames at its tab's playhead stay parked.
        if ((_cache.BigCard || Lean) && source.Stamp != default && !source.Failed)
            Park(source);
        else
            _cache.Forget(source.Serial);
    }

    // ----- Parked sources (the TabPerf follow-up, the user's X1) -----
    //
    // A tab put behind lets go of every source (GpuResourceBroker.ReleaseAll -> CompositorClient.ClearSource), and a retired source's
    // frames used to be forgotten with it (FrameCache.Forget): the tab brought back registered its files again under new serials and
    // decoded everything anew. On a big card the cache keeps its frames across a tab put behind, so a retired source's serial is parked
    // instead: by its file (the path, case aside, and the video stream: the identity GpuResourceBroker.SourceFor keys by) and the file's
    // size and time written when it was registered. The same file registered again, unchanged, takes the serial back, and with it every
    // frame the cache still holds of it; until then those frames are the least recently used and go first as other files need room. A
    // file changed on disk meanwhile, or a parked serial pushed out by newer ones (ParkedKept), is forgotten as before. Its decoders are
    // disposed as before: only the frames, video memory, stay. Under the gate.
    //
    // The stamps are read on the pool, never under the gate nor on the worker's thread (the 5.4D-TabPerf review's P13: a file on a
    // sleeping network share held the gate for seconds, the worker drew nothing, its beat stopped, and the client killed it and its
    // replacements in turn). A new source's stamp is read as it is registered (ReadStamp), for its parking later; one let go of before
    // its stamp is back is forgotten. A file with a serial parked waits outside the table (_deciding: left out of the frames drawn
    // meanwhile) until its stamp is back, StampWaitMs at most on the worker's thread while paused, then takes the serial back when the
    // stamp matches; a stamp not back within StampGraceMs, or an exact-frame scene that cannot wait, takes a fresh serial. A file on a
    // network share (a UNC path, or a drive mapped to one) is never stamped, so never parked: forgotten when let go of, as before X1.

    /// <summary>The most serials parked; the oldest is forgotten beyond it.</summary>
    private const int ParkedKept = 256;

    /// <summary>The longest the worker's thread waits for the stamps of a tab brought back while paused (ms).</summary>
    private const int StampWaitMs = 20;

    /// <summary>The longest a registration waits for its stamp before it takes a fresh serial (ms).</summary>
    private const int StampGraceMs = 100;

    private readonly List<ParkedSource> _parked = [];

    /// <summary>
    /// A serial parked by its file: the moments its tab's playhead showed when it was put behind (5.4.4: the spans of file time a lean park
    /// keeps, and a full one is trimmed to when the charger is pulled), and whether only those frames are kept (lean).
    /// </summary>
    private sealed record ParkedSource(string Path, int Stream, (long Length, long Written) Stamp, int Serial)
    {
        public (double From, double To)[] Moments { get; init; } = [];
        public bool Lean { get; set; }
    }

    // Registrations waiting for their stamps to take a parked serial back (by id), and their count (read without the gate).
    private readonly Dictionary<int, SourceState> _deciding = [];
    private int _decidingCount;

    // A stamp back from the pool; how many stamps were read, and on which thread the last one was (the goldens); the goldens' slow file.
    private readonly AutoResetEvent _stampBack = new(false);
    private int _stampsRead;
    private int _stampReadOn;
    private int _stampDelayForTests;

    /// <summary>
    /// Whether a path is on a network share: a UNC path (\\server\share, \\?\UNC\...), not a local drive's device path (\\?\C:\,
    /// \\.\C:\). A drive letter mapped to a share is found on the pool (StampOf). Its files are never stamped, so never parked.
    /// </summary>
    internal static bool IsNetworkPath(string path)
    {
        static bool Separator(char c) => c is '\\' or '/';
        if (path.Length < 2 || !Separator(path[0]) || !Separator(path[1]))
            return false;
        return !(path.Length >= 6 && path[2] is '?' or '.' && Separator(path[3]) && char.IsAsciiLetter(path[4]) && path[5] == ':');
    }

    /// <summary>A file's size and time written, on the pool; default when it cannot be read, or is on a mapped network drive (never parked then).</summary>
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

    /// <summary>Reads a new source's stamp on the pool (under the gate: only the work is queued), and settles it when it is back.</summary>
    private void ReadStamp(SourceState source)
    {
        source.Stamping = true;
        var path = source.Path;

        // A registration left out of the frames while it waits: the worker draws them again once its grace is over (a fresh serial
        // then), even when the stamp never comes back and nothing else is drawn.
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
                // The pool was disposed meanwhile: nothing waits for it.
            }

            // A registration left out of the frames meanwhile: the worker draws them again, with it (it settles it first).
            WakeIfDeciding(source);
        }, null);
    }

    /// <summary>Wakes the worker to draw again (FramesArrived) while <paramref name="source"/> still waits to be settled.</summary>
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
            // The pool was disposed meanwhile.
        }
    }

    /// <summary>
    /// Settles the registrations waiting for their stamps (on the worker's thread): first waiting up to <see cref="StampWaitMs"/> for them
    /// when <paramref name="wait"/>, outside the gate; then each whose stamp is back takes its parked serial back when the file is unchanged
    /// (a fresh one, its parked frames forgotten, otherwise), and each still waiting past <see cref="StampGraceMs"/>, or every one when
    /// <paramref name="force"/> (an exact-frame scene), takes a fresh serial. The hints are read again for those settled.
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
                    // Its frames kept at the tab's playhead (lean, 5.4.4) are ordinary frames again.
                    source.Serial = candidate.Serial;
                    _cache.Unkeep(candidate.Serial);
                }
                else
                {
                    // Written again since (its frames are not this file's), or its stamp not back in time: a fresh serial.
                    _cache.Forget(candidate.Serial);
                    source.Serial = ++_serials;
                }

                source.Candidate = null;
                _sources[id] = source;
                _deciding.Remove(id);
                settled = true;
            }

            Volatile.Write(ref _decidingCount, _deciding.Count);

            // The hints read meanwhile left those sources out (their cuts' first frames, a scrub's heading): read again.
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

        // Lean (5.4.4): only the frames at its tab's playhead stay, every other one goes at once; none there, nothing is parked.
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

    // ----- Lean parking (5.4.4, the user's rule for unified memory and battery: tabs behind cost the front tab nothing) -----
    //
    // A tab put behind on unified memory or on battery (the player's client asks for it: ControlBlock.TabParkingRequest) parks each of its
    // files' serials with only the frames its playhead showed: the frame of each layer of the file in the last frame that drew it, and the
    // frame after it when that frame was drawn playing (the paused playhead the tab comes back to may be a frame on), never more than three a
    // layer. Every other frame goes at once and its decoders are disposed at once, so the tab brought back finds its exact frame in the cache
    // and nothing else of it is held. Those frames go last of all (FrameCache.KeepOnly), within KeptCap, the oldest parked tab first; a
    // budget that falls later lets those beyond its share go first (FrameCache.KeptShare), and a low-memory Clear lets them all go. A
    // discrete card on AC parks whole stretches as before (5.4.2A).

    /// <summary>The most frames a layer keeps at its tab's playhead.</summary>
    private const int MostKeptPerLayer = 3;

    /// <summary>
    /// What the frames kept at the playheads of tabs put behind may hold together (bytes): FrameCache.KeptShare, max(96 MB, an eighth of
    /// the budget) and never more than a quarter of it (5.4.4).
    /// </summary>
    internal static long KeptCap(long budget) => FrameCache.KeptShare(budget);

    // The goldens' word for lean parking (LeanForTests): -1 none, 0 not, 1 lean.
    private int _leanForTests = -1;

    // Whether the PC runs on its battery, as Tidy last looked (every two seconds).
    private volatile bool _onBattery = PowerStatus.OnBattery;

    /// <summary>
    /// Whether a source let go of is parked lean (5.4.4): the client asks for it (<see cref="ControlBlock.TabParkingRequest"/> 1, the player's)
    /// and the card shares the PC's memory or the PC is on battery; or it asks for it always (2, the goldens); or the goldens' word.
    /// </summary>
    private bool Lean => Volatile.Read(ref _leanForTests) switch
    {
        1 => true,
        0 => false,
        _ => Volatile.Read(ref _shared.Control.TabParkingRequest) switch { 2 => true, 1 => _gpu.UnifiedMemory || _onBattery, _ => false },
    };

    /// <summary>The goldens' word for lean parking: true lean, false not, null as the client asks. Never called by HandPeg.</summary>
    internal void LeanForTests(bool? lean) => Volatile.Write(ref _leanForTests, lean is { } forced ? forced ? 1 : 0 : -1);

    /// <summary>
    /// The goldens: the layers of the source at <paramref name="id"/> drawn in one new frame (as Resolve records them), at their moments,
    /// <paramref name="playing"/> or not. Never called by HandPeg.
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
    /// The moments of a source its tab's playhead showed (under the gate; spans of the file's time): for each layer of it in the last frame
    /// that drew it, its moment (the stand-in it showed when its own frame is not cached), with the next frame when it was drawn playing.
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

    /// <summary>The frames kept at the playheads of tabs put behind held to <see cref="KeptCap"/>: the oldest parked tab's first, never <paramref name="newest"/>'s (under the gate).</summary>
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
    /// The PC went on battery (Tidy, 5.4.4): every source parked whole (a big card on AC) is trimmed to its playhead's frames, as a lean park
    /// would have kept them; one with none there is forgotten (under the gate).
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

    /// <summary>Whether the PC is on battery now, looked at by Tidy every two seconds: from AC to battery, the whole parks are trimmed (5.4.4).</summary>
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

    /// <summary>The serial parked for this file and stream, taken out of the parked (its stamp compared once the file's is read); null for none.</summary>
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

    /// <summary>A serial taken out of the parked for a registration let go of before it was settled: parked again, unless the same file was parked meanwhile.</summary>
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

    /// <summary>The serial of the source at <paramref name="id"/> of the table as last read (0 for none): the goldens.</summary>
    internal int SerialOf(int id)
    {
        lock (_gate)
            return _sources.TryGetValue(id, out var source) ? source.Serial : 0;
    }

    /// <summary>How many serials are parked: the goldens.</summary>
    internal int ParkedCount
    {
        get
        {
            lock (_gate)
                return _parked.Count;
        }
    }

    /// <summary>How many file stamps were read, and the managed thread the last one was read on: the goldens.</summary>
    internal (int Count, int Thread) StampsRead => (Volatile.Read(ref _stampsRead), Volatile.Read(ref _stampReadOn));

    /// <summary>The goldens' slow file: each stamp read waits this long first (0 none).</summary>
    internal void StampDelayForTests(int milliseconds) => Volatile.Write(ref _stampDelayForTests, milliseconds);

    /// <summary>The goldens: the kinds of the demands there are now, in order, comma-separated (5.4.4: "Idle" for a tab's pre-roll).</summary>
    internal string DemandKindsForTests()
    {
        lock (_gate)
            return string.Join(",", _demands.Select(demand => demand.Kind.ToString()));
    }

    /// <summary>
    /// The goldens: waits until no stamp is being read (at most <paramref name="milliseconds"/>), then settles the registrations waiting for
    /// theirs as the worker's next turn would. False when one was still being read.
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
/// The textures the client hands over (masks, LUTs, curves, stills: ResourceEntry), uploaded once per generation from
/// their own sections, and kept until the entry changes.
/// </summary>
internal sealed unsafe class ResourceCache(GpuDevice gpu, CompositorSharedMemory shared) : IDisposable
{
    private readonly Dictionary<int, (int Generation, GpuTexture? Texture, nint Lut3D, nint LutSrv, int Size)> _loaded = [];
    private int _version = -1;

    /// <summary>The client's resource table's version: a 3D plane kept from an earlier frame is drawn again once it changes (5.4.1-M2).</summary>
    public int Version => Volatile.Read(ref shared.Control.ResourcesVersion);

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
