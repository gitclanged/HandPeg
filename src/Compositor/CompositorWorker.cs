using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;
using Microsoft.Win32.SafeHandles;

namespace HandPegApp.Compositor;

/// <summary>
/// HandPeg.exe --compositor-worker &lt;base name&gt; &lt;adapter&gt; [--standby &lt;n&gt;] [--ffmpeg &lt;path&gt; --ffprobe &lt;path&gt;] (Pass 5.0-Core, 5.0A; the FFmpeg the decoders fall back on, 5.2-Arm64AndNpu): the GPU
/// compositor in a process of its own. A driver that falls over, a TDR or a decoder fault takes only it down; HandPeg
/// (CompositorClient) starts another and nothing is lost but a frame.
/// <para>It opens the client's control plane, makes its device, its shaders and three shared output textures, and then
/// renders whenever the client publishes a scene (or asks for a redraw): the newest scene only, never a queue. It ends
/// when the client ends, when told to, or when the device is lost (<see cref="CompositorProtocol.DeviceLostExitCode"/>).</para>
/// <para>A standby (--standby n) waits, holding nothing but a started runtime, until the client signals its take-over
/// event, then becomes the worker: the respawn after a crash costs a device and a frame, not a process start.</para>
/// <para>It gives memory back (5.4D-TabPerf) when the client asks (a tab put behind: a counted trim, which on unified memory or a small
/// card empties the frame cache too) and by itself after standing idle for 30 s: <see cref="TrimMemory"/>, then <see cref="GiveBack"/>
/// (for an asked-for trim once nothing has been asked of it for a moment, so a run of quick tab switches pays for it once).</para>
/// </summary>
public static unsafe partial class CompositorWorker
{
    public const string Argument = "--compositor-worker";

    /// <summary>The options that name the FFmpeg and ffprobe the worker decodes with when Media Foundation cannot (each followed by a path).</summary>
    public const string FfmpegOption = "--ffmpeg", FfprobeOption = "--ffprobe";

    /// <summary>The word after <paramref name="option"/> on the command line; empty when it is not there.</summary>
    internal static string OptionValue(string[] args, string option)
    {
        var at = Array.IndexOf(args, option);
        return at > 0 && at + 1 < args.Length ? args[at + 1] : "";
    }

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

        // The FFmpeg HandPeg uses (DependencyUpdater, AppSettings.FfmpegPath), for the streams Media Foundation cannot decode
        // (FfmpegVideoDecoder, 5.2-Arm64AndNpu): looked for only when one of those is opened.
        FfmpegVideoDecoder.FfmpegPath = OptionValue(args, FfmpegOption);
        FfmpegVideoDecoder.FfprobePath = OptionValue(args, FfprobeOption);

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
        ExportLoopbackPreview? loopback = null;
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

            // The counted requests (5.4D-TabPerf): what was asked before this worker started is served already (it holds nothing).
            var clearsSeen = Volatile.Read(ref control.ClearRequests);
            Volatile.Write(ref control.ClearsDone, clearsSeen);
            var trimsSeen = Volatile.Read(ref control.TrimRequests);
            Volatile.Write(ref control.TrimsDone, trimsSeen);

            // Standing idle: the last time anything was asked of it (a scene, hints, playing, a lane decoding), and whether it has
            // given its memory back since. Its memory is published every 2 s (Settings' readout, the goldens).
            var hintsSeen = Volatile.Read(ref control.HintsVersion);
            var askedLast = Stopwatch.GetTimestamp();
            var idleTrimmed = false;
            var nextPublish = 0L;

            // An asked-for trim served but for its last part (GiveBack), which waits for the worker to be left alone a moment.
            var giveBackWaiting = false;
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
                        nextPublish = 0;
                        break;
                }

                // A clear, then a trim, as counted (5.4D-TabPerf): between frames, before the next scene is taken. The trim's echo is
                // written once its last part is done and its memory published (GiveBack, below), so a reader that sees it served reads
                // what it gave back.
                var clears = Volatile.Read(ref control.ClearRequests);
                if (clears != clearsSeen)
                {
                    clearsSeen = clears;
                    decoders.Cache.Clear();
                    Volatile.Write(ref control.ClearsDone, clears);
                    nextPublish = 0;
                }

                var trims = Volatile.Read(ref control.TrimRequests);
                if (trims != trimsSeen)
                {
                    trimsSeen = trims;
                    TrimMemory(ref control, gpu, decoders, renderer, asked: true);
                    (giveBackWaiting, idleTrimmed, nextPublish) = (true, true, Stopwatch.GetTimestamp() + PublishMemoryTicks);
                }

                // The client's frame cache budget and seek hints (Instant Seek): read when they change, no frame drawn for them.
                var requested = Volatile.Read(ref control.CacheBudgetRequest);
                if (requested != budget)
                    decoders.Cache.SetBudget(budget = requested);
                decoders.TakeHints();
                decoders.Tidy();
                decoders.Cache.CheckWindowsBudget(Stopwatch.GetTimestamp());

                var slot = shared.TakeLatest(out var isNew);
                ref var header = ref shared.SceneHeader(slot);
                if ((isNew || redraw) && header.FrameSequenceNumber > 0 && header.CanvasWidth > 0 && header.CanvasHeight > 0)
                {
                    redraw = false;
                    var started = Stopwatch.GetTimestamp();
                    RenderScene(shared, slot, gpu, renderer, decoders, ref outputs, ref loopback);
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

                // What a press of play waits for after the frame on screen (GpuPreviewEngine.PlayStartReady): looked at closely while it is short of them.
                var ahead = decoders.NextFramesReady();
                control.PresentedAhead = ahead ? 1 : 0;

                // Idle (5.4D-TabPerf, the user's U2): paused, with no scene, hint or decode for IdleTrimSeconds, the worker gives back
                // what the frames do not need (they stay: a seek on coming back is still instant), once, and draws the scene again so
                // its working textures are made now, while paused, not on the first frame played. Not while the Export Quality
                // Preview is on (its redraw is an encode).
                var now = Stopwatch.GetTimestamp();
                var hints = Volatile.Read(ref control.HintsVersion);
                if (isNew || hints != hintsSeen || decoders.Playing)
                    (hintsSeen, askedLast, idleTrimmed) = (hints, now, false);
                else if (decoders.BusyLanes > 0)
                    askedLast = now;
                if (IdleTrimDue(now, askedLast, idleTrimmed, loopback is not null))
                {
                    TrimMemory(ref control, gpu, decoders, renderer, asked: false);
                    GiveBack(ref control, gpu, decoders.Cache);
                    control.IdleTrims++;
                    (idleTrimmed, redraw, nextPublish) = (true, true, now + PublishMemoryTicks);
                }

                // An asked-for trim's last part (the TabPerf follow-up, the user's X1: never at the cost of quick switches between tabs):
                // the driver's kept memory and the heap given back once nothing has been asked of the worker for GiveBackQuietMs and
                // nothing plays. IDXGIDevice3::Trim takes tens of milliseconds (75 ms on the Adreno X1-85 after a 1080p tab), which every
                // switch of a quick run would pay, and the new tab's first frames behind it; a run of switches gives back once, when it
                // ends. Then the trim is echoed (every one asked for meanwhile).
                if (giveBackWaiting && GiveBackDue(now, askedLast, decoders.Playing))
                {
                    GiveBack(ref control, gpu, decoders.Cache);
                    Volatile.Write(ref control.TrimsDone, trimsSeen);
                    (giveBackWaiting, nextPublish) = (false, now + PublishMemoryTicks);
                }

                if (now >= nextPublish)
                {
                    PublishMemory(ref control, decoders.Cache);
                    nextPublish = now + PublishMemoryTicks;
                }

                gpu.CheckAlive();
                // (A paused Export Quality Preview frame still in the encoder: looked for again in 2 ms, and drawn when it is back.)
                var pending = loopback?.Pending == true && ExportLoopbackPreview.Wants(header);
                var woke = WaitHandle.WaitAny(waits, pending ? 2 : _held ? 16 : ahead || !decoders.Playing ? 100 : 4);
                if (woke == 1)
                    return 0;
                // Drawn again only once the paused frame came back from the encoder and was decoded, or was given up (5.4.1A: no longer
                // a full redraw every 2 ms, each with its wait for the encoder, while a wipe or a loupe was dragged).
                if (pending && loopback!.Poll())
                    redraw = true;

                // A frame the last picture lacked was decoded (Instant Seek): the same scene again, with it.
                if ((woke == 2 && !decoders.LastFrameExact) || _held)
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
            loopback?.Dispose();
            _p010Picture?.Dispose();
            ReleaseReadback();
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
        PublishCache(ref control, decoders.Cache);
        control.LastExactLatencyMs = decoders.LastExactLatencyMs;
        control.WorstExactLatencyMs = decoders.WorstExactLatencyMs;
        control.BusyDecodeThreads = decoders.BusyLanes;
        control.PredictedHits = decoders.PredictedHits;
        control.Evictions = decoders.Cache.Evictions;
    }

    // ----- Giving memory back (5.4D-TabPerf, the user's U2: the computer's memory matters most) -----

    /// <summary>How long the worker stands idle (paused, nothing asked of it) before it gives back what the frames do not need, seconds.</summary>
    internal const int IdleTrimSeconds = 30;

    /// <summary>
    /// Whether the worker gives its memory back now for standing idle: nothing asked of it (no scene, hint, playing or decode) for more
    /// than <see cref="IdleTrimSeconds"/> since <paramref name="askedLast"/> (Stopwatch ticks), not trimmed since, and the Export Quality
    /// Preview off (<paramref name="loopbackOn"/>: the redraw after the trim would be an encode).
    /// </summary>
    internal static bool IdleTrimDue(long now, long askedLast, bool idleTrimmed, bool loopbackOn) =>
        !idleTrimmed && !loopbackOn && now - askedLast > IdleTrimSeconds * Stopwatch.Frequency;

    /// <summary>How long nothing is asked of the worker before an asked-for trim's last part (<see cref="GiveBack"/>) runs, ms.</summary>
    internal const int GiveBackQuietMs = 250;

    /// <summary>
    /// Whether an asked-for trim's last part runs now: nothing asked of the worker (no scene, hint or decode) for <see cref="GiveBackQuietMs"/>
    /// since <paramref name="askedLast"/> (Stopwatch ticks), and nothing <paramref name="playing"/> (a blocking pass at play would make a
    /// frame late).
    /// </summary>
    internal static bool GiveBackDue(long now, long askedLast, bool playing) =>
        !playing && now - askedLast >= GiveBackQuietMs * Stopwatch.Frequency / 1000;

    // How often the worker's memory is published while nothing else publishes it (Stopwatch ticks: 2 s).
    private static readonly long PublishMemoryTicks = 2 * Stopwatch.Frequency;

    /// <summary>What the frame cache holds and may hold (bytes, frames, the adaptive budget's maximum, ceiling and steps).</summary>
    private static void PublishCache(ref ControlBlock control, FrameCache cache)
    {
        control.CacheBytes = cache.Bytes;
        control.CacheBudgetBytes = cache.BudgetBytes;
        control.CachedFrames = cache.Count;
        control.CacheFreeBytes = cache.FreeBytes;
        control.CacheMaximumBytes = cache.MaximumBytes;
        control.CacheCeilingBytes = cache.CeilingBytes;
        control.CacheGrowSteps = cache.GrowSteps;
    }

    /// <summary>The worker's private bytes and the cache's sizes, for a reader between frames (every 2 s, and after a clear or a trim).</summary>
    private static void PublishMemory(ref ControlBlock control, FrameCache cache)
    {
        PublishCache(ref control, cache);
        Volatile.Write(ref control.WorkerPrivateBytes, PrivateBytes());
    }

    /// <summary>
    /// Whether a trim empties the frame cache (the TabPerf follow-up, the user's X1): one the client <paramref name="asked"/> for (a tab put
    /// behind, Settings' Clear) does, but on a big card (FrameCache.BigCard: discrete, more than 4 GB of its own), where the frames are video
    /// memory nothing else wants and the tab brought back finds them (DecoderPool's parked sources); on unified memory and a small card
    /// they are the computer's memory, which the user minds most. The idle trim never does: a seek on coming back stays instant.
    /// </summary>
    internal static bool TrimEmptiesCache(bool asked, bool bigCard) => asked && !bigCard;

    /// <summary>
    /// Gives back what the worker holds beyond the frames in its cache, between frames on its own thread (a tab put behind: the client's
    /// <see cref="ControlBlock.TrimRequests"/>, <paramref name="asked"/>; or standing idle): the sources the client let go of retired now
    /// (their decoders disposed; their frames parked on a big card, else forgotten), the frames themselves when the trim empties the cache
    /// (<see cref="TrimEmptiesCache"/>: asked for, and not a big card), the cache's free list and the frames retired while drawn, the pool's
    /// free working textures, the renderer's textures kept between frames (SceneRenderer.ReleaseIdleTextures: with what carries one frame
    /// into the next only when asked, so the idle trim's redraw is the picture shown), and the readback's staging and planes and an HDR
    /// export's picture (each made again by the next frame that wants one): a few milliseconds. Then its memory is published. The rest,
    /// handing it back to Windows, is <see cref="GiveBack"/>'s.
    /// </summary>
    private static void TrimMemory(ref ControlBlock control, GpuDevice gpu, DecoderPool decoders, SceneRenderer renderer, bool asked)
    {
        decoders.SyncSources();
        if (TrimEmptiesCache(asked, decoders.Cache.BigCard))
            decoders.Cache.Clear();
        decoders.Cache.DropFree();
        gpu.Textures.ReleaseFree();
        renderer.ReleaseIdleTextures(history: asked);
        ReleaseReadback();
        _p010Picture?.Dispose();
        _p010Picture = null;
        PublishMemory(ref control, decoders.Cache);
    }

    /// <summary>
    /// A trim's last part, handing what was let go of back to Windows: the driver's kept allocations (GpuDevice.Trim: tens of milliseconds)
    /// and a compacting collection of this process's own heap (FFmpeg decoders' pictures: the worker has no UI, and nothing plays here
    /// while it runs). At once for the idle trim; for an asked-for one once the worker is left alone (<see cref="GiveBackDue"/>). Then its
    /// memory is published.
    /// </summary>
    private static void GiveBack(ref ControlBlock control, GpuDevice gpu, FrameCache cache)
    {
        gpu.Trim();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        PublishMemory(ref control, cache);
    }

    /// <summary>The readback's view of the client's section, its staging textures and NV12 / P010 planes let go of (ReadBack makes them again by generation and size).</summary>
    private static void ReleaseReadback()
    {
        _readbackView?.Dispose();
        _readbackFile?.Dispose();
        (_readbackView, _readbackFile, _readbackGeneration) = (null, null, -1);
        Com.Release(ref _readbackStaging);
        Com.Release(ref _nv12ChromaStaging);
        _nv12Luma?.Dispose();
        _nv12Chroma?.Dispose();
        (_nv12Luma, _nv12Chroma, _readbackSize) = (null, null, default);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage,
            PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCounters counters, uint size);

    /// <summary>This process's private bytes (PROCESS_MEMORY_COUNTERS_EX.PrivateUsage: its commit), 0 when Windows does not say. No allocation.</summary>
    internal static long PrivateBytes()
    {
        var counters = new ProcessMemoryCounters { Size = (uint)sizeof(ProcessMemoryCounters) };
        return GetProcessMemoryInfo(-1, ref counters, counters.Size) ? (long)counters.PrivateUsage : 0;
    }

    private static void RenderScene(CompositorSharedMemory shared, int slot, GpuDevice gpu, SceneRenderer renderer, DecoderPool decoders, ref OutputRing? outputs,
        ref ExportLoopbackPreview? loopback)
    {
        ref var control = ref shared.Control;
        ref var header = ref shared.SceneHeader(slot);

        // The Export Quality Preview (5.4B): made on the first scene that asks for it, let go on the first player scene that does not (a
        // readback between two of its frames, Export Frame or a project's thumbnail, leaves it be). One whose textures or render failed
        // at this canvas's size is kept for its state (Failed) while the plain picture is drawn.
        var wanted = ExportLoopbackPreview.Wants(header);
        var looped = wanted && loopback?.Broken(header) != true;
        if (wanted)
        {
            loopback ??= new ExportLoopbackPreview(gpu, renderer);
        }
        else if ((header.Flags & FrameSceneHeader.FlagReadback) == 0)
        {
            if (loopback is not null)
            {
                loopback.Dispose();
                loopback = null;
            }

            if (control.LoopbackState != 0 || (header.Flags & FrameSceneHeader.FlagLoopback) != 0)
                control.LoopbackState = (int)((header.Flags & FrameSceneHeader.FlagLoopback) != 0 ? ExportLoopbackState.Unavailable : ExportLoopbackState.Off);
        }

        // Exports, filmstrips and tests draw only exact frames: every media layer's frame is asked for and waited on (up
        // to 20 s, beating) before the output is touched.
        if ((header.Flags & FrameSceneHeader.FlagExactFrames) != 0)
            WaitForExactFrames(shared, slot, decoders);
        var width = Math.Clamp(header.OutputWidth > 0 ? header.OutputWidth : header.CanvasWidth, 16, 8192);
        var height = Math.Clamp(header.OutputHeight > 0 ? header.OutputHeight : header.CanvasHeight, 16, 8192);

        // The frame is drawn at (width, height) into the top-left of the ring, which is made anew only when it no longer fits or is far
        // too large (5.4.1A: a dock splitter's drag of the player resized it on every pixel, three shared textures and their handles each
        // time); a frame read back stays in a ring of exactly its size.
        var capacity = OutputCapacity(outputs?.Width ?? 0, outputs?.Height ?? 0, width, height, (header.Flags & FrameSceneHeader.FlagReadback) != 0);
        if (outputs is null || (outputs.Width, outputs.Height) != capacity)
        {
            // A new ring is announced (its generation and size) only once a frame is drawn in it: a reader that opened it
            // before would copy a texture nothing was drawn into (a black flash on every resize).
            outputs?.Dispose();
            var generation = Math.Max(Volatile.Read(ref control.OutputGeneration), _lastGeneration) + 1;
            _lastGeneration = generation;
            outputs = new OutputRing(gpu, shared.BaseName, generation, capacity.Width, capacity.Height);
        }

        // A preview frame has the device to itself from taking its output texture to the flush after it: the decode lanes hold back
        // (DecoderPool.Drawing; the keyed mutex's acquire waits for the device as a draw does).
        var index = (Volatile.Read(ref control.PresentedIndex) + 1) % CompositorProtocol.OutputRing;
        var output = outputs[index];
        if (looped)
        {
            if (!DrawLoopback(shared, slot, gpu, renderer, decoders, loopback!, outputs, ref index, ref output, width, height, out var broke))
                return;
            // Its textures or render failed on this very frame: the plain picture, never a black one, while Failed stays published.
            if (broke)
                looped = false;
        }

        if (!looped)
        {
            RenderInto(shared, slot, gpu, renderer, decoders, outputs, ref index, ref output, width, height, out var refused);
            if (refused)
                return;
        }

        // The frame commit barrier (the user's directive): this picture has videos at two moments. It goes no further: what is on
        // screen stays, and it is drawn again as the frames it lacks arrive, or once the barrier's time is up.
        _held = decoders.HoldsForOthers();
        if (_held)
            return;

        // Whether this frame showed every exact frame, set before its readback so a reader can check the very frame it got.
        control.PresentedExact = decoders.LastFrameExact ? 1 : 0;
        if ((header.Flags & FrameSceneHeader.FlagReadback) != 0)
            ReadBack(shared, gpu, renderer, output, width, height, header.FrameSequenceNumber, (header.Flags & FrameSceneHeader.FlagReadbackNv12) != 0,
                (header.Flags & FrameSceneHeader.FlagReadbackP010) != 0 ? _p010Picture : null);
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
            control.OutputWidth = outputs.Width;
            control.OutputHeight = outputs.Height;
            Volatile.Write(ref control.OutputGeneration, outputs.Generation);
            outputs.Announced = true;
        }

        Interlocked.Increment(ref control.FramesRendered);
        Volatile.Write(ref control.PresentedSequence, header.FrameSequenceNumber);
    }

    /// <summary>
    /// The size of the output ring for a frame of <paramref name="width"/> x <paramref name="height"/> (5.4.1A): the ring as it is
    /// (<paramref name="ringWidth"/> x <paramref name="ringHeight"/>, 0 for none) while the frame fits in it and fills at least half its
    /// area; else a new one. The first ring, and every ring a frame is read back from (<paramref name="exact"/>: the readback copies the
    /// whole texture), is the frame's size exactly; a ring grown or re-fitted is rounded up to 128 pixels a side (at most 8192), and a grown
    /// one keeps the old ring's larger side, so a drag of the player makes a few rings, not one a pixel. Pure: the golden test
    /// player-resize-no-stretch checks it.
    /// </summary>
    internal static (int Width, int Height) OutputCapacity(int ringWidth, int ringHeight, int width, int height, bool exact)
    {
        if (exact || ringWidth <= 0 || ringHeight <= 0)
            return (width, height);
        if (width > ringWidth || height > ringHeight)
            return (Math.Max(RoundUp(width), ringWidth), Math.Max(RoundUp(height), ringHeight));
        if ((long)width * height * 2 < (long)ringWidth * ringHeight)
            return (RoundUp(width), RoundUp(height));
        return (ringWidth, ringHeight);

        static int RoundUp(int side) => Math.Min((side + 127) / 128 * 128, 8192);
    }

    /// <summary>
    /// The scene drawn into the output texture (every frame but the Export Quality Preview's). <paramref name="refused"/>: neither of two
    /// output textures could be taken (a reader holding them), and nothing was drawn. The slot's content size is written while it is
    /// held (5.4.1A: a reader that holds it reads the size of the frame in it).
    /// </summary>
    private static void RenderInto(CompositorSharedMemory shared, int slot, GpuDevice gpu, SceneRenderer renderer, DecoderPool decoders, OutputRing outputs,
        ref int index, ref (GpuTexture Texture, nint KeyedMutex) output, int width, int height, out bool refused)
    {
        refused = false;
        ref var control = ref shared.Control;
        ref var header = ref shared.SceneHeader(slot);

        // An HDR export's frame read back as P010 (5.4B) is drawn into a 16-bit float picture of the worker's own, for its ten bits.
        var target = (header.Flags & (FrameSceneHeader.FlagReadback | FrameSceneHeader.FlagReadbackP010)) == (FrameSceneHeader.FlagReadback | FrameSceneHeader.FlagReadbackP010)
            ? P010Picture(gpu, width, height).Rtv
            : output.Texture.Rtv;
        decoders.Drawing((header.Flags & FrameSceneHeader.FlagExactFrames) == 0);
        var decodedBefore = decoders.FramesDecoded;
        try
        {
            // The texture after the one shown last; a reader holding it (rarely, and briefly) makes it the next.
            var started = Stopwatch.GetTimestamp();
            if (Dxgi.AcquireSync(output.KeyedMutex, 0, 50) != 0)
            {
                index = (index + 1) % CompositorProtocol.OutputRing;
                output = outputs[index];
                if (Dxgi.AcquireSync(output.KeyedMutex, 0, 50) != 0)
                {
                    refused = true;
                    return;
                }
            }

            var acquired = Stopwatch.GetTimestamp();
            control.SetOutputContentSize(index, width, height, outputs.Generation, (header.Flags & FrameSceneHeader.FlagReadback) != 0);
            var count = Math.Clamp(header.ActiveLayerCount, 0, CompositorProtocol.MaxLayers);
            var layers = MemoryMarshal.CreateReadOnlySpan(ref shared.Layer(slot, 0), count);
            try
            {
                renderer.Render(header, layers, target, width, height, raw: (header.Flags & FrameSceneHeader.FlagRawOutput) != 0);
            }
            finally
            {
                _ = Dxgi.ReleaseSync(output.KeyedMutex, 0);
            }

            var drawn = Stopwatch.GetTimestamp();
            gpu.Context.Flush();
            control.LastAcquireMs = (float)Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds;
            control.LastDrawMs = (float)Stopwatch.GetElapsedTime(acquired, drawn).TotalMilliseconds;
            control.LastFlushMs = (float)Stopwatch.GetElapsedTime(drawn).TotalMilliseconds;
            control.LastDrawDecodes = decoders.FramesDecoded - decodedBefore;
            control.TexturesMade = gpu.TexturesMade;
        }
        finally
        {
            decoders.Drawing(false);
        }
    }

    /// <summary>
    /// The Export Quality Preview's frame (5.4B): the scene rendered at the canvas's size and handed to the encoder while the decode lanes
    /// hold back, the bounded wait for its decoded picture with them free and no output texture held, then the compare drawn into the
    /// output under its keyed mutex. While an edge is tuned (5.4C), or with the Magnifier on under the pointer (5.4D), the loupe comes
    /// over it: its point found beside the render, its view
    /// (the compare picture round that point) drawn before the output is taken, one quad laid on after the compare, under the mutex.
    /// False when no output texture could be taken; broke when the render failed on this frame (nothing drawn: the caller draws the plain picture).
    /// </summary>
    private static bool DrawLoopback(CompositorSharedMemory shared, int slot, GpuDevice gpu, SceneRenderer renderer, DecoderPool decoders, ExportLoopbackPreview loopback,
        OutputRing outputs, ref int index, ref (GpuTexture Texture, nint KeyedMutex) output, int width, int height, out bool broke)
    {
        broke = false;
        ref var control = ref shared.Control;
        ref var header = ref shared.SceneHeader(slot);
        var started = Stopwatch.GetTimestamp();
        var decodedBefore = decoders.FramesDecoded;
        var loupe = false;
        decoders.Drawing(true);
        try
        {
            var count = Math.Clamp(header.ActiveLayerCount, 0, CompositorProtocol.MaxLayers);
            var layers = MemoryMarshal.CreateReadOnlySpan(ref shared.Layer(slot, 0), count);
            loopback.Render(ref control, header, layers, decoders);

            // The loupe's point (its edge scan, a layer pass) here, never under the output's mutex; the encoded frame was rendered
            // without it (Render's flat copy has no loupe key). An edge being tuned comes first; else the Magnifier (5.4D) round the
            // point under the pointer, its fractions of the output made this output's pixels.
            loupe = !loopback.Broken(header)
                && ((header.LoupeClipKey != 0 && renderer.PrepareLoupe(header, layers, width, height, drawLayer: false))
                    || ((header.Flags & FrameSceneHeader.FlagLoopbackLoupe) != 0
                        && renderer.PrepareLoupeAt(header, header.LoopbackLoupeX * width, header.LoopbackLoupeY * height, width, height)));
        }
        finally
        {
            decoders.Drawing(false);
        }

        if (loopback.Broken(header))
        {
            broke = true;
            return true;
        }

        loopback.Collect(ref control);

        // The loupe's view: the compare picture itself (encoded, or the difference) round the same point, point sampled.
        if (loupe && renderer.LoupeFocus is { } focus)
            loupe = loopback.DrawLoupeView(header, focus.Rtv, focus.Side, focus.X, focus.Y, focus.Scale);
        var collected = Stopwatch.GetTimestamp();
        if (Dxgi.AcquireSync(output.KeyedMutex, 0, 50) != 0)
        {
            index = (index + 1) % CompositorProtocol.OutputRing;
            output = outputs[index];
            if (Dxgi.AcquireSync(output.KeyedMutex, 0, 50) != 0)
                return false;
        }

        var acquired = Stopwatch.GetTimestamp();
        control.SetOutputContentSize(index, width, height, outputs.Generation, (header.Flags & FrameSceneHeader.FlagReadback) != 0);
        try
        {
            loopback.Draw(header, output.Texture.Rtv, width, height);
            if (loupe)
                renderer.CompositeLoupe(output.Texture.Rtv, width, height);
        }
        finally
        {
            _ = Dxgi.ReleaseSync(output.KeyedMutex, 0);
        }

        var drawn = Stopwatch.GetTimestamp();
        gpu.Context.Flush();
        control.LastAcquireMs = (float)Stopwatch.GetElapsedTime(collected, acquired).TotalMilliseconds;
        control.LastDrawMs = (float)(Stopwatch.GetElapsedTime(started, collected).TotalMilliseconds + Stopwatch.GetElapsedTime(acquired, drawn).TotalMilliseconds);
        control.LastFlushMs = (float)Stopwatch.GetElapsedTime(drawn).TotalMilliseconds;
        control.LastDrawDecodes = decoders.FramesDecoded - decodedBefore;
        control.TexturesMade = gpu.TexturesMade;
        return true;
    }

    // An HDR export's picture before its P010 conversion (5.4B): RGBA16F at the output's size, made on the first such frame.
    private static GpuTexture? _p010Picture;

    private static GpuTexture P010Picture(GpuDevice gpu, int width, int height)
    {
        if (_p010Picture is null || _p010Picture.Width != width || _p010Picture.Height != height)
        {
            _p010Picture?.Dispose();
            _p010Picture = gpu.CreateTexture(width, height, DxgiFormat.R16G16B16A16Float, renderTarget: true);
        }

        return _p010Picture;
    }

    // The last picture was held back by the frame commit barrier: it is drawn again on the worker's next turn, whatever wakes it.
    private static bool _held;

    // The output generation made last (announced or not): names are never reused within a worker.
    private static int _lastGeneration;

    // ----- Readback (FlagReadback: exports, filmstrips) -----

    private static System.IO.MemoryMappedFiles.MemoryMappedFile? _readbackFile;
    private static System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _readbackView;
    private static int _readbackGeneration = -1;
    private static nint _readbackStaging;
    private static (int Width, int Height) _readbackSize;

    // NV12's two planes, drawn on the card, and the second's staging texture (the first's is _readbackStaging, in R8 while its size says so).
    // P010's (5.4B) are the same two, in R16 and R16G16 while _readbackIsP010 says so.
    private static GpuTexture? _nv12Luma, _nv12Chroma;
    private static nint _nv12ChromaStaging;
    private static bool _readbackIsNv12, _readbackIsP010;

    /// <summary>
    /// The frame just drawn into the client's readback section: copied to a staging texture, mapped (waiting for the card),
    /// written row by row, and its sequence number set last. A section too small, or not there, is skipped (logged once).
    /// <paramref name="p010Picture"/> (5.4B, FlagReadbackP010): the 16-bit float frame, read back as P010 instead (NV12's layout, two bytes a sample).
    /// </summary>
    private static void ReadBack(CompositorSharedMemory shared, GpuDevice gpu, SceneRenderer renderer, (GpuTexture Texture, nint KeyedMutex) output, int width, int height, long sequence, bool nv12,
        GpuTexture? p010Picture)
    {
        ref var control = ref shared.Control;
        var generation = Volatile.Read(ref control.ReadbackGeneration);
        var capacity = Volatile.Read(ref control.ReadbackCapacity);
        var p010 = p010Picture is not null;
        nv12 &= !p010;
        var sample = p010 ? 2 : 1;
        var needed = p010 ? CompositorProtocol.P010Bytes(width, height) : nv12 ? CompositorProtocol.Nv12Bytes(width, height) : (long)width * height * 4;
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

            var (chromaWidth, chromaHeight) = ((width + 1) / 2, (height + 1) / 2);
            if (_readbackStaging == 0 || _readbackSize != (width, height) || _readbackIsNv12 != nv12 || _readbackIsP010 != p010)
            {
                Com.Release(ref _readbackStaging);
                Com.Release(ref _nv12ChromaStaging);
                _nv12Luma?.Dispose();
                _nv12Chroma?.Dispose();
                (_nv12Luma, _nv12Chroma) = (null, null);
                if (p010)
                {
                    _nv12Luma = gpu.CreateTexture(width, height, DxgiFormat.R16Unorm, renderTarget: true);
                    _nv12Chroma = gpu.CreateTexture(chromaWidth, chromaHeight, DxgiFormat.R16G16Unorm, renderTarget: true);
                    _readbackStaging = gpu.CreateStaging(width, height, DxgiFormat.R16Unorm);
                    _nv12ChromaStaging = gpu.CreateStaging(chromaWidth, chromaHeight, DxgiFormat.R16G16Unorm);
                }
                else if (nv12)
                {
                    _nv12Luma = gpu.CreateTexture(width, height, DxgiFormat.R8Unorm, renderTarget: true);
                    _nv12Chroma = gpu.CreateTexture(chromaWidth, chromaHeight, DxgiFormat.R8G8Unorm, renderTarget: true);
                    _readbackStaging = gpu.CreateStaging(width, height, DxgiFormat.R8Unorm);
                    _nv12ChromaStaging = gpu.CreateStaging(chromaWidth, chromaHeight, DxgiFormat.R8G8Unorm);
                }
                else
                {
                    _readbackStaging = gpu.CreateStaging(width, height, DxgiFormat.B8G8R8A8Unorm);
                }

                (_readbackSize, _readbackIsNv12, _readbackIsP010) = ((width, height), nv12, p010);
            }

            if (p010)
            {
                // The worker's own picture, not the shared output: no keyed mutex.
                renderer.ToP010(p010Picture!, _nv12Luma!, _nv12Chroma!);
                gpu.Context.CopyResource(_readbackStaging, _nv12Luma!.Texture);
                gpu.Context.CopyResource(_nv12ChromaStaging, _nv12Chroma!.Texture);
            }
            else
            {
                // The output texture is shared: copied out under its keyed mutex, as every other use of it.
                if (Dxgi.AcquireSync(output.KeyedMutex, 0, 100) != 0)
                    return;
                try
                {
                    if (nv12)
                    {
                        renderer.ToNv12(output.Texture, _nv12Luma!, _nv12Chroma!);
                        gpu.Context.CopyResource(_readbackStaging, _nv12Luma!.Texture);
                        gpu.Context.CopyResource(_nv12ChromaStaging, _nv12Chroma!.Texture);
                    }
                    else
                    {
                        gpu.Context.CopyResource(_readbackStaging, output.Texture.Texture);
                    }
                }
                finally
                {
                    _ = Dxgi.ReleaseSync(output.KeyedMutex, 0);
                }
            }

            Com.Check(gpu.Context.Map(_readbackStaging, 0, D3D11.MapRead, 0, out var mapped), "Map readback");
            byte* into = null;
            _readbackView.SafeMemoryMappedViewHandle.AcquirePointer(ref into);
            try
            {
                into += _readbackView.PointerOffset;
                var row = nv12 || p010 ? width * sample : width * 4;
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(mapped.Data + (long)y * mapped.RowPitch, into + (long)y * row, row, row);
                if (nv12 || p010)
                {
                    // The UV plane, after the Y plane, rows packed.
                    Com.Check(gpu.Context.Map(_nv12ChromaStaging, 0, D3D11.MapRead, 0, out var chroma), "Map readback (UV)");
                    try
                    {
                        var start = into + (long)width * height * sample;
                        var rowUv = chromaWidth * 2 * sample;
                        for (var y = 0; y < chromaHeight; y++)
                            Buffer.MemoryCopy(chroma.Data + (long)y * chroma.RowPitch, start + (long)y * rowUv, rowUv, rowUv);
                    }
                    finally
                    {
                        gpu.Context.Unmap(_nv12ChromaStaging, 0);
                    }
                }
            }
            finally
            {
                _readbackView.SafeMemoryMappedViewHandle.ReleasePointer();
                gpu.Context.Unmap(_readbackStaging, 0);
            }

            (control.ReadbackWidth, control.ReadbackHeight, control.ReadbackRowBytes) = (width, height, nv12 || p010 ? width * sample : width * 4);
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
