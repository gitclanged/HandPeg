using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;
using Microsoft.Win32.SafeHandles;

namespace HandPegApp.Compositor;

/// <summary>
/// HandPeg.exe --compositor-worker &lt;base name&gt; &lt;adapter&gt; [--standby &lt;n&gt;] (Pass 5.0-Core, 5.0A): the GPU
/// compositor in a process of its own. A driver that falls over, a TDR or a decoder fault takes only it down; HandPeg
/// (CompositorClient) starts another and nothing is lost but a frame.
/// <para>It opens the client's control plane, makes its device, its shaders and three shared output textures, and then
/// renders whenever the client publishes a scene (or asks for a redraw): the newest scene only, never a queue. It ends
/// when the client ends, when told to, or when the device is lost (<see cref="CompositorProtocol.DeviceLostExitCode"/>).</para>
/// <para>A standby (--standby n) waits, holding nothing but a started runtime, until the client signals its take-over
/// event, then becomes the worker: the respawn after a crash costs a device and a frame, not a process start.</para>
/// </summary>
public static unsafe partial class CompositorWorker
{
    public const string Argument = "--compositor-worker";

    public static int Run(string[] args)
    {
        // A fault in a driver or a decoder ends this process quietly (no "stopped working" box), and a file on an empty
        // drive fails instead of asking for a disk: HandPeg sees the exit and takes over (SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX).
        _ = SetErrorMode(0x0001 | 0x0002);
        if (args.Length < 3)
            return CompositorProtocol.BadControlPlaneExitCode;
        var baseName = args[1];
        var adapter = int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : -1;
        var standbyAt = Array.IndexOf(args, "--standby");
        var standby = standbyAt > 0 && standbyAt + 1 < args.Length && int.TryParse(args[standbyAt + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

        using var shared = CompositorSharedMemory.Open(baseName);
        if (shared is null)
            return CompositorProtocol.BadControlPlaneExitCode;
        using var client = OpenProcess(Synchronize, false, shared.Control.ClientPid);
        if (client.IsInvalid)
            return 0;
        using var clientExit = new ProcessHandleWait(client);

        if (standby >= 0)
        {
            using var takeOver = new EventWaitHandle(false, EventResetMode.AutoReset, CompositorProtocol.TakeOverEvent(baseName, standby));
            if (WaitHandle.WaitAny([takeOver, clientExit]) != 0)
                return 0;
        }

        // Media Foundation and Direct3D want a multithreaded apartment; Main's thread is an STA.
        var exitCode = 0;
        var thread = new Thread(() => exitCode = Work(shared, adapter, clientExit)) { Name = "Compositor", IsBackground = false };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int Work(CompositorSharedMemory shared, int adapter, WaitHandle clientExit)
    {
        var clock = Stopwatch.StartNew();
        ref var control = ref shared.Control;
        control.WorkerPid = Environment.ProcessId;
        Volatile.Write(ref control.WorkerState, (int)CompositorWorkerState.Starting);

        GpuDevice? gpu = null;
        ShaderLibrary? shaders = null;
        DecoderPool? decoders = null;
        ResourceCache? resources = null;
        SceneRenderer? renderer = null;
        OutputRing? outputs = null;
        try
        {
            _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
            MfVideoDecoder.Beat = () => Interlocked.Increment(ref shared.Control.Heartbeat);
            using var wake = EventWaitHandle.OpenExisting(CompositorProtocol.WakeEvent(shared.BaseName));
            using var presented = EventWaitHandle.OpenExisting(CompositorProtocol.FrameEvent(shared.BaseName));

            gpu = GpuDevice.Create(adapter);
            shaders = new ShaderLibrary(gpu);
            decoders = new DecoderPool(gpu, shared);
            resources = new ResourceCache(gpu, shared);
            renderer = new SceneRenderer(gpu, shaders, decoders, resources);
            control.AdapterLuid = gpu.AdapterLuid;
            control.AdapterIndex = gpu.AdapterIndex;
            control.IsSoftwareDevice = gpu.IsWarp ? 1 : 0;
            Note(shared, $"{gpu.AdapterName}{(gpu.IsWarp ? " (WARP)" : "")}, {shaders.Count} shaders");
            Volatile.Write(ref control.WorkerState, (int)CompositorWorkerState.Ready);

            var redraw = true;
            var first = true;
            var budget = long.MinValue;
            WaitHandle[] waits = [wake, clientExit, decoders.FramesArrived];
            while (true)
            {
                // A decode lane stuck inside a driver for 8 s: the beat stops, and the client takes over (10 s).
                if (!decoders.LaneStuckFor(8))
                    Interlocked.Increment(ref control.Heartbeat);
                if (decoders.LaneFault is { } fault)
                    throw fault;
                switch ((CompositorCommand)Interlocked.Exchange(ref control.Command, 0))
                {
                    case CompositorCommand.SimulateDeviceLost:
                        throw new GpuException(Com.DeviceRemoved, "A simulated device removal");
                    case CompositorCommand.SimulateCrash:
                        Environment.Exit(unchecked((int)0xC0000005)); // as a crash ends it, without a report to Windows
                        break;
                    case CompositorCommand.Shutdown:
                        Volatile.Write(ref control.WorkerState, (int)CompositorWorkerState.Exiting);
                        return 0;
                    case CompositorCommand.Redraw:
                        redraw = true;
                        break;
                    case CompositorCommand.ClearCache:
                        decoders.Cache.Clear();
                        break;
                }

                // The client's frame cache budget and seek hints (Instant Seek): read when they change, no frame drawn for them.
                var requested = Volatile.Read(ref control.CacheBudgetRequest);
                if (requested != budget)
                    decoders.Cache.SetBudget(budget = requested);
                decoders.TakeHints();
                decoders.Tidy();

                var slot = shared.TakeLatest(out var isNew);
                ref var header = ref shared.SceneHeader(slot);
                if ((isNew || redraw) && header.FrameSequenceNumber > 0 && header.CanvasWidth > 0 && header.CanvasHeight > 0)
                {
                    redraw = false;
                    var started = Stopwatch.GetTimestamp();
                    RenderScene(shared, slot, gpu, renderer, decoders, ref outputs);
                    control.LastFrameCpuMs = (float)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    PublishCounters(ref control, decoders);
                    if (decoders.LastError.Length > 0)
                        Note(shared, decoders.LastError);
                    if (first)
                    {
                        control.StartupMs = (float)clock.Elapsed.TotalMilliseconds;
                        first = false;
                    }

                    presented.Set();
                }

                // The decoders' counters on every turn, drawn or not (a pre-roll decodes between frames; a reader of them
                // must not see them only when the next frame comes).
                control.DecoderOpens = decoders.Opens;
                control.DecoderSeeks = decoders.Seeks;
                control.DecodedFrames = decoders.FramesDecoded;
                (control.Stretches, control.LateLandings, control.MissingFrames) = (decoders.Stretches, decoders.LateLandings, decoders.MissingFrames);

                gpu.CheckAlive();
                var woke = WaitHandle.WaitAny(waits, 100);
                if (woke == 1)
                    return 0;

                // A frame the last picture lacked was decoded (Instant Seek): the same scene again, with it.
                if (woke == 2 && !decoders.LastFrameExact)
                    redraw = true;
            }
        }
        catch (GpuException ex) when (ex.IsDeviceLost)
        {
            control.LastError = ex.Code;
            Note(shared, ex.Message);
            Volatile.Write(ref control.WorkerState, (int)CompositorWorkerState.DeviceLost);
            return CompositorProtocol.DeviceLostExitCode;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            control.LastError = ex is GpuException gpuFault ? gpuFault.Code : ex.HResult;
            Note(shared, ex.Message);
            Volatile.Write(ref control.WorkerState, (int)CompositorWorkerState.Failed);
            return 1;
        }
        finally
        {
            Com.Release(ref _readbackStaging);
            _readbackView?.Dispose();
            _readbackFile?.Dispose();
            outputs?.Dispose();
            renderer?.Dispose();
            decoders?.Dispose();
            resources?.Dispose();
            shaders?.Dispose();
            MediaFoundation.ReleaseManager();
            gpu?.Dispose();
        }
    }

    /// <summary>The frame cache's counters, for diagnostics and tools\SeekBench.</summary>
    private static void PublishCounters(ref ControlBlock control, DecoderPool decoders)
    {
        control.PresentedExact = decoders.LastFrameExact ? 1 : 0;
        control.CacheHits = decoders.CacheHits;
        control.CacheMisses = decoders.CacheMisses;
        if (!decoders.LastFrameExact)
            control.ApproximatePresents++;
        control.CacheBytes = decoders.Cache.Bytes;
        control.CacheBudgetBytes = decoders.Cache.BudgetBytes;
        control.CachedFrames = decoders.Cache.Count;
        control.LastExactLatencyMs = decoders.LastExactLatencyMs;
        control.WorstExactLatencyMs = decoders.WorstExactLatencyMs;
        control.BusyDecodeThreads = decoders.BusyLanes;
        control.PredictedHits = decoders.PredictedHits;
        control.Evictions = decoders.Cache.Evictions;
    }

    private static void RenderScene(CompositorSharedMemory shared, int slot, GpuDevice gpu, SceneRenderer renderer, DecoderPool decoders, ref OutputRing? outputs)
    {
        ref var control = ref shared.Control;
        ref var header = ref shared.SceneHeader(slot);

        // Exports, filmstrips and tests draw only exact frames: every media layer's frame is asked for and waited on (up
        // to 20 s, beating) before the output is touched.
        if ((header.Flags & FrameSceneHeader.FlagExactFrames) != 0)
            WaitForExactFrames(shared, slot, decoders);
        var width = Math.Clamp(header.OutputWidth > 0 ? header.OutputWidth : header.CanvasWidth, 16, 8192);
        var height = Math.Clamp(header.OutputHeight > 0 ? header.OutputHeight : header.CanvasHeight, 16, 8192);
        if (outputs is null || outputs.Width != width || outputs.Height != height)
        {
            // A new ring is announced (its generation and size) only once a frame is drawn in it: a reader that opened it
            // before would copy a texture nothing was drawn into (a black flash on every resize).
            outputs?.Dispose();
            var generation = Math.Max(Volatile.Read(ref control.OutputGeneration), _lastGeneration) + 1;
            _lastGeneration = generation;
            outputs = new OutputRing(gpu, shared.BaseName, generation, width, height);
        }

        // The texture after the one shown last; a reader holding it (rarely, and briefly) makes it the next.
        var index = (Volatile.Read(ref control.PresentedIndex) + 1) % CompositorProtocol.OutputRing;
        var output = outputs[index];
        if (Dxgi.AcquireSync(output.KeyedMutex, 0, 50) != 0)
        {
            index = (index + 1) % CompositorProtocol.OutputRing;
            output = outputs[index];
            if (Dxgi.AcquireSync(output.KeyedMutex, 0, 50) != 0)
                return;
        }

        var count = Math.Clamp(header.ActiveLayerCount, 0, CompositorProtocol.MaxLayers);
        var layers = MemoryMarshal.CreateReadOnlySpan(ref shared.Layer(slot, 0), count);
        try
        {
            renderer.Render(header, layers, output.Texture.Rtv, width, height, raw: (header.Flags & FrameSceneHeader.FlagRawOutput) != 0);
        }
        finally
        {
            _ = Dxgi.ReleaseSync(output.KeyedMutex, 0);
        }

        gpu.Context.Flush();

        // Whether this frame showed every exact frame, set before its readback so a reader can check the very frame it got.
        control.PresentedExact = decoders.LastFrameExact ? 1 : 0;
        if ((header.Flags & FrameSceneHeader.FlagReadback) != 0)
            ReadBack(shared, gpu, output, width, height, header.FrameSequenceNumber);
        if ((header.Flags & FrameSceneHeader.FlagTimePasses) != 0 && (header.Flags & FrameSceneHeader.FlagWaitForTimings) != 0 && renderer.Timer.Collect())
        {
            for (var i = 0; i < 128; i++)
            {
                control.SlotGpuMs[i] = renderer.Timer.SlotMs[i];
                control.SlotRuns[i] = renderer.Timer.SlotRuns[i];
            }

            control.LastFrameGpuMs = renderer.Timer.FrameMs;
        }

        control.PresentedIndex = index;
        if (!outputs.Announced)
        {
            control.OutputWidth = width;
            control.OutputHeight = height;
            Volatile.Write(ref control.OutputGeneration, outputs.Generation);
            outputs.Announced = true;
        }

        Interlocked.Increment(ref control.FramesRendered);
        Volatile.Write(ref control.PresentedSequence, header.FrameSequenceNumber);
    }

    // The output generation made last (announced or not): names are never reused within a worker.
    private static int _lastGeneration;

    // ----- Readback (FlagReadback: exports, filmstrips) -----

    private static System.IO.MemoryMappedFiles.MemoryMappedFile? _readbackFile;
    private static System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _readbackView;
    private static int _readbackGeneration = -1;
    private static nint _readbackStaging;
    private static (int Width, int Height) _readbackSize;

    /// <summary>
    /// The frame just drawn into the client's readback section: copied to a staging texture, mapped (waiting for the card),
    /// written row by row, and its sequence number set last. A section too small, or not there, is skipped (logged once).
    /// </summary>
    private static void ReadBack(CompositorSharedMemory shared, GpuDevice gpu, (GpuTexture Texture, nint KeyedMutex) output, int width, int height, long sequence)
    {
        ref var control = ref shared.Control;
        var generation = Volatile.Read(ref control.ReadbackGeneration);
        var capacity = Volatile.Read(ref control.ReadbackCapacity);
        var needed = (long)width * height * 4;
        if (generation <= 0 || capacity < needed)
            return;

        try
        {
            if (generation != _readbackGeneration || _readbackView is null)
            {
                _readbackView?.Dispose();
                _readbackFile?.Dispose();
                _readbackFile = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(CompositorProtocol.ReadbackSection(shared.BaseName, generation), System.IO.MemoryMappedFiles.MemoryMappedFileRights.ReadWrite);
                _readbackView = _readbackFile.CreateViewAccessor(0, capacity, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
                _readbackGeneration = generation;
            }

            if (_readbackStaging == 0 || _readbackSize != (width, height))
            {
                Com.Release(ref _readbackStaging);
                _readbackStaging = gpu.CreateStaging(width, height, DxgiFormat.B8G8R8A8Unorm);
                _readbackSize = (width, height);
            }

            // The output texture is shared: copied out under its keyed mutex, as every other use of it.
            if (Dxgi.AcquireSync(output.KeyedMutex, 0, 100) != 0)
                return;
            try
            {
                gpu.Context.CopyResource(_readbackStaging, output.Texture.Texture);
            }
            finally
            {
                _ = Dxgi.ReleaseSync(output.KeyedMutex, 0);
            }

            Com.Check(gpu.Context.Map(_readbackStaging, 0, D3D11.MapRead, 0, out var mapped), "Map readback");
            byte* into = null;
            _readbackView.SafeMemoryMappedViewHandle.AcquirePointer(ref into);
            try
            {
                into += _readbackView.PointerOffset;
                var row = width * 4;
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(mapped.Data + (long)y * mapped.RowPitch, into + (long)y * row, row, row);
            }
            finally
            {
                _readbackView.SafeMemoryMappedViewHandle.ReleasePointer();
                gpu.Context.Unmap(_readbackStaging, 0);
            }

            (control.ReadbackWidth, control.ReadbackHeight, control.ReadbackRowBytes) = (width, height, width * 4);
            Volatile.Write(ref control.ReadbackSequence, sequence);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            Note(shared, "Readback: " + ex.Message);
        }
    }

    private static void WaitForExactFrames(CompositorSharedMemory shared, int slot, DecoderPool decoders)
    {
        ref var header = ref shared.SceneHeader(slot);
        var count = Math.Clamp(header.ActiveLayerCount, 0, CompositorProtocol.MaxLayers);
        var missing = false;
        for (var i = 0; i < count; i++)
        {
            ref var layer = ref shared.Layer(slot, i);
            if (layer.SourceKind == (int)GpuSourceKind.Media && (layer.Flags & GpuLayerDescriptor.FlagVisible) != 0)
                missing |= !decoders.Request(layer.SourceId, layer.MediaSeconds, layer.SourcePathHash, layer.ClipKey);
        }

        if (!missing)
            return;
        var baseName = shared;
        _ = decoders.WaitForExact(() =>
        {
            for (var i = 0; i < count; i++)
            {
                ref var layer = ref baseName.Layer(slot, i);
                if (layer.SourceKind == (int)GpuSourceKind.Media && (layer.Flags & GpuLayerDescriptor.FlagVisible) != 0 &&
                    !decoders.HasExact(layer.SourceId, layer.MediaSeconds))
                    return false;
            }

            return true;
        }, 20_000, () => Interlocked.Increment(ref baseName.Control.Heartbeat));
    }

    private static void Note(CompositorSharedMemory shared, string message) =>
        CompositorSharedMemory.WriteString(shared.Control.Message, message);

    /// <summary>The three shared output textures of one worker generation, each with its keyed mutex and its named handle.</summary>
    private sealed class OutputRing : IDisposable
    {
        private readonly (GpuTexture Texture, nint KeyedMutex, nint Handle)[] _ring = new (GpuTexture, nint, nint)[CompositorProtocol.OutputRing];

        public int Width { get; }
        public int Height { get; }
        public int Generation { get; }

        /// <summary>Its generation and size are in the control block (after its first frame).</summary>
        public bool Announced { get; set; }

        public OutputRing(GpuDevice gpu, string baseName, int generation, int width, int height)
        {
            (Width, Height, Generation) = (width, height, generation);
            for (var i = 0; i < _ring.Length; i++)
            {
                var texture = gpu.CreateTexture(width, height, DxgiFormat.B8G8R8A8Unorm, renderTarget: true, D3D11.MiscSharedNtHandle | D3D11.MiscSharedKeyedMutex);
                var handle = Dxgi.CreateSharedHandle(texture.Texture, CompositorProtocol.OutputTexture(baseName, generation, i));
                var mutex = Com.Query(texture.Texture, Iid.DxgiKeyedMutex);
                _ring[i] = (texture, mutex, handle);
            }
        }

        public (GpuTexture Texture, nint KeyedMutex) this[int index] => (_ring[index].Texture, _ring[index].KeyedMutex);

        public void Dispose()
        {
            for (var i = 0; i < _ring.Length; i++)
            {
                var (texture, mutex, handle) = _ring[i];
                Com.Release(ref mutex);
                if (handle != 0)
                    Dxgi.CloseHandle(handle);
                texture?.Dispose();
            }
        }
    }

    private sealed class ProcessHandleWait : WaitHandle
    {
        public ProcessHandleWait(SafeWaitHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    private const uint Synchronize = 0x00100000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll")]
    internal static partial uint SetErrorMode(uint mode);
}
