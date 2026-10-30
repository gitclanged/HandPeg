using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// A texture of the compositor's resource table as a plan refers to it: its id and the serial of the content the broker
/// gave that id. A plan draws with it only while the broker says that content is there (<see cref="GpuResourceBroker.StateOf"/>),
/// so a plan made before an id was given to something else never draws the wrong picture.
/// </summary>
public readonly record struct GpuResourceRef(int Id, int Serial)
{
    public static GpuResourceRef None => new(-1, 0);

    public bool IsNone => Id < 0;
}

/// <summary>Where a resource stands: being made, handed over, or not to be had (its file is gone or unreadable).</summary>
public enum GpuResourceState
{
    None,
    Pending,
    Ready,
    Failed,
}

/// <summary>
/// The GPU engine's tables on HandPeg's side (Pass 5.0-AudioAnd3D): one per <see cref="CompositorClient"/>, owning its 64
/// source ids and 64 resource ids. A file is put in the source table once (with the I-frame times HandPeg knows, in the
/// worker's time: 0 at the file's first decoded frame) and a texture is handed over once for each content (masks baked,
/// LUTs and curves made, stills read or drawn), found again by its content's hash. Both tables are least recently used:
/// an entry the newest plan uses is never let go, and one an older plan used is given to something new only when the
/// table is full (that plan stops drawing it: <see cref="StateOf"/>).
/// <para>Threads: everything here is the UI thread's (the thread it was made on) but <see cref="StateOf"/>, which the thread
/// that evaluates plans reads lock-free. Masks, LUTs and stills from files are made on a background lane (or on the calling
/// thread, for tests) and handed over by <see cref="Pump"/>, which is posted back to the thread the broker was made on as
/// each is done; <see cref="ResourcesChanged"/> then says a new frame may show more.</para>
/// </summary>
public sealed class GpuResourceBroker : IDisposable
{
    private readonly CompositorClient? _client;
    private readonly bool _bakeHere;
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

    // Read by the thread that evaluates plans: the serial handed over at each id (positive), its negative when it failed.
    private readonly int[] _handed = new int[CompositorProtocol.MaxResources];

    private readonly ConcurrentQueue<Baked> _baked = new();

    private readonly record struct Baked(int Id, int Serial, GpuResourceKind Kind, int Width, int Height, int Depth, byte[]? Pixels);

    private int _serial;
    private long _tick;
    private int _plan;
    private bool _toldFull;

    /// <param name="client">The compositor the tables are handed to; null keeps them here only (a plan made and evaluated without a worker: the golden tests).</param>
    /// <param name="bakeOnCallingThread">Make masks, LUTs and stills where they are asked for (the golden tests), not on a background lane.</param>
    public GpuResourceBroker(CompositorClient? client, bool bakeOnCallingThread = false)
    {
        _client = client;
        _bakeHere = bakeOnCallingThread;
        Text = new TextRasterizer(this);
    }

    public CompositorClient? Client => _client;

    /// <summary>Words drawn into stills: text clips and the captions (5.0D, <see cref="TextRasterizer"/>).</summary>
    public TextRasterizer Text { get; }

    /// <summary>Raised on the UI thread when textures that were being made have been handed over: a new frame shows them.</summary>
    public event Action? ResourcesChanged;

    /// <summary>
    /// Starts a plan: what it asks for from here on is its own and is not let go while it is the newest. (Called by
    /// <c>GpuScenePlan.Create</c>.)
    /// </summary>
    internal int BeginPlan() => ++_plan;

    // ----- Sources -----

    /// <summary>
    /// The source id of a file's <paramref name="videoStreamOrdinal"/>-th video stream (0 the first), put in the source
    /// table the first time it is asked for: -1 when the table cannot take it (64 files the newest plan all uses).
    /// </summary>
    public int SourceFor(string path, int videoStreamOrdinal)
    {
        var file = path.Trim().Trim('"');
        if (file.Length == 0)
            return -1;

        var key = string.Concat(file, "|", videoStreamOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_sourceIds.TryGetValue(key, out var id) && _sources[id] is { } known)
        {
            (known.LastUsed, known.Plan) = (++_tick, _plan);

            // Its I-frames came to be known since: given again, so the worker seeks exactly from now on.
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

    /// <summary>The path hash the source table holds for an id (what a layer names it by, <see cref="GpuLayerDescriptor.SourcePathHash"/>); 0 for none.</summary>
    public long PathHashOf(int sourceId) => sourceId is >= 0 and < CompositorProtocol.MaxSources && _sources[sourceId] is { } slot ? slot.PathHash : 0;

    /// <summary>
    /// A source's I-frames in the worker's time, when HandPeg knows them (null when it does not): the Overview Bar's
    /// pictures are taken on them (<see cref="GpuProgramStrip"/>), one decoded frame each.
    /// </summary>
    public double[]? IFramesOfSource(int sourceId) => sourceId is >= 0 and < CompositorProtocol.MaxSources && _sources[sourceId] is { } slot ? IFramesOf(slot.Path) : null;

    /// <summary>What a resource id holds now, by its recipe's hash (the same across brokers and sessions); 0 for nothing.</summary>
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
    /// A file's I-frame times in the worker's time (0 at its first decoded frame): HandPeg's list (MediaProbe, on the
    /// container's clock) less where its picture starts (the video stream's start time, which is the first frame's).
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

    /// <summary>Whether a resource a plan refers to is there to be drawn with (lock-free: the thread that evaluates plans).</summary>
    public GpuResourceState StateOf(GpuResourceRef resource)
    {
        if (resource.Id is < 0 or >= CompositorProtocol.MaxResources)
            return GpuResourceState.None;
        var handed = Volatile.Read(ref _handed[resource.Id]);
        return handed == resource.Serial ? GpuResourceState.Ready : handed == -resource.Serial ? GpuResourceState.Failed : GpuResourceState.Pending;
    }

    /// <summary>A mask baked from its recipe (<see cref="MaskBaker"/>), on a background lane the first time; the same recipe is the same texture.</summary>
    public GpuResourceRef Mask(in MaskRecipe recipe)
    {
        var copy = recipe;
        return Acquire(recipe.ContentHash(), recipe.FamilyHash(), GpuResourceKind.Mask2D,
            () => MaskBaker.Bake(copy) is { } pixels ? new Baked(0, 0, GpuResourceKind.Mask2D, copy.Width, copy.Height, 1, pixels) : default, background: true);
    }

    /// <summary>The newest ready mask of the same family as a recipe (an older size of it), or none: what stands in while a new bake is made.</summary>
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

    /// <summary>A .cube file as a 3D LUT; none when it cannot be read.</summary>
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

    /// <summary>The Curves card's table (its preset, its master points); none when it changes nothing.</summary>
    public GpuResourceRef Curve(string? preset, string? master)
    {
        if (GpuColorTables.ToneCurve(preset, master) is not { } table)
            return GpuResourceRef.None;
        var hash = default(GpuHash).Add("curve").Add(preset).Add(master).Value;
        return Acquire(hash, hash, GpuResourceKind.Curve1D, () => new Baked(0, 0, GpuResourceKind.Curve1D, 256, 1, 1, table), background: false);
    }

    /// <summary>A picture file (an image clip) as a still at its own size; none when it is not there.</summary>
    public GpuResourceRef StillFromFile(string path)
    {
        var file = path.Trim().Trim('"');
        var state = FileState.Get(file);
        if (file.Length == 0 || !state.Exists)
            return GpuResourceRef.None;
        var hash = default(GpuHash).Add("still-file").Add(file).Add(state.Length).Add(state.WrittenUtc.Ticks).Value;
        return Acquire(hash, hash, GpuResourceKind.Still2D,
            () => MaskBaker.ReadStill(file) is { } still ? new Baked(0, 0, GpuResourceKind.Still2D, still.Width, still.Height, 1, still.Pixels) : default, background: true);
    }

    /// <summary>
    /// A still made here (words drawn on the UI thread: <see cref="TextRasterizer"/>), BGRA8 straight alpha, by its content
    /// hash: <paramref name="draw"/> is only called when that content is not in the table already.
    /// </summary>
    /// <param name="pinned">
    /// Kept while the newest plan is (a text clip's); false for what the playhead's neighbourhood keeps (a caption): let go
    /// by its last use alone, so a long playback without an edit never fills the table with captions.
    /// </param>
    internal GpuResourceRef Still(long hash, Func<(byte[] Pixels, int Width, int Height)?> draw, bool pinned = true) =>
        Acquire(hash, hash, GpuResourceKind.Still2D, () => draw() is { } made ? new Baked(0, 0, GpuResourceKind.Still2D, made.Width, made.Height, 1, made.Pixels) : default,
            background: false, pinned);

    /// <summary>Whether a resource of this content is in the table already (made or being made).</summary>
    internal bool Has(long hash) => _resourceIds.ContainsKey(hash);

    /// <summary>Marks a resource as used by the newest plan (a still found again by a plan that did not make it).</summary>
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

        _ = WorkScheduler.Run(new WorkSpec("Preparing a picture for the GPU engine", WorkLane.Interactive) { Visible = false, Cancellable = false }, _ => Task.Run(() =>
        {
            _baked.Enqueue(Made(make) with { Id = id, Serial = serial });
            _home?.Post(static broker => ((GpuResourceBroker)broker!).Pump(), this);
            return true;
        }));
        return new GpuResourceRef(id, serial);
    }

    // What a maker made, or nothing (and a line in the log) when it threw.
    private static Baked Made(Func<Baked> make)
    {
        try
        {
            return make();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("A texture for the GPU engine could not be made", ex);
            return default;
        }
    }

    /// <summary>Hands over what the background lane has made since the last call (the UI thread: <c>GpuScenePlan.Create</c> and the orchestrator's frame tick).</summary>
    public void Pump()
    {
        if (_disposed)
            return;

        var any = false;
        while (_baked.TryDequeue(out var baked))
            any |= Complete(baked);
        if (any)
            ResourcesChanged?.Invoke();
    }

    /// <summary>Says that a new frame shows more (a caption drawn on the UI thread).</summary>
    internal void Changed() => ResourcesChanged?.Invoke();

    private bool Complete(Baked baked)
    {
        // Given to something else since it was asked for: dropped.
        if (_resources[baked.Id] is not { } slot || slot.Serial != baked.Serial)
            return false;

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

    // ----- The tables' room -----

    /// <summary>An empty slot, else the one used longest ago that the newest plan does not use; -1 when every one is the newest plan's.</summary>
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

    /// <summary>Lets go of what is still being made (it is dropped when done); the client's tables go with the client.</summary>
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
