using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using HandPegApp.Compositor;

namespace HandPegApp.Services;

/// <summary>The frame cache's counters (<see cref="CompositorClient.CacheStats"/>): see <see cref="ControlBlock"/>'s Instant Seek part.</summary>
public readonly record struct FrameCacheStats(
    long Hits, long Misses, long ApproximatePresents, long Bytes, long BudgetBytes, int Frames,
    float LastExactLatencyMs, float WorstExactLatencyMs, int BusyDecodeThreads, long PredictedHits, long Evictions, bool PresentedExact);

/// <summary>
/// HandPeg's side of the GPU compositor (Pass 5.0-Core, 5.0A): it owns the control plane (CompositorSharedMemory), starts
/// the worker (HandPeg.exe --compositor-worker, through <see cref="WorkerProcess"/>: STARTF_FORCEOFFFEEDBACK, the
/// kill-on-close job) with a standby beside it, and watches them from a thread of its own.
/// <para>Scenes are written straight into the shared slot (<see cref="Scene"/> / <see cref="Layer"/>, then
/// <see cref="Publish"/>): a few microseconds, no copy, no allocation, no waiting for the worker. One thread writes
/// scenes (the writer's slot is that thread's alone); the supervisor never touches it.</para>
/// <para>Self-healing: when the worker ends unasked (a crash, or a TDR: <see cref="CompositorProtocol.DeviceLostExitCode"/>)
/// the standby takes over at once and draws the newest scene the triple buffer holds (published and not yet taken, or the
/// one the fallen worker held: neither is the writer's), and <see cref="EngineRecovered"/> is raised once its first
/// frame is shown (the title bar's "Preview engine recovered" badge). Never a dialog, never HandPeg's own fault.</para>
/// </summary>
public sealed class CompositorClient : IDisposable
{
    /// <summary>Raised (on the supervisor's thread) after the engine came back from a crash or a lost device: the time it took, ms.</summary>
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
    /// Starts the compositor on the card at <paramref name="adapter"/> (DXGI's order; -1 the high-performance card).
    /// With <paramref name="keepStandby"/> a second process waits to take over (the under-150-ms respawn).
    /// </summary>
    /// <param name="announcesRecovery">
    /// Raise <see cref="EngineRecovered"/> (the title bar's badge) after a recovery: the player's engine; an export's or a
    /// filmstrip's worker recovers quietly.
    /// </param>
    /// <param name="softwareDecode">
    /// Decode on the processor even where the card could (Settings' hardware video decoding off: the player and its
    /// filmstrips pass it; exports never do).
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
        _shared.Control.DecodeThreadsRequest = Math.Clamp(decodeThreads, 0, 4);
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

    /// <summary>How many times the engine has been brought back, and how long the last time took (ms, to its first frame).</summary>
    public int Recoveries { get; private set; }

    public double LastRecoveryMs { get; private set; }

    /// <summary>The worker's last message (its card, or what went wrong).</summary>
    public string Message => CompositorSharedMemory.ReadString(_shared.Control.Message);

    public int WorkerProcessId => _worker?.ProcessId ?? 0;

    private string WorkerArguments(int standby) => standby < 0
        ? string.Create(CultureInfo.InvariantCulture, $"{CompositorWorker.Argument} {BaseName} {_adapter}")
        : string.Create(CultureInfo.InvariantCulture, $"{CompositorWorker.Argument} {BaseName} {_adapter} --standby {standby}");

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

    /// <summary>Hands the scene to the worker and wakes it; returns its sequence number (to wait for with <see cref="WaitForFrame"/>).</summary>
    public long Publish()
    {
        var sequence = Interlocked.Increment(ref _sequence);
        _shared.SceneHeader(_shared.WriterSlot).FrameSequenceNumber = sequence;
        _shared.Publish();
        _wake.Set();
        return sequence;
    }

    /// <summary>Waits until the frame of <paramref name="sequence"/> (or a later one) has been presented.</summary>
    public bool WaitForFrame(long sequence, int milliseconds)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        while (Volatile.Read(ref _shared.Control.PresentedSequence) < sequence)
        {
            var left = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (left <= 0)
                return false;
            _presented.WaitOne(Math.Min(left, 50));
        }

        return true;
    }

    // ----- Instant Seek -----

    /// <summary>
    /// Leaves up to <see cref="CompositorProtocol.MaxHints"/> moments the worker should decode ahead (the pointer's moment
    /// over the timeline, where a scrub is heading, the next cut, the playhead), replacing the last ones, and wakes it. No
    /// frame is drawn for it; cheap enough for every pointer move. One thread at a time (the one that writes scenes).
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

    /// <summary>The frame cache's budget: 0 automatic, -1 off, else bytes (AppSettings.GpuFrameCacheMb). Read by the worker on its next turn.</summary>
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
    /// Makes sure the readback section holds at least <paramref name="bytes"/> (a frame of BGRA8: width x height x 4): a
    /// scene published with <see cref="FrameSceneHeader.FlagReadback"/> is then written into it by the worker.
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

    /// <summary>
    /// Waits up to <paramref name="milliseconds"/> for the frame of <paramref name="sequence"/> (published with
    /// <see cref="FrameSceneHeader.FlagReadback"/>) to be read back, then copies its BGRA8 rows into <paramref name="into"/>
    /// (at least width x height x 4). False on a timeout, or when <paramref name="into"/> is too small.
    /// </summary>
    public bool TryReadback(long sequence, Span<byte> into, out int width, out int height, int milliseconds)
    {
        (width, height) = (0, 0);
        if (_readback is null)
            return false;
        var deadline = Stopwatch.GetTimestamp() + (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        ref var control = ref _shared.Control;
        while (Volatile.Read(ref control.ReadbackSequence) < sequence)
        {
            var left = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (left <= 0)
                return false;
            _presented.WaitOne(Math.Min(left, 20));
        }

        (width, height) = (control.ReadbackWidth, control.ReadbackHeight);
        var bytes = (long)width * height * 4;
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

    /// <summary>Empties Instant Seek's frame cache (Settings' Clear): its video memory goes back at once.</summary>
    public void ClearFrameCache() => Send(CompositorCommand.ClearCache);

    /// <summary>Decode threads (0 automatic); read when a worker starts.</summary>
    public int DecodeThreads
    {
        get => Volatile.Read(ref _shared.Control.DecodeThreadsRequest);
        set => Volatile.Write(ref _shared.Control.DecodeThreadsRequest, Math.Clamp(value, 0, 4));
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
    /// The decoders' counters as the worker last wrote them (tests, tools): opens, seeks, frames decoded, frames stretched
    /// over a dropped frame, seeks made again after a late landing, moments with no frame.
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
    /// Puts a video file in the source table at <paramref name="id"/> (0-63), with its I-frame times when known (seconds,
    /// the file's own time, from MediaProbe's seek index): the worker opens a decoder for it when a layer first shows it.
    /// <paramref name="streamIndex"/> counts video streams only (0 the first).
    /// </summary>
    public void SetSource(int id, string path, int streamIndex = 0, IReadOnlyList<double>? iFrames = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, CompositorProtocol.MaxSources);
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
    /// Hands a texture to the worker at <paramref name="id"/> (0-63): a mask or still (BGRA8, straight alpha), a LUT
    /// (RGBA32F, size cubed, 2 to 256), or a curve (RGBA32F, 256 x 1). The pixels go into a section of their own, read once;
    /// the section of the generation before is let go.
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

        // The entry is written before its generation, which the worker reads first (and again after the pixels).
        Volatile.Write(ref entry.Generation, 0);
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

    /// <summary>Keeps the newest section of an entry; the one it replaces is let go (the worker never reads an old generation).</summary>
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

                    // Alive, but is it still turning? The worker beats on every turn of its loop and on every sample it
                    // decodes; one that stood still for 10 s (15 s while it starts) is hung in a driver: ended and taken over.
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
                if (++failuresInARow > 8)
                {
                    GiveUp();
                    return;
                }
                _stopping.WaitOne(1000);
            }
        }
    }

    /// <summary>True once the supervisor stopped bringing the engine back: the player stops, says why and starts again (MainWindow.OnGpuFaulted).</summary>
    public bool GaveUp => _gaveUp;

    private volatile bool _gaveUp;

    private void GiveUp()
    {
        _gaveUp = true;
        Volatile.Write(ref _shared.Control.WorkerState, (int)CompositorWorkerState.GaveUp);
    }

    /// <summary>Waits until a frame is presented after the count <paramref name="framesBefore"/> (any scene: a recovery's redraw).</summary>
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Volatile.Write(ref _shared.Control.Command, (int)CompositorCommand.Shutdown);
        _wake.Set();
        _stopping.Set();
        _supervisor.Join(2000);
        lock (_gate)
        {
            // The standby holds nothing: ended at once. The worker lets go of its device and textures first.
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
            _readbackView?.Dispose();
            _readback?.Dispose();
            (_readback, _readbackView) = (null, null);
        }

        _wake.Dispose();
        _presented.Dispose();
        _stopping.Dispose();
        _shared.Dispose();
    }
}
