using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;
using Microsoft.Win32.SafeHandles;
using HandPegApp.Native;
using static HandPegApp.Native.Kernel32;

namespace HandPegApp.Compositor;

/// <summary>
/// HandPeg.exe --compositor-worker &lt;base name&gt; &lt;adapter&gt; [--standby &lt;n&gt;] [--ffmpeg &lt;path&gt; --ffprobe &lt;path&gt;]: the
/// out-of-process GPU compositor (the FFmpeg paths are for decoder fallback). A driver crash, TDR or decoder fault takes down only this
/// process; HandPeg (CompositorClient) starts another and loses at most a frame.
/// <para>Opens the client's control plane, creates its device, shaders and three shared output textures, then renders whenever the
/// client publishes a scene (or requests a redraw): newest scene only, never queued. Exits when the client exits, on request, or on
/// device loss (<see cref="CompositorProtocol.DeviceLostExitCode"/>).</para>
/// <para>A standby (--standby n) waits holding only a started runtime until the client signals its take-over event, then becomes the
/// worker: respawn after a crash costs a device and a frame, not a process start.</para>
/// <para>Releases memory on client request (tab deactivated: a counted trim that also empties the frame cache on unified memory or
/// low-VRAM GPUs) and on its own after 30 s idle: <see cref="TrimMemory"/>, then <see cref="GiveBack"/> (for requested trims only after a
/// quiet period, so a burst of tab switches pays once).</para>
/// </summary>
public static unsafe partial class CompositorWorker
{
    public const string Argument = "--compositor-worker";

    /// <summary>Options naming the FFmpeg and ffprobe used when Media Foundation cannot decode (each followed by a path).</summary>
    public const string FfmpegOption = "--ffmpeg", FfprobeOption = "--ffprobe";

    /// <summary>Command-line value following <paramref name="option"/>; empty when absent.</summary>
    internal static string OptionValue(string[] args, string option)
    {
        var at = Array.IndexOf(args, option);
        return at > 0 && at + 1 < args.Length ? args[at + 1] : "";
    }

    public static int Run(string[] args)
    {
        // Driver or decoder faults end the process silently (no "stopped working" dialog) and empty-drive access fails instead of
        // prompting; HandPeg detects the exit and takes over (SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX).
        _ = SetErrorMode(0x0001 | 0x0002);
        if (args.Length < 3)
            return CompositorProtocol.BadControlPlaneExitCode;
        var baseName = args[1];
        var adapter = int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : -1;
        var standbyAt = Array.IndexOf(args, "--standby");
        var standby = standbyAt > 0 && standbyAt + 1 < args.Length && int.TryParse(args[standbyAt + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

        // HandPeg's FFmpeg (DependencyUpdater, AppSettings.FfmpegPath) for streams MF cannot decode (FfmpegVideoDecoder); only
        // probed when such a stream is opened.
        FfmpegVideoDecoder.FfmpegPath = OptionValue(args, FfmpegOption);
        FfmpegVideoDecoder.FfprobePath = OptionValue(args, FfprobeOption);

        using var shared = CompositorSharedMemory.Open(baseName);
        if (shared is null)
            return CompositorProtocol.BadControlPlaneExitCode;

        // Unhandled exceptions (e.g. from a decode lane, which deliberately has no catch-all) end the process. Log the stack first and
        // write the one-line client note naming the exception and throwing frame (replacing the start-up adapter text). CreateNoWindow
        // and SetErrorMode hide the CLR's own report, so this is the only record.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is not Exception unhandled)
                return;
            AppLog.Write("Compositor worker: unhandled exception", unhandled);
            Note(shared, FaultLine(unhandled));
        };

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

        // MF and D3D need an MTA; Main's thread is STA.
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
            control.UnifiedMemory = gpu.UnifiedMemory ? 1 : 0;
            Note(shared, $"{gpu.AdapterName}{(gpu.IsWarp ? " (WARP)" : "")}, {shaders.Count} shaders");
            Volatile.Write(ref control.WorkerState, (int)CompositorWorkerState.Ready);

            var redraw = true;
            var first = true;
            var budget = long.MinValue;
            WaitHandle[] waits = [wake, clientExit, decoders.FramesArrived];

            // Counted requests issued before this worker started are already satisfied (it holds nothing).
            var clearsSeen = Volatile.Read(ref control.ClearRequests);
            Volatile.Write(ref control.ClearsDone, clearsSeen);
            var trimsSeen = Volatile.Read(ref control.TrimRequests);
            Volatile.Write(ref control.TrimsDone, trimsSeen);

            // Idle tracking: last activity (scene, hints, playback, lane decoding) and whether memory was trimmed since. Memory stats
            // are published every 2 s (Settings readout, goldens).
            var hintsSeen = Volatile.Read(ref control.HintsVersion);
            var askedLast = Stopwatch.GetTimestamp();
            var idleTrimmed = false;
            var nextPublish = 0L;

            // A requested trim awaiting its final GiveBack phase (deferred until quiet), and whether a new scene arrived after it (another
            // tab activated, whose working set GiveBack would discard).
            var giveBackWaiting = false;
            var sceneAfterTrim = false;

            // The 3D path warmed once (Q2, 5.6.2B): see ExplodedWarmDue.
            var explodedWarmed = false;
            while (true)
            {
                // A decode lane stuck in a driver for 8 s stops the heartbeat; the client takes over at 10 s.
                if (!decoders.LaneStuckFor(8))
                    Interlocked.Increment(ref control.Heartbeat);
                if (decoders.LaneFault is { } fault)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(fault);
                switch ((CompositorCommand)Interlocked.Exchange(ref control.Command, 0))
                {
                    case CompositorCommand.SimulateDeviceLost:
                        throw new GpuException(Com.DeviceRemoved, "A simulated device removal");
                    case CompositorCommand.SimulateCrash:
                        Environment.Exit(unchecked((int)0xC0000005)); // crash-like exit code without a Windows error report
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

                // Counted clear, then trim, between frames before taking the next scene. The trim is acknowledged only after its final
                // phase and memory publish (GiveBack, below), so a reader seeing it done reads the released memory.
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
                    TrimSources(ref control, decoders, asked: true);
                    (giveBackWaiting, sceneAfterTrim, idleTrimmed, nextPublish) = (true, false, true, Stopwatch.GetTimestamp() + PublishMemoryTicks);
                }

                // Client cache budget and seek hints (Instant Seek): applied on change without drawing a frame.
                var requested = Volatile.Read(ref control.CacheBudgetRequest);
                if (requested != budget)
                    decoders.Cache.SetBudget(budget = requested);
                decoders.TakeHints();
                decoders.Tidy();
                decoders.Cache.CheckWindowsBudget(Stopwatch.GetTimestamp());

                var slot = shared.TakeLatest(out var isNew);
                sceneAfterTrim |= giveBackWaiting && isNew;
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

                // Publish decoder counters every turn, drawn or not (pre-roll decodes between frames; readers must not wait for the next
                // frame to see them).
                control.DecoderOpens = decoders.Opens;
                control.DecoderSeeks = decoders.Seeks;
                control.DecodedFrames = decoders.FramesDecoded;
                (control.Stretches, control.LateLandings, control.MissingFrames) = (decoders.Stretches, decoders.LateLandings, decoders.MissingFrames);

                // Frames after the displayed one that play start waits for (GpuPreviewEngine.PlayStartReady); polled tightly while missing.
                var ahead = decoders.NextFramesReady();
                control.PresentedAhead = ahead ? 1 : 0;

                // Idle trim: paused with no scene, hint or decode for IdleTrimSeconds, release everything but cached frames (seeks stay
                // instant) once, then redraw so working textures are recreated while paused rather than on the first played frame. Skipped
                // while the Live Encoded Preview is on (its redraw is an encode).
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

                // The player's worker warms the 3D path once, quiet after its first flat frames, so the first 3D open in a session does not
                // pay the driver's first-draw shader builds (87 ms on the Adreno X1-85, ~2 ms on an RX 7900 XTX). Never while playing, with
                // the Live Encoded Preview on, or for strip, export and test clients (TabParkingRequest 1 is the player).
                if (!explodedWarmed && !first && ExplodedWarmDue(now, askedLast, decoders.Playing, loopback is not null))
                {
                    explodedWarmed = true;
                    if (Volatile.Read(ref control.TabParkingRequest) == 1 && header.ViewMode == 0 && header.FrameSequenceNumber > 0
                        && header.CanvasWidth > 0 && header.CanvasHeight > 0 && header.ActiveLayerCount > 0)
                    {
                        // Like a frame, it owns the device: decode lanes hold back. A failure is logged, never fatal (the 3D view just opens cold).
                        var count = Math.Clamp(header.ActiveLayerCount, 0, CompositorProtocol.MaxLayers);
                        decoders.Drawing(true);
                        try
                        {
                            renderer.WarmExploded(header, MemoryMarshal.CreateReadOnlySpan(ref shared.Layer(slot, 0), count));
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not GpuException { IsDeviceLost: true })
                        {
                            AppLog.Write("Compositor worker: the 3D warm-up failed", ex);
                        }
                        finally
                        {
                            decoders.Drawing(false);
                        }
                    }
                }

                // Final phase of a requested trim (must not penalize rapid tab switching): release driver memory and compact the heap once
                // the worker has been quiet for GiveBackQuietMs and nothing plays. IDXGIDevice3::Trim costs tens of ms (75 ms on Adreno X1-85
                // after a 1080p tab), which each switch and the new tab's first frames would pay; a burst of switches releases once at its end.
                // Then acknowledge all trims requested meanwhile. If a new scene arrived after the trim (another tab activated, playback
                // likely), its working set and driver allocations belong to that tab: keep them for the idle trim; otherwise release all now.
                if (giveBackWaiting && GiveBackDue(now, askedLast, decoders.Playing))
                {
                    if (!sceneAfterTrim)
                    {
                        ReleaseWorkingSet(gpu, decoders, renderer, asked: true);
                        GiveBack(ref control, gpu, decoders.Cache);
                    }

                    Volatile.Write(ref control.TrimsDone, trimsSeen);
                    (giveBackWaiting, nextPublish) = (false, now + PublishMemoryTicks);
                }

                if (now >= nextPublish)
                {
                    PublishMemory(ref control, decoders.Cache);
                    nextPublish = now + PublishMemoryTicks;
                }

                gpu.CheckAlive();
                // A paused Live Encoded Preview frame still in the encoder: re-check in 2 ms and draw when it returns.
                var pending = loopback?.Pending == true && ExportLoopbackPreview.Wants(header);
                var woke = WaitHandle.WaitAny(waits, pending ? 2 : _held ? 16 : ahead || !decoders.Playing ? 100 : 4);
                if (woke == 1)
                    return 0;
                // Redraw only once the paused frame has returned and decoded, or was given up (not a full redraw with an encoder wait every
                // 2 ms while a wipe or loupe is dragged).
                if (pending && loopback!.Poll())
                    redraw = true;

                // A frame missing from the last picture was decoded (Instant Seek): redraw the same scene with it.
                if ((woke == 2 && !decoders.LastFrameExact) || _held)
                    redraw = true;
            }
        }
        catch (GpuException ex) when (ex.IsDeviceLost)
        {
            control.LastError = ex.Code;
            AppLog.Write("Compositor worker: device lost", ex);
            Note(shared, FaultLine(ex));
            Volatile.Write(ref control.WorkerState, (int)CompositorWorkerState.DeviceLost);
            return CompositorProtocol.DeviceLostExitCode;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            control.LastError = ex is GpuException gpuFault ? gpuFault.Code : ex.HResult;
            AppLog.Write("Compositor worker failed", ex);
            Note(shared, FaultLine(ex));
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

    // ----- Memory release -----

    /// <summary>Idle time (paused, no activity) before the worker releases everything but cached frames, seconds.</summary>
    internal const int IdleTrimSeconds = 30;

    /// <summary>Quiet seconds (no scene, hint, playback or decode) before the player's worker warms the 3D path (once per worker).</summary>
    internal const int ExplodedWarmSeconds = 2;

    /// <summary>True when the one-shot 3D warm-up may run: quiet for <see cref="ExplodedWarmSeconds"/>, not playing, no Live Encoded Preview. Pure.</summary>
    internal static bool ExplodedWarmDue(long now, long askedLast, bool playing, bool loopbackOn) =>
        !playing && !loopbackOn && now - askedLast > ExplodedWarmSeconds * Stopwatch.Frequency;

    /// <summary>
    /// True when the idle trim is due: no activity (scene, hint, playback or decode) for more than <see cref="IdleTrimSeconds"/> since
    /// <paramref name="askedLast"/> (Stopwatch ticks), not already trimmed, and the Live Encoded Preview off (<paramref name="loopbackOn"/>:
    /// the post-trim redraw would be an encode).
    /// </summary>
    internal static bool IdleTrimDue(long now, long askedLast, bool idleTrimmed, bool loopbackOn) =>
        !idleTrimmed && !loopbackOn && now - askedLast > IdleTrimSeconds * Stopwatch.Frequency;

    /// <summary>Quiet time required before a requested trim's final phase (<see cref="GiveBack"/>), ms.</summary>
    internal const int GiveBackQuietMs = 250;

    /// <summary>
    /// True when a requested trim's final phase may run: no activity (scene, hint or decode) for <see cref="GiveBackQuietMs"/> since
    /// <paramref name="askedLast"/> (Stopwatch ticks) and not <paramref name="playing"/> (a blocking pass during playback would make a
    /// frame late).
    /// </summary>
    internal static bool GiveBackDue(long now, long askedLast, bool playing) =>
        !playing && now - askedLast >= GiveBackQuietMs * Stopwatch.Frequency / 1000;

    // Periodic memory publish interval when nothing else publishes (Stopwatch ticks: 2 s).
    private static readonly long PublishMemoryTicks = 2 * Stopwatch.Frequency;

    /// <summary>Publishes frame cache usage and limits (bytes, frames, adaptive budget maximum, ceiling and steps).</summary>
    private static void PublishCache(ref ControlBlock control, FrameCache cache)
    {
        control.CacheBytes = cache.Bytes;
        control.CacheBudgetBytes = cache.BudgetBytes;
        control.CacheBudgetCap = cache.CapReason;
        control.CachedFrames = cache.Count;
        control.CacheFreeBytes = cache.FreeBytes;
        control.CacheMaximumBytes = cache.MaximumBytes;
        control.CacheCeilingBytes = cache.CeilingBytes;
        control.CacheGrowSteps = cache.GrowSteps;
    }

    /// <summary>Publishes private bytes and cache sizes for readers between frames (every 2 s and after a clear or trim).</summary>
    private static void PublishMemory(ref ControlBlock control, FrameCache cache)
    {
        PublishCache(ref control, cache);
        Volatile.Write(ref control.WorkerPrivateBytes, PrivateBytes());
    }

    /// <summary>
    /// True when a trim empties the frame cache. Background tabs stay warm: a requested trim (<paramref name="asked"/>, tab deactivated)
    /// keeps frames so the reactivated tab finds its playhead frames immediately, unless free RAM (AvailPhys) is below the floor
    /// (<paramref name="memoryShort"/>) and the frames live in system RAM (unified memory or low VRAM; a large VRAM cache,
    /// FrameCache.BigCard, keeps them within the DXGI budget). The idle trim never empties it.
    /// </summary>
    internal static bool TrimEmptiesCache(bool asked, bool bigCard, bool memoryShort) => asked && !bigCard && memoryShort;

    /// <summary>Free RAM (AvailPhys) floor below which a deactivated tab empties the cache (matches MainWindow.AvailableFloorBytes).</summary>
    internal const long AvailableFloorBytes = 2560L << 20;

    /// <summary>
    /// Releases everything beyond cached frames, between frames on the worker thread (requested via <see cref="ControlBlock.TrimRequests"/>
    /// when <paramref name="asked"/>, or idle): retires released sources (decoders disposed; frames parked with a large cache, else
    /// forgotten), empties the cache when <see cref="TrimEmptiesCache"/> says, drops the cache free list and frames retired while drawn, pool
    /// free textures, renderer textures kept between frames (SceneRenderer.ReleaseIdleTextures; frame-to-frame history only when asked, so
    /// the idle redraw matches what is shown), and readback staging/planes and the HDR export picture (recreated on demand): a few ms.
    /// Then publishes memory. Returning memory to Windows is <see cref="GiveBack"/>'s job.
    /// </summary>
    private static void TrimMemory(ref ControlBlock control, GpuDevice gpu, DecoderPool decoders, SceneRenderer renderer, bool asked)
    {
        TrimSources(ref control, decoders, asked);
        ReleaseWorkingSet(gpu, decoders, renderer, asked);
        PublishMemory(ref control, decoders.Cache);
    }

    /// <summary>
    /// Immediate first phase of a trim (all a requested trim does on the worker's next turn): retires released sources, empties the cache
    /// when <see cref="TrimEmptiesCache"/> says, and releases readback staging and the HDR export picture (never on the player path). Then
    /// publishes memory.
    /// </summary>
    private static void TrimSources(ref ControlBlock control, DecoderPool decoders, bool asked)
    {
        decoders.SyncSources();
        if (TrimEmptiesCache(asked, decoders.Cache.BigCard, SystemCommit.TryRead(out _, out _, out var available) && available < AvailableFloorBytes))
            decoders.Cache.Clear();
        ReleaseReadback();
        _p010Picture?.Dispose();
        _p010Picture = null;
        PublishMemory(ref control, decoders.Cache);
    }

    /// <summary>
    /// Releases the player's working set: cache free list, pool free textures and renderer-kept textures. Done by the idle trim, and by a
    /// requested trim only when no scene followed it (the newly activated tab would recreate them on its first frames and first play).
    /// </summary>
    private static void ReleaseWorkingSet(GpuDevice gpu, DecoderPool decoders, SceneRenderer renderer, bool asked)
    {
        decoders.Cache.DropFree();
        gpu.Textures.ReleaseFree();
        renderer.ReleaseIdleTextures(history: asked);
    }

    /// <summary>
    /// Final trim phase returning released memory to Windows: driver-retained allocations (GpuDevice.Trim, tens of ms) and a compacting
    /// collection of this process's heap (FFmpeg decoder pictures; the worker has no UI and nothing plays meanwhile). Immediate for the idle
    /// trim; for a requested trim once quiet (<see cref="GiveBackDue"/>) and only when no scene followed. Then publishes memory.
    /// </summary>
    private static void GiveBack(ref ControlBlock control, GpuDevice gpu, FrameCache cache)
    {
        gpu.Trim();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        PublishMemory(ref control, cache);
    }

    /// <summary>Releases the readback section view, staging textures and NV12/P010 planes (ReadBack recreates them by generation and size).</summary>
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

    /// <summary>This process's private bytes (PROCESS_MEMORY_COUNTERS_EX.PrivateUsage, i.e. commit); 0 when unavailable. No allocation.</summary>
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

        // Live Encoded Preview: created on the first scene requesting it, released on the first player scene that does not (readbacks in
        // between, e.g. Export Frame or a project thumbnail, leave it alone). One that failed at this canvas size is kept for its Failed
        // state while the plain picture is drawn.
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

        // Exports, filmstrips and tests draw only exact frames: request and wait for every media layer's frame (up to 20 s, with
        // heartbeat) before touching the output.
        if ((header.Flags & FrameSceneHeader.FlagExactFrames) != 0)
            WaitForExactFrames(shared, slot, decoders);
        var width = Math.Clamp(header.OutputWidth > 0 ? header.OutputWidth : header.CanvasWidth, 16, 8192);
        var height = Math.Clamp(header.OutputHeight > 0 ? header.OutputHeight : header.CanvasHeight, 16, 8192);

        // Draw at (width, height) into the ring's top-left; recreate the ring only when the frame no longer fits or it is far too large
        // (dragging a dock splitter would otherwise recreate three shared textures and handles per pixel). Readback frames use an exact-size
        // ring.
        var capacity = OutputCapacity(outputs?.Width ?? 0, outputs?.Height ?? 0, width, height, (header.Flags & FrameSceneHeader.FlagReadback) != 0);
        if (outputs is null || (outputs.Width, outputs.Height) != capacity)
        {
            // Announce a new ring (generation and size) only after a frame is drawn in it; earlier readers would copy an empty texture
            // (black flash on every resize).
            outputs?.Dispose();
            var generation = Math.Max(Volatile.Read(ref control.OutputGeneration), _lastGeneration) + 1;
            _lastGeneration = generation;
            outputs = new OutputRing(gpu, shared.BaseName, generation, capacity.Width, capacity.Height);
        }

        // A preview frame owns the device from output acquire to flush: decode lanes hold back (DecoderPool.Drawing; the keyed mutex
        // acquire waits for the device like a draw).
        var index = (Volatile.Read(ref control.PresentedIndex) + 1) % CompositorProtocol.OutputRing;
        var output = outputs[index];
        if (looped)
        {
            if (!DrawLoopback(shared, slot, gpu, renderer, decoders, loopback!, outputs, ref index, ref output, width, height, out var broke))
                return;
            // Textures or render failed on this frame: draw the plain picture (never black) while Failed stays published.
            if (broke)
                looped = false;
        }

        if (!looped)
        {
            RenderInto(shared, slot, gpu, renderer, decoders, outputs, ref index, ref output, width, height, out var refused);
            if (refused)
                return;
        }

        // Frame commit barrier: this picture mixes videos from two times, so it is not presented; the screen keeps the previous frame and
        // this one is redrawn as missing frames arrive or when the barrier times out.
        _held = decoders.HoldsForOthers();
        if (_held)
            return;

        // Set exactness before readback so readers can check the exact frame they received.
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
    /// Output ring size for a <paramref name="width"/> x <paramref name="height"/> frame: keep the current ring (<paramref name="ringWidth"/>
    /// x <paramref name="ringHeight"/>, 0 = none) while the frame fits and fills at least half its area; else a new one. The first ring and
    /// readback rings (<paramref name="exact"/>: readback copies the whole texture) are exactly frame-sized; grown or refitted rings round up
    /// to 128 px per side (max 8192), and growth keeps the old larger side, so a player drag creates a few rings, not one per pixel. Pure;
    /// covered by the player-resize-no-stretch golden.
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
    /// Draws the scene into an output texture (all frames except the Live Encoded Preview's). <paramref name="refused"/>: neither of two
    /// output textures could be acquired (held by a reader) and nothing was drawn. The slot's content size is written while held, so a
    /// reader holding it sees the size of the frame it contains.
    /// </summary>
    private static void RenderInto(CompositorSharedMemory shared, int slot, GpuDevice gpu, SceneRenderer renderer, DecoderPool decoders, OutputRing outputs,
        ref int index, ref (GpuTexture Texture, nint KeyedMutex) output, int width, int height, out bool refused)
    {
        refused = false;
        ref var control = ref shared.Control;
        ref var header = ref shared.SceneHeader(slot);

        decoders.Drawing((header.Flags & FrameSceneHeader.FlagExactFrames) == 0);
        var decodedBefore = decoders.FramesDecoded;
        try
        {
            // Use the texture after the last shown; if a reader holds it (rare, brief), fall back to the next.
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

            // Choose the render target only now, from the texture actually held: the fallback may have moved slots, and drawing into the
            // unacquired one published stale or black frames. HDR exports read back as P010 render into a private RGBA16F picture to keep
            // 10 bits.
            var target = (header.Flags & (FrameSceneHeader.FlagReadback | FrameSceneHeader.FlagReadbackP010)) == (FrameSceneHeader.FlagReadback | FrameSceneHeader.FlagReadbackP010)
                ? P010Picture(gpu, width, height).Rtv
                : output.Texture.Rtv;
            var acquired = Stopwatch.GetTimestamp();
            control.SetOutputContentSize(index, width, height, outputs.Generation, (header.Flags & FrameSceneHeader.FlagReadback) != 0, header.FrameSequenceNumber);
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
    /// Live Encoded Preview frame: render at canvas size and submit to the encoder while decode lanes hold back; bounded wait for the decoded
    /// picture with lanes free and no output held; then draw the compare into the output under its keyed mutex. While an edge is tuned, or
    /// the Magnifier is on under the pointer, the loupe is overlaid: its point found alongside the render, its view (compare picture around
    /// that point) drawn before acquiring the output, and one quad composited after the compare under the mutex. False when no output could
    /// be acquired; <paramref name="broke"/> when the render failed this frame (nothing drawn; the caller draws the plain picture).
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

            // Loupe point (edge scan, a layer pass) computed here, never under the output mutex; the encoded frame was rendered without
            // it (Render's flat copy has no loupe key). Edge tuning takes priority; else the Magnifier at the pointer, converting output
            // fractions to pixels.
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

        // Loupe view: the compare picture (encoded or difference) around the same point, point-sampled.
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
        control.SetOutputContentSize(index, width, height, outputs.Generation, (header.Flags & FrameSceneHeader.FlagReadback) != 0, header.FrameSequenceNumber);
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

    // HDR export picture before P010 conversion: RGBA16F at output size, created on first use.
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

    // Last picture was held by the frame commit barrier: redraw on the next turn, whatever wakes the worker.
    private static bool _held;

    // Last output generation created (announced or not): names are never reused within a worker.
    private static int _lastGeneration;

    // ----- Readback (FlagReadback: exports, filmstrips) -----

    private static System.IO.MemoryMappedFiles.MemoryMappedFile? _readbackFile;
    private static System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _readbackView;
    private static int _readbackGeneration = -1;
    private static nint _readbackStaging;
    private static (int Width, int Height) _readbackSize;

    // NV12 planes rendered on the GPU, plus the chroma staging texture (luma uses _readbackStaging as R8 when sized for it). For P010 the
    // same two are R16 and R16G16 while _readbackIsP010 is set.
    private static GpuTexture? _nv12Luma, _nv12Chroma;
    private static nint _nv12ChromaStaging;
    private static bool _readbackIsNv12, _readbackIsP010;

    /// <summary>
    /// Copies the just-drawn frame into the client's readback section: staging copy, map (waits on the GPU), row-by-row write, and the
    /// sequence number set last. A missing or too-small section is skipped (logged once). <paramref name="p010Picture"/>
    /// (FlagReadbackP010): the RGBA16F frame, read back as P010 instead (NV12 layout, two bytes per sample).
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
            if (generation != _readbackGeneration || _readbackView is null || _readbackView.Capacity < needed)
            {
                _readbackView?.Dispose();
                _readbackFile?.Dispose();
                (_readbackView, _readbackFile, _readbackGeneration) = (null, null, -1);
                _readbackFile = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(CompositorProtocol.ReadbackSection(shared.BaseName, generation), System.IO.MemoryMappedFiles.MemoryMappedFileRights.ReadWrite);
                _readbackView = _readbackFile.CreateViewAccessor(0, capacity, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
                _readbackGeneration = generation;
            }

            // Raw-pointer write: never exceed the actually mapped view, whatever capacity the control block now claims (a capacity raised
            // without a new section would otherwise overrun it).
            if (needed > _readbackView.Capacity)
                return;

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
                // Private picture, not the shared output: no keyed mutex needed.
                renderer.ToP010(p010Picture!, _nv12Luma!, _nv12Chroma!);
                gpu.Context.CopyResource(_readbackStaging, _nv12Luma!.Texture);
                gpu.Context.CopyResource(_nv12ChromaStaging, _nv12Chroma!.Texture);
            }
            else
            {
                // Shared output texture: copy under its keyed mutex like every other access.
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
                    // UV plane after the Y plane, rows packed.
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
            AppLog.Write("Compositor worker: readback", ex);
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

    /// <summary>
    /// One-line fault summary for the shared note the client logs when the worker exits (max 511 chars): exception type, message and
    /// throwing frame. The full stack is in HandPeg.log, written by this process.
    /// </summary>
    internal static string FaultLine(Exception ex)
    {
        var top = ex.StackTrace?.TrimStart().Split('\n', 2)[0].Trim();
        return top is { Length: > 0 } ? $"{ex.GetType().Name}: {ex.Message} {top}" : $"{ex.GetType().Name}: {ex.Message}";
    }

    /// <summary>The three shared output textures of one worker generation, each with a keyed mutex and named handle.</summary>
    private sealed class OutputRing : IDisposable
    {
        private readonly (GpuTexture Texture, nint KeyedMutex, nint Handle)[] _ring = new (GpuTexture, nint, nint)[CompositorProtocol.OutputRing];

        public int Width { get; }
        public int Height { get; }
        public int Generation { get; }

        /// <summary>True once its generation and size are published in the control block (after its first frame).</summary>
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
                    HandPegApp.Native.Kernel32.CloseHandle(handle);
                texture?.Dispose();
            }
        }
    }

    private sealed class ProcessHandleWait : WaitHandle
    {
        public ProcessHandleWait(SafeWaitHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    private const uint Synchronize = 0x00100000;

}
