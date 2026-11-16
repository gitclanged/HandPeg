using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using HandPegApp.Compositor;

namespace HandPegApp.Services;

/// <summary>Frame cache counters (<see cref="CompositorClient.CacheStats"/>; see the Instant Seek section of <see cref="ControlBlock"/>).</summary>
public readonly record struct FrameCacheStats(
    long Hits, long Misses, long ApproximatePresents, long Bytes, long BudgetBytes, int Frames,
    float LastExactLatencyMs, float WorstExactLatencyMs, int BusyDecodeThreads, long PredictedHits, long Evictions, bool PresentedExact);

/// <summary>
/// Client side of the GPU compositor: owns the control plane (CompositorSharedMemory), launches the worker
/// (HandPeg.exe --compositor-worker via <see cref="WorkerProcess"/>: STARTF_FORCEOFFFEEDBACK, kill-on-close job) plus a standby, and
/// supervises them on a dedicated thread.
/// <para>Scenes are written in place (<see cref="Scene"/> / <see cref="Layer"/>, then <see cref="Publish"/>): microseconds, no copy,
/// no allocation, no waiting. A single thread owns the writer slot; the supervisor never touches it.</para>
/// <para>Self-healing: on unexpected worker exit (crash or TDR, <see cref="CompositorProtocol.DeviceLostExitCode"/>) the standby takes
/// over and renders the newest triple-buffer scene; <see cref="EngineRecovered"/> fires after its first frame (title-bar badge). No dialogs.</para>
/// </summary>
public sealed class CompositorClient : IDisposable
{
    /// <summary>Raised on the supervisor thread after recovery from a crash or device loss, with the recovery time (ms).</summary>
    public static event Action<double>? EngineRecovered;

    private readonly CompositorSharedMemory _shared;
    private readonly EventWaitHandle _wake;
    private readonly EventWaitHandle _presented;
    private readonly ManualResetEvent _stopping = new(false);
    private readonly Thread _supervisor;
    private readonly int _adapter;
    private readonly bool _keepStandby;
    private readonly Dictionary<string, MemoryMappedFile> _sections = [];
    private readonly int[] _sourceGenerations = new int[CompositorProtocol.MaxSources];
    private readonly int[] _resourceGenerations = new int[CompositorProtocol.MaxResources];
    private readonly object _gate = new();
    private WorkerProcess? _worker;
    private WorkerProcess? _standby;
    private int _standbyNumber;
    private EventWaitHandle? _standbyTakeOver;
    private EventWaitHandle? _signaledTakeOver;
    private long _sequence;
    private volatile bool _disposed;
    private readonly bool _announcesRecovery;

    /// <summary>
    /// Starts the compositor on DXGI adapter <paramref name="adapter"/> (-1 = high-performance). <paramref name="keepStandby"/> keeps a
    /// second process ready for sub-150 ms takeover.
    /// </summary>
    /// <param name="announcesRecovery">
    /// Raise <see cref="EngineRecovered"/> after recovery (player only; export and filmstrip workers recover silently).
    /// </param>
    /// <param name="softwareDecode">
    /// Force CPU decode (hardware decoding disabled; passed by the player and filmstrips, never by exports).
    /// </param>
    public CompositorClient(int adapter = -1, bool keepStandby = true, long frameCacheBudget = 0, int decodeThreads = 0, bool announcesRecovery = true, bool softwareDecode = false)
    {
        _adapter = adapter;
        _keepStandby = keepStandby;
        _announcesRecovery = announcesRecovery;
        var baseName = string.Create(CultureInfo.InvariantCulture, $"HandPeg.Compositor.{Environment.ProcessId}.{Guid.NewGuid():N}");
        _shared = CompositorSharedMemory.Create(baseName);

        // Read by the worker as it starts (Instant Seek's frame cache and decode lanes, and where it decodes).
        _shared.Control.CacheBudgetRequest = frameCacheBudget;
        _shared.Control.DecodeThreadsRequest = Math.Clamp(decodeThreads, 0, 8);
        _shared.Control.SoftwareDecodeRequest = softwareDecode ? 1 : 0;
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, CompositorProtocol.WakeEvent(baseName));
        _presented = new EventWaitHandle(false, EventResetMode.AutoReset, CompositorProtocol.FrameEvent(baseName));
        _worker = WorkerProcess.Start(WorkerArguments(standby: -1), readOutput: false);
        if (keepStandby)
            StartStandby();
        _supervisor = new Thread(Supervise) { Name = "Compositor supervisor", IsBackground = true };
        _supervisor.Start();
    }

    public string BaseName => _shared.BaseName;

    /// <summary>The control plane itself (diagnostics, tests, the frame reader).</summary>
    public CompositorSharedMemory Memory => _shared;

    public CompositorWorkerState State => (CompositorWorkerState)Volatile.Read(ref _shared.Control.WorkerState);

    /// <summary>Recovery count and last recovery time to first frame (ms).</summary>
    public int Recoveries { get; private set; }

    public double LastRecoveryMs { get; private set; }

    /// <summary>The worker's last message (its card, or what went wrong).</summary>
    public string Message => CompositorSharedMemory.ReadString(_shared.Control.Message);

    public int WorkerProcessId => _worker?.ProcessId ?? 0;

    private string WorkerArguments(int standby) => (standby < 0
        ? string.Create(CultureInfo.InvariantCulture, $"{CompositorWorker.Argument} {BaseName} {_adapter}")
        : string.Create(CultureInfo.InvariantCulture, $"{CompositorWorker.Argument} {BaseName} {_adapter} --standby {standby}")) + FfmpegArguments();

    /// <summary>
    /// FFmpeg/ffprobe paths (DependencyUpdater: custom, else managed) for streams Media Foundation cannot decode (FfmpegVideoDecoder).
    /// Re-read per worker launch so newly installed tools apply; existence is checked lazily by the worker.
    /// </summary>
    private static string FfmpegArguments()
    {
        var (ffmpeg, ffprobe) = (DependencyUpdater.FfmpegPath, DependencyUpdater.FfprobePath);
        return ffmpeg.Length == 0 || ffprobe.Length == 0 || ffmpeg.Contains('"') || ffprobe.Contains('"')
            ? ""
            : $" {CompositorWorker.FfmpegOption} \"{ffmpeg.TrimEnd('\\')}\" {CompositorWorker.FfprobeOption} \"{ffprobe.TrimEnd('\\')}\"";
    }

    private void StartStandby()
    {
        var number = ++_standbyNumber;
        _standbyTakeOver = new EventWaitHandle(false, EventResetMode.AutoReset, CompositorProtocol.TakeOverEvent(BaseName, number));
        _standby = WorkerProcess.Start(WorkerArguments(number), readOutput: false);
    }

    // ----- Scenes -----

    /// <summary>The header of the scene being written (the client's own slot until <see cref="Publish"/>).</summary>
    public ref FrameSceneHeader Scene => ref _shared.SceneHeader(_shared.WriterSlot);

    /// <summary>A layer of the scene being written.</summary>
    public ref GpuLayerDescriptor Layer(int index) => ref _shared.Layer(_shared.WriterSlot, index);

    /// <summary>Clears the scene being written (header and layers) to start a new one.</summary>
    public void ClearScene() => _shared.Scene(_shared.WriterSlot).Clear();

    /// <summary>Publishes the scene and wakes the worker; returns its sequence number for <see cref="WaitForFrame"/>.</summary>
    public long Publish()
    {
        var sequence = Interlocked.Increment(ref _sequence);
        _shared.SceneHeader(_shared.WriterSlot).FrameSequenceNumber = sequence;
        _shared.Publish();
        _wake.Set();
        return sequence;
    }

    /// <summary>
    /// Waits until <paramref name="sequence"/> or later is presented; false on timeout or when <paramref name="abort"/> is signalled
    /// (export cancel/failure, so the render thread stops within ms).
    /// </summary>
    public bool WaitForFrame(long sequence, int milliseconds, WaitHandle? abort = null)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        WaitHandle[]? both = abort is null ? null : [_presented, abort];
        while (Volatile.Read(ref _shared.Control.PresentedSequence) < sequence)
        {
            var left = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (left <= 0)
                return false;
            if (both is null)
                _presented.WaitOne(Math.Min(left, 50));
            else if (WaitHandle.WaitAny(both, Math.Min(left, 50)) == 1)
                return false;
        }

        return true;
    }

    // ----- Instant Seek -----

    /// <summary>
    /// Replaces up to <see cref="CompositorProtocol.MaxHints"/> pre-decode hints (hover, scrub target, next cut, playhead) and wakes the
    /// worker without rendering; cheap per pointer move. Scene-writer thread only.
    /// </summary>
    public void SetHints(ReadOnlySpan<GpuSeekHint> hints)
    {
        ref var control = ref _shared.Control;
        var count = Math.Min(hints.Length, CompositorProtocol.MaxHints);
        for (var i = 0; i < count; i++)
            control.Hints[i] = hints[i];
        Volatile.Write(ref control.HintCount, count);
        Interlocked.Increment(ref control.HintsVersion);
        _wake.Set();
    }

    /// <summary>
    /// Frame cache cap: 0 automatic (grow to hardware ceiling), -1 disabled, else bytes (AppSettings.GpuFrameCacheMb). Applied next worker turn.
    /// </summary>
    public long FrameCacheBudget
    {
        get => Volatile.Read(ref _shared.Control.CacheBudgetRequest);
        set
        {
            Volatile.Write(ref _shared.Control.CacheBudgetRequest, value);
            _wake.Set();
        }
    }

    // ----- Readback (GPU exports, filmstrips) -----

    private MemoryMappedFile? _readback;
    private MemoryMappedViewAccessor? _readbackView;
    private int _readbackGeneration;

    /// <summary>
    /// Ensures the readback section holds at least <paramref name="bytes"/> (BGRA8: width x height x 4) for
    /// <see cref="FrameSceneHeader.FlagReadback"/> scenes.
    /// </summary>
    public void EnsureReadback(long bytes)
    {
        if (_readback is not null && Volatile.Read(ref _shared.Control.ReadbackCapacity) >= bytes)
            return;
        var generation = ++_readbackGeneration;
        var section = MemoryMappedFile.CreateNew(CompositorProtocol.ReadbackSection(BaseName, generation), bytes);
        var (old, oldView) = (_readback, _readbackView);
        (_readback, _readbackView) = (section, section.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.Read));
        Volatile.Write(ref _shared.Control.ReadbackCapacity, bytes);
        Volatile.Write(ref _shared.Control.ReadbackGeneration, generation);
        oldView?.Dispose();
        old?.Dispose();
    }

    /// <summary>The longest a readback wait sleeps between two looks at the worker's count (and at its abort), ms.</summary>
    internal const int ReadbackSliceMs = 20;

    /// <summary>
    /// Waits up to <paramref name="milliseconds"/> for the readback of <paramref name="sequence"/> and copies it into <paramref name="into"/>:
    /// BGRA8, NV12 (<see cref="FrameSceneHeader.FlagReadbackNv12"/>, <see cref="CompositorProtocol.Nv12Bytes"/>) or P010
    /// (<see cref="FrameSceneHeader.FlagReadbackP010"/>, <see cref="CompositorProtocol.P010Bytes"/>). False on timeout or a short buffer.
    /// </summary>
    public bool TryReadback(long sequence, Span<byte> into, out int width, out int height, int milliseconds) =>
        TryReadback(sequence, into, out width, out height, milliseconds, abort: null);

    /// <summary>
    /// <see cref="TryReadback(long, Span{byte}, out int, out int, int)"/> that returns false once <paramref name="abort"/> (the player's stop
    /// event) is set, so a stopping pump exits within one slice and joins before the mapping is released (a long capture wait would
    /// otherwise outlive the join and read unmapped memory).
    /// </summary>
    public bool TryReadback(long sequence, Span<byte> into, out int width, out int height, int milliseconds, WaitHandle? abort)
    {
        (width, height) = (0, 0);
        if (_readback is null)
            return false;
        ref var control = ref _shared.Control;
        if (!WaitForSequence(ref control.ReadbackSequence, sequence, _presented, abort, milliseconds))
            return false;

        (width, height) = (control.ReadbackWidth, control.ReadbackHeight);
        // Row byte count identifies the format: BGRA8, P010 (2 bytes/sample) or NV12.
        var bytes = control.ReadbackRowBytes == width * 4 ? (long)width * height * 4
            : control.ReadbackRowBytes == width * 2 ? CompositorProtocol.P010Bytes(width, height) : CompositorProtocol.Nv12Bytes(width, height);
        if (bytes <= 0 || into.Length < bytes || _readbackView is not { } view || bytes > view.Capacity)
            return false;
        unsafe
        {
            byte* data = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref data);
            try
            {
                new ReadOnlySpan<byte>(data + view.PointerOffset, (int)bytes).CopyTo(into);
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        return true;
    }

    /// <summary>
    /// Waits for worker counter <paramref name="counter"/> (<see cref="ControlBlock.ReadbackSequence"/>) to reach <paramref name="target"/>,
    /// sleeping on <paramref name="signal"/> in slices of at most <see cref="ReadbackSliceMs"/>. False on timeout, or immediately when
    /// <paramref name="abort"/> is set (checked each slice and also waited on). Allocation-free (per-frame export/filmstrip path).
    /// </summary>
    internal static bool WaitForSequence(ref long counter, long target, WaitHandle signal, WaitHandle? abort, int milliseconds)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        var handles = abort is null ? null : new WaitHandle[] { signal, abort };
        while (Volatile.Read(ref counter) < target)
        {
            if (abort is not null && abort.WaitOne(0))
                return false;
            var left = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (left <= 0)
                return false;
            if (handles is null)
                signal.WaitOne(Math.Min(left, ReadbackSliceMs));
            else if (WaitHandle.WaitAny(handles, Math.Min(left, ReadbackSliceMs)) == 1)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Clears the Instant Seek cache and resets its budget (Settings Clear, project close). Tab deactivation uses <see cref="TrimWorker"/>
    /// instead. Counted request (survives later commands). Returns the request number (<see cref="ControlBlock.ClearsDone"/> reaches it).
    /// </summary>
    public int ClearFrameCache()
    {
        var request = Interlocked.Increment(ref _shared.Control.ClearRequests);
        _wake.Set();
        return request;
    }

    /// <summary>
    /// Requests a worker memory trim (tab deactivation, Settings Clear): cache free list, working and renderer textures, readback staging,
    /// driver allocations and heap; cached frames too on unified memory or small cards (CompositorWorker.TrimEmptiesCache). Served between
    /// frames, except the OS give-back (driver Trim, heap; CompositorWorker.GiveBack), which waits for 250 ms idle with nothing playing.
    /// Counted request; returns its number for <see cref="WaitForTrim"/>. A scene published afterwards keeps the working set for the
    /// active tab (released later by the idle trim).
    /// </summary>
    public int TrimWorker()
    {
        var request = Interlocked.Increment(ref _shared.Control.TrimRequests);
        _wake.Set();
        return request;
    }

    /// <summary>Waits up to <paramref name="milliseconds"/> for trim <paramref name="request"/> (<see cref="TrimWorker"/>); tests and tools.</summary>
    public bool WaitForTrim(int request, int milliseconds) => WaitForCount(ref _shared.Control.TrimsDone, request, milliseconds);

    /// <summary>Waits up to <paramref name="milliseconds"/> for clear <paramref name="request"/> (<see cref="ClearFrameCache"/>); tests and tools.</summary>
    public bool WaitForClear(int request, int milliseconds) => WaitForCount(ref _shared.Control.ClearsDone, request, milliseconds);

    private bool WaitForCount(ref int done, int request, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref done) - request < 0)
        {
            if (_disposed || clock.ElapsedMilliseconds > milliseconds)
                return false;
            Thread.Sleep(5);
        }

        return true;
    }

    /// <summary>Worker private bytes as last published (every 2 s and after trims; 0 initially).</summary>
    public long WorkerPrivateBytes => Volatile.Read(ref _shared.Control.WorkerPrivateBytes);

    /// <summary>Cache free-list bytes (excluded from <see cref="FrameCacheStats.Bytes"/>).</summary>
    public long CacheFreeBytes => Volatile.Read(ref _shared.Control.CacheFreeBytes);

    /// <summary>Current budget ceiling (bytes): hardware ceiling or Settings maximum, whichever is lower.</summary>
    public long CacheMaximumBytes => Volatile.Read(ref _shared.Control.CacheMaximumBytes);

    /// <summary>The hardware's ceiling now (bytes): what Auto grows to at most.</summary>
    public long CacheCeilingBytes => Volatile.Read(ref _shared.Control.CacheCeilingBytes);

    /// <summary>True for a unified-memory worker adapter (ControlBlock.UnifiedMemory); false until Ready.</summary>
    public bool UnifiedMemory => Volatile.Read(ref _shared.Control.UnifiedMemory) != 0;

    /// <summary>
    /// Parked-tab source policy (ControlBlock.TabParkingRequest): 0 keep all, 1 player (lean on unified memory or battery), 2 always lean (goldens).
    /// </summary>
    public int TabParking
    {
        get => Volatile.Read(ref _shared.Control.TabParkingRequest);
        set => Volatile.Write(ref _shared.Control.TabParkingRequest, value);
    }

    /// <summary>Decode threads (0 automatic); read when a worker starts.</summary>
    public int DecodeThreads
    {
        get => Volatile.Read(ref _shared.Control.DecodeThreadsRequest);
        set => Volatile.Write(ref _shared.Control.DecodeThreadsRequest, Math.Clamp(value, 0, 8));
    }

    /// <summary>The frame cache's counters as the worker last wrote them (Settings' readout, tools\SeekBench).</summary>
    public FrameCacheStats CacheStats
    {
        get
        {
            ref var c = ref _shared.Control;
            return new FrameCacheStats(c.CacheHits, c.CacheMisses, c.ApproximatePresents, c.CacheBytes, c.CacheBudgetBytes, c.CachedFrames,
                c.LastExactLatencyMs, c.WorstExactLatencyMs, c.BusyDecodeThreads, c.PredictedHits, c.Evictions, c.PresentedExact != 0);
        }
    }

    /// <summary>
    /// Decoder counters (tests, tools): opens, seeks, frames decoded, back-filled drops, re-seeks after overshoot, uncovered times.
    /// </summary>
    public (int Opens, int Seeks, int Decoded, int Stretches, int LateLandings, int Missing) DecodeCounters
    {
        get
        {
            ref var c = ref _shared.Control;
            return (Volatile.Read(ref c.DecoderOpens), Volatile.Read(ref c.DecoderSeeks), Volatile.Read(ref c.DecodedFrames),
                Volatile.Read(ref c.Stretches), Volatile.Read(ref c.LateLandings), Volatile.Read(ref c.MissingFrames));
        }
    }

    /// <summary>Leaves a command for the worker (tests of the recovery, a redraw) and wakes it.</summary>
    public void Send(CompositorCommand command)
    {
        Volatile.Write(ref _shared.Control.Command, (int)command);
        _wake.Set();
    }

    // ----- Sources and resources -----

    /// <summary>
    /// Registers a video in source-table slot <paramref name="id"/> (0-63) with optional I-frame times (file seconds, MediaProbe seek index);
    /// the worker opens a decoder lazily. <paramref name="streamIndex"/> counts video streams only.
    /// </summary>
    public void SetSource(int id, string path, int streamIndex = 0, IReadOnlyList<double>? iFrames = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, CompositorProtocol.MaxSources);
        if (path.Length >= CompositorProtocol.MaxPathChars)
            throw new System.IO.PathTooLongException(string.Create(CultureInfo.InvariantCulture, $"The GPU engine's source table holds paths of up to {CompositorProtocol.MaxPathChars - 1} characters, not {path.Length}."));
        ref var entry = ref _shared.Source(id);

        // Generations only ever grow, also across ClearSource: the worker can never mistake a new file for the old one.
        var generation = ++_sourceGenerations[id];
        var count = 0;
        MemoryMappedFile? section = null;
        if (iFrames is { Count: > 0 })
        {
            count = iFrames.Count;
            section = MemoryMappedFile.CreateNew(CompositorProtocol.IFrameSection(BaseName, id, generation), (long)count * sizeof(double));
            using var view = section.CreateViewAccessor();
            view.WriteArray(0, iFrames as double[] ?? [.. iFrames], 0, count);
        }

        // Seqlock write: set generation 0 (Interlocked.Exchange full fence, also on ARM64), write fields, then release-write the new
        // generation. The worker reads generation, fields, fence, generation again, and accepts only when both reads match.
        Interlocked.Exchange(ref entry.Generation, 0);
        entry.Kind = (int)GpuSourceKind.Media;
        entry.StreamIndex = streamIndex;
        entry.IFrameCount = count;
        entry.PathHash = CompositorSharedMemory.PathHash(path);
        CompositorSharedMemory.WriteString(entry.Path, path);
        Volatile.Write(ref entry.Generation, generation);
        Interlocked.Increment(ref _shared.Control.SourcesVersion);
        KeepSection(string.Create(CultureInfo.InvariantCulture, $"s{id}"), section);
    }

    public void ClearSource(int id)
    {
        Volatile.Write(ref _shared.Source(id).Generation, 0);
        Interlocked.Increment(ref _shared.Control.SourcesVersion);
        KeepSection(string.Create(CultureInfo.InvariantCulture, $"s{id}"), null);
    }

    /// <summary>
    /// Uploads a texture to resource slot <paramref name="id"/> (0-63): mask/still (BGRA8, straight alpha), LUT (RGBA32F, N^3, N 2-256) or
    /// curve (RGBA32F, 256 x 1). Pixels go into a new section read once; the previous generation's section is released.
    /// </summary>
    public void SetResource(int id, GpuResourceKind kind, int width, int height, int depth, ReadOnlySpan<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, CompositorProtocol.MaxResources);
        var needed = ResourceBytes(kind, width, height, depth);
        if (needed <= 0 || pixels.Length < needed)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A {kind} of {width} x {height} x {depth} needs {needed} bytes, not {pixels.Length}."), nameof(pixels));

        ref var entry = ref _shared.Resource(id);
        var generation = ++_resourceGenerations[id];
        var section = MemoryMappedFile.CreateNew(CompositorProtocol.ResourceSection(BaseName, id, generation), needed);
        using (var view = section.CreateViewAccessor())
        {
            unsafe
            {
                byte* data = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref data);
                try
                {
                    pixels[..(int)needed].CopyTo(new Span<byte>(data + view.PointerOffset, (int)needed));
                }
                finally
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        // The same seqlock write as SetSource: the generation 0 (a full fence) while the fields change, then the new one.
        Interlocked.Exchange(ref entry.Generation, 0);
        (entry.Kind, entry.Width, entry.Height, entry.Depth, entry.Bytes) = ((int)kind, width, height, depth, (int)needed);
        entry.ContentHash = 0;
        Volatile.Write(ref entry.Generation, generation);
        Interlocked.Increment(ref _shared.Control.ResourcesVersion);
        KeepSection(string.Create(CultureInfo.InvariantCulture, $"r{id}"), section);
    }

    /// <summary>The bytes a resource's texture reads; 0 for sizes out of bounds.</summary>
    public static long ResourceBytes(GpuResourceKind kind, int width, int height, int depth) => kind switch
    {
        GpuResourceKind.Mask2D or GpuResourceKind.Still2D when width is >= 1 and <= 16384 && height is >= 1 and <= 16384 => (long)width * height * 4,
        GpuResourceKind.Curve1D when width is >= 1 and <= 16384 && height is >= 1 and <= 16 => (long)width * height * 16,
        GpuResourceKind.Lut3D when width is >= 2 and <= 256 && height == width && depth == width => (long)width * width * width * 16,
        _ => 0,
    };

    /// <summary>Stores the newest section and releases the replaced one (the worker never reads stale generations).</summary>
    private void KeepSection(string key, MemoryMappedFile? section)
    {
        MemoryMappedFile? old;
        lock (_gate)
        {
            _sections.Remove(key, out old);
            if (section is not null)
                _sections[key] = section;
        }

        old?.Dispose();
    }

    // ----- Supervision -----

    private void Supervise()
    {
        var failuresInARow = 0;
        long lastBeat = -1;
        var beatSince = Stopwatch.GetTimestamp();
        var framesSeen = Volatile.Read(ref _shared.Control.FramesRendered);
        while (!_disposed)
        {
            try
            {
                var worker = _worker;
                if (worker is null)
                {
                    // Not running (a start that failed): tried again, as any other failure.
                    lock (_gate)
                        _worker = WorkerProcess.Start(WorkerArguments(standby: -1), readOutput: false);
                    continue;
                }

                using var exited = worker.ExitedHandle;
                var which = WaitHandle.WaitAny([exited, _stopping], 500);
                if (_disposed || which == 1)
                    return;

                if (which == WaitHandle.WaitTimeout)
                {
                    // Only a presented frame proves the engine well again: the failures in a row are counted until then.
                    var frames = Volatile.Read(ref _shared.Control.FramesRendered);
                    if (frames != framesSeen)
                        (framesSeen, failuresInARow) = (frames, 0);

                    // Hang detection: the heartbeat advances per loop and per decoded sample; 10 s stall (15 s during startup) means a
                    // driver hang, so kill and fail over.
                    var beat = Volatile.Read(ref _shared.Control.Heartbeat);
                    if (beat != lastBeat)
                        (lastBeat, beatSince) = (beat, Stopwatch.GetTimestamp());
                    else if (Stopwatch.GetElapsedTime(beatSince).TotalSeconds > (State == CompositorWorkerState.Ready ? 10 : 15))
                    {
                        AppLog.Write("Compositor: the worker stopped answering; it is ended and taken over.");
                        worker.Kill();
                        beatSince = Stopwatch.GetTimestamp();
                    }

                    continue;
                }

                // It ended. Bring the engine back.
                var clock = Stopwatch.StartNew();
                var code = worker.ExitCode;
                AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Compositor worker ended ({(code == CompositorProtocol.DeviceLostExitCode ? "device lost" : $"exit 0x{code:X}")}): {Message}"));
                lock (_gate)
                {
                    worker.Dispose();
                    _worker = null;
                }

                if (++failuresInARow > 8)
                {
                    AppLog.Write("Compositor: the engine failed too often in a row; not started again.");
                    GiveUp();
                    return;
                }

                // Back off when it keeps falling (a TDR takes a moment to settle), not after a single crash.
                if (failuresInARow > 2)
                    _stopping.WaitOne(Math.Min(100 * (1 << Math.Min(failuresInARow - 2, 4)), 2000));
                if (_disposed)
                    return;

                Volatile.Write(ref _shared.Control.WorkerState, (int)CompositorWorkerState.Starting);
                lock (_gate)
                {
                    if (_standby is { HasExited: false } standby && _standbyTakeOver is { } takeOver)
                    {
                        (_worker, _standby, _standbyTakeOver) = (standby, null, null);
                        takeOver.Set();

                        // Kept open until the next take-over: a standby still starting opens it later and finds it set.
                        _signaledTakeOver?.Dispose();
                        _signaledTakeOver = takeOver;
                    }
                    else
                    {
                        _standby?.Dispose();
                        _standby = null;
                        _worker = WorkerProcess.Start(WorkerArguments(standby: -1), readOutput: false);
                    }
                }

                // Closed during worker startup (Close waited on the gate): stop; the control plane may already be gone.
                if (_disposed)
                    return;

                // The newest scene again: the new worker draws it as it starts (and is told to, should it be ready already).
                var framesBefore = Volatile.Read(ref _shared.Control.FramesRendered);
                Send(CompositorCommand.Redraw);
                if (WaitForFramesAfter(framesBefore, 5000))
                {
                    Recoveries++;
                    LastRecoveryMs = clock.Elapsed.TotalMilliseconds;
                    AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Compositor recovered in {LastRecoveryMs:0.0} ms"));
                    if (_announcesRecovery)
                        EngineRecovered?.Invoke(LastRecoveryMs);
                }

                // A new standby, once the new worker has its first frame out (so the two do not compete for the card).
                if (_keepStandby && !_disposed)
                {
                    lock (_gate)
                        StartStandby();
                }

                (lastBeat, beatSince) = (-1, Stopwatch.GetTimestamp());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never out of this thread (it would end HandPeg): the engine is tried again after a moment.
                AppLog.Write("Compositor supervisor", ex);
                if (_disposed)
                    return;
                if (++failuresInARow > 8)
                {
                    GiveUp();
                    return;
                }
                _stopping.WaitOne(1000);
            }
        }
    }

    /// <summary>True once the supervisor gives up respawning (MainWindow.OnGpuFaulted stops and restarts the player).</summary>
    public bool GaveUp => _gaveUp;

    private volatile bool _gaveUp;

    private void GiveUp()
    {
        _gaveUp = true;
        Volatile.Write(ref _shared.Control.WorkerState, (int)CompositorWorkerState.GaveUp);
    }

    /// <summary>Waits until any frame is presented after count <paramref name="framesBefore"/> (e.g. a recovery redraw).</summary>
    private bool WaitForFramesAfter(int framesBefore, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref _shared.Control.FramesRendered) == framesBefore)
        {
            if (_disposed || clock.ElapsedMilliseconds > milliseconds)
                return false;
            _presented.WaitOne(20);
        }

        return true;
    }

    public void Dispose() => Close(keepMemory: false);

    /// <summary>
    /// Like <see cref="Dispose"/> (ends workers, releases sections) but keeps the control plane, readback section and events mapped for an
    /// owner thread still blocked on them (a pump that missed its join); unmapping would cause an access violation. Leaked until process exit.
    /// </summary>
    public void DisposeKeepingMemory() => Close(keepMemory: true);

    private void Close(bool keepMemory)
    {
        if (_disposed)
            return;
        _disposed = true;
        Volatile.Write(ref _shared.Control.Command, (int)CompositorCommand.Shutdown);
        _wake.Set();
        _stopping.Set();

        // A supervisor still in a slow CreateProcess will touch the control plane and events afterwards: end processes and sections,
        // but keep those mapped as DisposeKeepingMemory does.
        var supervisorStopped = ThreadStop.Join(_supervisor, 2000, "Compositor supervisor");
        keepMemory |= !supervisorStopped;
        lock (_gate)
        {
            // Standby holds no resources: kill immediately. The worker releases its device and textures first.
            _standby?.Kill();
            _standby?.Dispose();
            _standby = null;
            if (_worker is { } worker)
            {
                if (!worker.WaitForExit(500))
                    worker.Kill();
                worker.Dispose();
                _worker = null;
            }

            _standbyTakeOver?.Dispose();
            _signaledTakeOver?.Dispose();
            foreach (var section in _sections.Values)
                section.Dispose();
            _sections.Clear();
            if (!keepMemory)
            {
                _readbackView?.Dispose();
                _readback?.Dispose();
                (_readback, _readbackView) = (null, null);
            }
        }

        if (keepMemory)
            return;
        _wake.Dispose();
        _presented.Dispose();
        _stopping.Dispose();
        _shared.Dispose();
    }
}
