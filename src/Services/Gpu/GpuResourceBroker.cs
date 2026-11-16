using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// A compositor resource-table texture as a plan references it: id plus the serial of the content assigned to that id. A plan draws with it
/// only while the broker reports that content (<see cref="GpuResourceBroker.StateOf"/>), so a stale plan never draws a reassigned id.
/// </summary>
public readonly record struct GpuResourceRef(int Id, int Serial)
{
    public static GpuResourceRef None => new(-1, 0);

    public bool IsNone => Id < 0;
}

/// <summary>Resource state: pending, uploaded (ready), or failed (file missing or unreadable).</summary>
public enum GpuResourceState
{
    None,
    Pending,
    Ready,
    Failed,
}

/// <summary>
/// Editor-side mirror of the GPU engine's tables: one per <see cref="CompositorClient"/>, owning its 64 source ids and 64 resource ids. A
/// file enters the source table once (with known I-frame times in worker time: 0 at the first decoded frame) and each texture content is
/// uploaded once (baked masks, LUTs, curves, stills read or drawn), found again by content hash. Both tables are LRU: entries the newest
/// plan uses are never evicted; entries only older plans used are reassigned when the table is full (those plans stop drawing them:
/// <see cref="StateOf"/>).
/// <para>Threading: everything belongs to the owning (UI) thread except <see cref="StateOf"/>, which the plan-evaluating thread reads
/// lock-free. File-based masks, LUTs and stills are built on a background lane (or the calling thread in tests) and uploaded by
/// <see cref="Pump"/>, posted back to the owning thread as each completes; <see cref="ResourcesChanged"/> then signals a new frame.</para>
/// </summary>
public sealed class GpuResourceBroker : IDisposable
{
    private readonly CompositorClient? _client;
    private readonly bool _bakeHere;

    // Owning thread (first user of the broker); every mutating member checks it.
    private readonly ThreadOwner _owner = new();
    private readonly SynchronizationContext? _home = SynchronizationContext.Current;
    private volatile bool _disposed;

    // ----- Sources -----

    private sealed class SourceSlot
    {
        public required string Key { get; init; }
        public required string Path { get; init; }
        public required int Stream { get; init; }
        public required long PathHash { get; init; }
        public bool HasIFrames { get; set; }
        public long LastUsed { get; set; }
        public int Plan { get; set; }
    }

    private readonly SourceSlot?[] _sources = new SourceSlot?[CompositorProtocol.MaxSources];
    private readonly Dictionary<string, int> _sourceIds = new(StringComparer.OrdinalIgnoreCase);

    // ----- Resources -----

    private sealed class ResourceSlot
    {
        public required long Hash { get; init; }
        public required long Family { get; init; }
        public required int Serial { get; init; }
        public required GpuResourceKind Kind { get; init; }
        public long LastUsed { get; set; }
        public int Plan { get; set; }
        public GpuResourceState State { get; set; }
    }

    private readonly ResourceSlot?[] _resources = new ResourceSlot?[CompositorProtocol.MaxResources];
    private readonly Dictionary<long, int> _resourceIds = [];

    // Read by the plan-evaluating thread: the serial uploaded at each id (positive), negated on failure.
    private readonly int[] _handed = new int[CompositorProtocol.MaxResources];

    private readonly ConcurrentQueue<Baked> _baked = new();

    private readonly record struct Baked(int Id, int Serial, GpuResourceKind Kind, int Width, int Height, int Depth, byte[]? Pixels, Func<Baked>? NotMade = null);

    // At most two bakes at a time: resizing a masked layer requests a new size every frame (20 concurrent bakes of four full-frame arrays
    // each exhausted commit), and only what the newest plan uses is built when its turn comes.
    private readonly SemaphoreSlim _makers = new(2, 2);

    private int _serial;
    private long _tick;
    private int _plan;
    private bool _toldFull;

    /// <param name="client">Compositor the tables are mirrored to; null keeps them local (plans evaluated without a worker: goldens).</param>
    /// <param name="bakeOnCallingThread">Bake masks, LUTs and stills on the calling thread (goldens) instead of a background lane.</param>
    public GpuResourceBroker(CompositorClient? client, bool bakeOnCallingThread = false)
    {
        _client = client;
        _bakeHere = bakeOnCallingThread;
        Text = new TextRasterizer(this);
    }

    public CompositorClient? Client => _client;

    /// <summary>Text rendering into stills: text clips and captions (<see cref="TextRasterizer"/>).</summary>
    public TextRasterizer Text { get; }

    /// <summary>Raised on the UI thread when pending textures have been uploaded; a new frame will show them.</summary>
    public event Action? ResourcesChanged;

    /// <summary>
    /// Starts a plan: everything it requests from here on stays pinned while it is the newest plan. Called by <c>GpuScenePlan.Create</c>.
    /// </summary>
    internal int BeginPlan() => ++_plan;

    // ----- Sources -----

    /// <summary>
    /// Source id for a file's <paramref name="videoStreamOrdinal"/>-th video stream (0 = first), added on first request; -1 when the table is
    /// full (64 files all used by the newest plan).
    /// </summary>
    public int SourceFor(string path, int videoStreamOrdinal)
    {
        _owner.Check();
        var file = path.Trim().Trim('"');
        if (file.Length == 0)
            return -1;

        var key = string.Concat(file, "|", videoStreamOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_sourceIds.TryGetValue(key, out var id) && _sources[id] is { } known)
        {
            (known.LastUsed, known.Plan) = (++_tick, _plan);

            // I-frames became known since: resend them so the worker seeks exactly from now on.
            if (!known.HasIFrames && IFramesOf(file) is { Length: > 0 } late && Put(id, file, videoStreamOrdinal, late))
                known.HasIFrames = true;
            return id;
        }

        id = FreeSlot(_sources, s => s!.LastUsed, s => s!.Plan);
        if (id < 0)
        {
            TellFull("source");
            return -1;
        }

        if (_sources[id] is { } evicted)
        {
            _sourceIds.Remove(evicted.Key);
            _sources[id] = null;
        }

        var iFrames = IFramesOf(file);
        if (!Put(id, file, videoStreamOrdinal, iFrames))
            return -1;

        _sources[id] = new SourceSlot
        {
            Key = key, Path = file, Stream = videoStreamOrdinal, PathHash = CompositorSharedMemory.PathHash(file),
            HasIFrames = iFrames is { Length: > 0 }, LastUsed = ++_tick, Plan = _plan,
        };
        _sourceIds[key] = id;
        return id;
    }

    /// <summary>Path hash held for a source id (how layers name it, <see cref="GpuLayerDescriptor.SourcePathHash"/>); 0 when empty.</summary>
    public long PathHashOf(int sourceId) => sourceId is >= 0 and < CompositorProtocol.MaxSources && _sources[sourceId] is { } slot ? slot.PathHash : 0;

    /// <summary>
    /// A source's I-frames in worker time when known (null otherwise); the Sequence Overview Scrubber decodes one frame at each
    /// (<see cref="GpuProgramStrip"/>).
    /// </summary>
    public double[]? IFramesOfSource(int sourceId) => sourceId is >= 0 and < CompositorProtocol.MaxSources && _sources[sourceId] is { } slot ? IFramesOf(slot.Path) : null;

    /// <summary>Content hash held at a resource id (stable across brokers and sessions); 0 when empty.</summary>
    public long ContentOf(int id) => id is >= 0 and < CompositorProtocol.MaxResources && _resources[id] is { } slot ? slot.Hash : 0;

    private bool Put(int id, string file, int stream, double[]? iFrames)
    {
        try
        {
            _client?.SetSource(id, file, stream, iFrames);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            AppLog.Write($"The GPU engine's source table did not take {Path.GetFileName(file)}", ex);
            return false;
        }
    }

    /// <summary>
    /// A file's I-frame times in worker time (0 at the first decoded frame): MediaProbe's container-clock list minus the video stream start
    /// (the first frame's time).
    /// </summary>
    private static double[]? IFramesOf(string file)
    {
        if (MediaProbe.KnownIFrames(file) is not { Length: > 0 } raw)
            return null;
        var start = MediaProbe.KnownVideoStart(file) ?? raw[0];
        var times = new double[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            times[i] = Math.Max(raw[i] - start, 0);
        return times;
    }

    // ----- Resources -----

    /// <summary>State of a plan's resource reference (lock-free; plan-evaluating thread).</summary>
    public GpuResourceState StateOf(GpuResourceRef resource)
    {
        if (resource.Id is < 0 or >= CompositorProtocol.MaxResources)
            return GpuResourceState.None;
        var handed = Volatile.Read(ref _handed[resource.Id]);
        return handed == resource.Serial ? GpuResourceState.Ready : handed == -resource.Serial ? GpuResourceState.Failed : GpuResourceState.Pending;
    }

    /// <summary>Mask baked from a recipe (<see cref="MaskBaker"/>) on a background lane the first time; identical recipes share one texture.</summary>
    public GpuResourceRef Mask(in MaskRecipe recipe)
    {
        var copy = recipe;
        return Acquire(recipe.ContentHash(), recipe.FamilyHash(), GpuResourceKind.Mask2D,
            () => MaskBaker.Bake(copy) is { } pixels ? new Baked(0, 0, GpuResourceKind.Mask2D, copy.Width, copy.Height, 1, pixels) : default, background: true);
    }

    /// <summary>Newest ready mask of the recipe's family (an older size), or none: the stand-in while a new bake runs.</summary>
    public GpuResourceRef MaskStandIn(in MaskRecipe recipe)
    {
        var family = recipe.FamilyHash();
        var (best, bestTick) = (GpuResourceRef.None, -1L);
        for (var id = 0; id < _resources.Length; id++)
        {
            if (_resources[id] is { Kind: GpuResourceKind.Mask2D, State: GpuResourceState.Ready } slot && slot.Family == family && slot.LastUsed > bestTick)
                (best, bestTick) = (new GpuResourceRef(id, slot.Serial), slot.LastUsed);
        }

        if (!best.IsNone)
            (_resources[best.Id]!.LastUsed, _resources[best.Id]!.Plan) = (++_tick, _plan);
        return best;
    }

    /// <summary>A .cube file as a 3D LUT; none when unreadable.</summary>
    public GpuResourceRef Lut(string path)
    {
        var file = path.Trim().Trim('"');
        var state = FileState.Get(file);
        if (file.Length == 0 || !state.Exists)
            return GpuResourceRef.None;
        var hash = default(GpuHash).Add("lut").Add(file).Add(state.Length).Add(state.WrittenUtc.Ticks).Value;
        return Acquire(hash, hash, GpuResourceKind.Lut3D,
            () => GpuColorTables.ReadCube(file) is { } cube ? new Baked(0, 0, GpuResourceKind.Lut3D, cube.Size, cube.Size, cube.Size, cube.Pixels) : default, background: true);
    }

    /// <summary>Curves card table (preset, master points); none when it is the identity.</summary>
    public GpuResourceRef Curve(string? preset, string? master)
    {
        if (GpuColorTables.ToneCurve(preset, master) is not { } table)
            return GpuResourceRef.None;
        var hash = default(GpuHash).Add("curve").Add(preset).Add(master).Value;
        return Acquire(hash, hash, GpuResourceKind.Curve1D, () => new Baked(0, 0, GpuResourceKind.Curve1D, 256, 1, 1, table), background: false);
    }

    /// <summary>
    /// Color Grade curves (Master, Red, Green, Blue) as one table keyed by the four texts (baked only when new: a Color Studio curve drag
    /// bakes once per plan); none when every curve is the identity.
    /// </summary>
    public GpuResourceRef ColorCurves(string? master, string? red, string? green, string? blue)
    {
        if (GpuColorTables.CurveIsIdentity(master) && GpuColorTables.CurveIsIdentity(red) && GpuColorTables.CurveIsIdentity(green) && GpuColorTables.CurveIsIdentity(blue))
            return GpuResourceRef.None;
        var hash = default(GpuHash).Add("color-curves").Add(master).Add(red).Add(green).Add(blue).Value;
        return Acquire(hash, hash, GpuResourceKind.Curve1D,
            () => GpuColorTables.ColorCurves(master, red, green, blue) is { } table ? new Baked(0, 0, GpuResourceKind.Curve1D, 256, 1, 1, table) : default, background: false);
    }

    /// <summary>Picture file (image clip) as a still at native size; none when missing.</summary>
    public GpuResourceRef StillFromFile(string path)
    {
        _owner.Check();
        var file = path.Trim().Trim('"');
        var state = FileState.Get(file);
        if (file.Length == 0 || !state.Exists)
            return GpuResourceRef.None;
        var hash = default(GpuHash).Add("still-file").Add(file).Add(state.Length).Add(state.WrittenUtc.Ticks).Value;
        return Acquire(hash, hash, GpuResourceKind.Still2D,
            () => MaskBaker.ReadStill(file) is { } still ? new Baked(0, 0, GpuResourceKind.Still2D, still.Width, still.Height, 1, still.Pixels) : default, background: true);
    }

    /// <summary>
    /// Still rendered here (text drawn on the UI thread: <see cref="TextRasterizer"/>), BGRA8 straight alpha, keyed by content hash;
    /// <paramref name="draw"/> runs only when that content is not already in the table.
    /// </summary>
    /// <param name="pinned">
    /// Pinned while the newest plan is current (text clips); false for playhead-neighbourhood content (captions), evicted by recency alone so
    /// long playback without edits never fills the table with captions.
    /// </param>
    internal GpuResourceRef Still(long hash, Func<(byte[] Pixels, int Width, int Height)?> draw, bool pinned = true) =>
        Acquire(hash, hash, GpuResourceKind.Still2D, () => draw() is { } made ? new Baked(0, 0, GpuResourceKind.Still2D, made.Width, made.Height, 1, made.Pixels) : default,
            background: false, pinned);

    /// <summary>True when a resource with this content is in the table (ready or pending).</summary>
    internal bool Has(long hash) => _resourceIds.ContainsKey(hash);

    /// <summary>Marks a resource as used by the newest plan (a still found by a plan that did not create it).</summary>
    internal void Touch(GpuResourceRef resource, bool pinned = true)
    {
        if (resource.Id is >= 0 and < CompositorProtocol.MaxResources && _resources[resource.Id] is { } slot && slot.Serial == resource.Serial)
            (slot.LastUsed, slot.Plan) = (++_tick, pinned ? _plan : slot.Plan);
    }

    private GpuResourceRef Acquire(long hash, long family, GpuResourceKind kind, Func<Baked> make, bool background, bool pinned = true)
    {
        if (_resourceIds.TryGetValue(hash, out var known) && _resources[known] is { } slot && slot.Hash == hash)
        {
            (slot.LastUsed, slot.Plan) = (++_tick, pinned ? _plan : slot.Plan);
            return new GpuResourceRef(known, slot.Serial);
        }

        var id = FreeSlot(_resources, s => s!.LastUsed, s => s!.Plan);
        if (id < 0)
        {
            TellFull("resource");
            return GpuResourceRef.None;
        }

        if (_resources[id] is { } evicted)
            _resourceIds.Remove(evicted.Hash);

        var serial = ++_serial;
        _resources[id] = new ResourceSlot { Hash = hash, Family = family, Serial = serial, Kind = kind, LastUsed = ++_tick, Plan = pinned ? _plan : 0, State = GpuResourceState.Pending };
        _resourceIds[hash] = id;
        Volatile.Write(ref _handed[id], 0);

        if (!background || _bakeHere)
        {
            Complete(Made(make) with { Id = id, Serial = serial });
            return new GpuResourceRef(id, serial);
        }

        MakeInBackground(id, serial, kind, make);
        return new GpuResourceRef(id, serial);
    }

    private void MakeInBackground(int id, int serial, GpuResourceKind kind, Func<Baked> make) =>
        _ = WorkScheduler.Run(new WorkSpec("Preparing a picture for the GPU engine", WorkLane.Interactive) { Visible = false, Cancellable = false }, _ => Task.Run(async () =>
        {
            await _makers.WaitAsync();
            try
            {
                // Requested by a plan since replaced: skip the bake (Complete re-checks on the planning thread).
                var wanted = _resources[id] is { } waiting && waiting.Serial == serial && waiting.Plan == Volatile.Read(ref _plan);
                _baked.Enqueue(wanted ? Made(make) with { Id = id, Serial = serial } : new Baked(id, serial, kind, 0, 0, 0, null, make));
            }
            finally
            {
                _makers.Release();
            }

            _home?.Post(static broker => ((GpuResourceBroker)broker!).Pump(), this);
            return true;
        }));

    // Runs a maker; returns nothing (and logs) when it throws, out-of-memory included: a picture WIC could not decode under commit pressure
    // is simply not drawn, not a fault to report.
    private static Baked Made(Func<Baked> make)
    {
        try
        {
            return make();
        }
        catch (Exception ex)
        {
            AppLog.Write("A texture for the GPU engine could not be made", ex);
            return default;
        }
    }

    /// <summary>Uploads what the background lane has baked since the last call (UI thread: <c>GpuScenePlan.Create</c> and the frame tick).</summary>
    public void Pump()
    {
        _owner.Check();
        if (_disposed)
            return;

        var any = false;
        while (_baked.TryDequeue(out var baked))
            any |= Complete(baked);
        if (any)
            ResourcesChanged?.Invoke();
    }

    /// <summary>Signals that a new frame shows more (a caption drawn on the UI thread).</summary>
    internal void Changed() => ResourcesChanged?.Invoke();

    private bool Complete(Baked baked)
    {
        // Slot reassigned since the request: drop the result.
        if (_resources[baked.Id] is not { } slot || slot.Serial != baked.Serial)
            return false;

        // Skipped bake: free the slot, unless the newest plan wants it after all (then bake now).
        if (baked.NotMade is { } make)
        {
            if (slot.Plan == _plan)
            {
                MakeInBackground(baked.Id, baked.Serial, baked.Kind, make);
                return false;
            }

            _resources[baked.Id] = null;
            _resourceIds.Remove(slot.Hash);
            return false;
        }

        if (baked.Pixels is { } pixels)
        {
            try
            {
                _client?.SetResource(baked.Id, baked.Kind, baked.Width, baked.Height, baked.Depth, pixels);
                slot.State = GpuResourceState.Ready;
                Volatile.Write(ref _handed[baked.Id], baked.Serial);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                AppLog.Write("The GPU engine's resource table did not take a texture", ex);
            }
        }

        slot.State = GpuResourceState.Failed;
        Volatile.Write(ref _handed[baked.Id], -baked.Serial);
        return true;
    }

    /// <summary>
    /// Releases every source (UI thread; tab deactivated): each id is cleared in the worker's table (CompositorClient.ClearSource), whose
    /// decoder pool retires the source on its next turn (decoders disposed, cached frames dropped), freeing the ids for the next project.
    /// Textures are kept: the worker has no resource-clear command, ids are found again by content hash (a Game Preset's masks serve the
    /// next project too), and the table evicts by recency as it fills.
    /// </summary>
    public void ReleaseAll() => ReleaseAllBut(NoIds);

    private static readonly HashSet<int> NoIds = [];

    /// <summary>
    /// <see cref="ReleaseAll"/> except the ids in <paramref name="keep"/> (tab deactivated: sources named by the tab pre-roll stay resident with
    /// their decoders and frames for the tab being activated); <see cref="ReleaseKeptUnasked"/> later releases those not requested since.
    /// </summary>
    public void ReleaseAllBut(IReadOnlySet<int> keep)
    {
        _owner.Check();
        for (var id = 0; id < _sources.Length; id++)
        {
            if (_sources[id] is not { } slot || keep.Contains(id))
                continue;
            Release(id, slot);
        }

        _keptAcross = [.. keep.Where(id => id >= 0 && id < _sources.Length && _sources[id] is not null)];
        _keptAt = _tick;
    }

    // Sources kept by the last ReleaseAllBut and the table tick at that time; ReleaseKeptUnasked releases those not requested since.
    private int[] _keptAcross = [];
    private long _keptAt;

    /// <summary>
    /// Releases, once, the sources the last <see cref="ReleaseAllBut"/> kept that nothing has requested since (called by the first plan after
    /// a tab switch: pre-roll sources the plan does not use). Sources requested since stay: the plan's, and those a pre-roll or drop named.
    /// </summary>
    public void ReleaseKeptUnasked()
    {
        _owner.Check();
        foreach (var id in _keptAcross)
        {
            if (_sources[id] is { } slot && slot.LastUsed <= _keptAt)
                Release(id, slot);
        }

        _keptAcross = [];
    }

    // Releases one source: removed here and cleared in the worker's table.
    private void Release(int id, SourceSlot slot)
    {
        _sources[id] = null;
        _sourceIds.Remove(slot.Key);
        try
        {
            _client?.ClearSource(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            AppLog.Write("The GPU engine's source table could not let a file go", ex);
        }
    }

    // ----- Table capacity -----

    /// <summary>An empty slot, else the least recently used slot the newest plan does not use; -1 when every slot belongs to the newest plan.</summary>
    private int FreeSlot<T>(T?[] slots, Func<T?, long> lastUsed, Func<T?, int> plan) where T : class
    {
        var (best, oldest) = (-1, long.MaxValue);
        for (var id = 0; id < slots.Length; id++)
        {
            if (slots[id] is null)
                return id;
            if (plan(slots[id]) != _plan && lastUsed(slots[id]) < oldest)
                (best, oldest) = (id, lastUsed(slots[id]));
        }

        return best;
    }

    /// <summary>Drops pending bakes (discarded when they complete); the client's tables are disposed with the client.</summary>
    public void Dispose()
    {
        _disposed = true;
        _baked.Clear();
    }

    private void TellFull(string what)
    {
        if (_toldFull)
            return;
        _toldFull = true;
        AppLog.Write($"The GPU engine's {what} table is full ({CompositorProtocol.MaxResources} entries, all in use by the scene): what does not fit is not drawn.");
    }
}
