using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HandPegApp.Models.Effects;

namespace HandPegApp.Services;

// ----- Compositor control plane -----
//
// One named memory-mapped file created by the client (HandPeg) and opened by the worker (HandPeg.exe --compositor-worker).
// Layout is fixed-size, blittable and append-only: fields are never moved or renumbered; new fields use reserved space.
// No serialization: both sides read and write structs in place.
//
//   Control block       4 KB    who is running, the frame shown last, commands, timings, the last error
//   Scene slots     3 x 64 KB   FrameScene, a lock-free triple buffer (CompositorSharedMemory.Publish / TakeLatest)
//   Source table   64 x 1 KB    media files the layers draw from (path, stream, generation)
//   Resource table 64 x 128 B   textures the client hands over (masks, LUTs, curves, stills), each in its own section

/// <summary>Control-plane constants: sizes, offsets and kernel object names derived from one base name.</summary>
public static class CompositorProtocol
{
    /// <summary>"HPGC": a mapping that is not a compositor's control plane is refused.</summary>
    public const uint Magic = 0x43475048;

    /// <summary>
    /// Bumped only on an incompatible change; a worker of another version refuses the mapping. History: 2 seek hints and counters;
    /// 3 hints of 32 bytes with a stretch and a moment, at 3584; 4 NV12 readbacks; 6 a layer's shutter; 7 a layer's 3D tilt, 784 bytes
    /// a layer; 8 an output ring that can be larger than its frames, each slot's content size at 3176; 9 a source entry exactly
    /// <see cref="SourceEntryBytes"/> (paths of up to <see cref="MaxPathChars"/> - 1 characters).
    /// </summary>
    public const int Version = 9;

    /// <summary>NV12 frame size in bytes: Y plane then interleaved UV at half resolution (rounded up), rows packed.</summary>
    public static long Nv12Bytes(int width, int height) => (long)width * height + 2L * ((width + 1) / 2) * ((height + 1) / 2);

    /// <summary>P010 frame size: NV12 layout with two bytes per sample.</summary>
    public static long P010Bytes(int width, int height) => 2 * Nv12Bytes(width, height);

    public const int MaxLayers = 64;
    public const int MaxEffectsPerLayer = 16;
    public const int SceneBytes = 64 * 1024;
    public const int SceneSlots = 3;
    public const int MaxSources = 64;
    public const int MaxResources = 64;
    public const int OutputRing = 3;

    /// <summary>
    /// UTF-16 chars in a source entry's path field including the terminator: 32-byte header + 496 x 2 = 1024 =
    /// <see cref="SourceEntryBytes"/>. Longer paths are rejected (CompositorClient.SetSource), never truncated.
    /// </summary>
    public const int MaxPathChars = 496;

    /// <summary>How many seek hints the client can leave in the control block (<see cref="ControlBlock.Hints"/>).</summary>
    public const int MaxHints = 16;

    public const int ControlOffset = 0;
    public const int ControlBytes = 4096;
    public const int SceneOffset = ControlOffset + ControlBytes;
    public const int SourceEntryBytes = 1024;
    public const int SourceTableOffset = SceneOffset + SceneSlots * SceneBytes;
    public const int ResourceEntryBytes = 128;
    public const int ResourceTableOffset = SourceTableOffset + MaxSources * SourceEntryBytes;
    public const int TotalBytes = ResourceTableOffset + MaxResources * ResourceEntryBytes;

    /// <summary>Worker exit code after device removal/reset (TDR): respawn; not a fault.</summary>
    public const int DeviceLostExitCode = 0x0D3D;

    /// <summary>The worker's exit code when the control plane could not be opened or is of another version.</summary>
    public const int BadControlPlaneExitCode = 0x0BAD;

    public static string WakeEvent(string baseName) => baseName + ".Wake";

    public static string FrameEvent(string baseName) => baseName + ".Frame";

    public static string TakeOverEvent(string baseName, int standby) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}.TakeOver{standby}");

    /// <summary>Output texture name for ID3D11Device1::OpenSharedResourceByName; unique per worker generation.</summary>
    public static string OutputTexture(string baseName, int generation, int index) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}.Out{generation}.{index}");

    /// <summary>Resource pixel section (created by the client, read once per generation by the worker).</summary>
    public static string ResourceSection(string baseName, int id, int generation) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}.Res{id}.{generation}");

    /// <summary>Readback section for FlagReadback frames: BGRA8, rows packed, created by the client.</summary>
    public static string ReadbackSection(string baseName, int generation) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}.Readback{generation}");

    /// <summary>Section with a source's I-frame times (double seconds, file time base), when known to the client.</summary>
    public static string IFrameSection(string baseName, int id, int generation) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}.IFrames{id}.{generation}");
}

/// <summary>What the worker is doing (<see cref="ControlBlock.WorkerState"/>).</summary>
public enum CompositorWorkerState
{
    Starting = 0,
    Ready = 1,
    DeviceLost = 2,
    Failed = 3,
    Exiting = 4,
    /// <summary>A standby process, waiting to take over.</summary>
    Standby = 5,

    /// <summary>Set by the client supervisor after it stops respawning (too many consecutive failures).</summary>
    GaveUp = 6,
}

/// <summary>Client-to-worker command (<see cref="ControlBlock.Command"/>), consumed with Interlocked.Exchange.</summary>
public enum CompositorCommand
{
    None = 0,
    /// <summary>Simulates a TDR: reports device lost and exits with <see cref="CompositorProtocol.DeviceLostExitCode"/>.</summary>
    SimulateDeviceLost = 1,
    /// <summary>Fall over (tests of the respawn).</summary>
    SimulateCrash = 2,
    Shutdown = 3,
    /// <summary>Re-renders the latest scene unchanged (after a respawn or a resource upload).</summary>
    Redraw = 4,

    /// <summary>
    /// Releases every frame in the Instant Seek cache. Legacy path for tools; the client uses <see cref="ControlBlock.ClearRequests"/>
    /// (a counter that a later command cannot overwrite).
    /// </summary>
    ClearCache = 5,
}

/// <summary>Layer pixel source (<see cref="GpuLayerDescriptor.SourceKind"/>).</summary>
public enum GpuSourceKind
{
    None = 0,
    /// <summary>Source-table video, GPU-decoded at <see cref="GpuLayerDescriptor.MediaSeconds"/>.</summary>
    Media = 1,
    /// <summary>A flat colour (<see cref="GpuLayerDescriptor.SolidR"/>…).</summary>
    SolidColor = 2,
    /// <summary>Shader-generated test pattern (<see cref="GpuLayerDescriptor.PatternKind"/>) for tests and benchmarks.</summary>
    TestPattern = 3,
    /// <summary>Resource-table still (<see cref="GpuLayerDescriptor.StillResourceId"/>).</summary>
    Still = 4,
}

/// <summary>The kinds of texture the client hands over (<see cref="ResourceEntry.Kind"/>).</summary>
public enum GpuResourceKind
{
    None = 0,
    /// <summary>A mask picture, BGRA8 (its luma, or its alpha where it has one, is the mask).</summary>
    Mask2D = 1,
    /// <summary>A 3D LUT, RGBA32F, Width = Height = Depth = the cube's size.</summary>
    Lut3D = 2,
    /// <summary>A tone curve, RGBA32F, 256 x 1: R, G, B per channel, A the master curve.</summary>
    Curve1D = 3,
    /// <summary>A still picture, BGRA8, straight alpha.</summary>
    Still2D = 4,
}

/// <summary>
/// Control block at offset 0 (4 KB). The client writes <see cref="ClientPid"/>, <see cref="Command"/> and scene state; the worker
/// writes the rest. Cross-process fields use Volatile / Interlocked access.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = CompositorProtocol.ControlBytes)]
public struct ControlBlock
{
    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public int Version;
    [FieldOffset(8)] public int ClientPid;
    [FieldOffset(12)] public int WorkerPid;

    /// <summary>Triple-buffer state word: bits 0-1 last published slot, bit 2 set until it is taken.</summary>
    [FieldOffset(16)] public int SceneState;

    /// <summary>Slot held by the worker (last rendered scene); a takeover worker resumes from it.</summary>
    [FieldOffset(20)] public int ReaderSlot;

    [FieldOffset(24)] public int WorkerState;
    [FieldOffset(28)] public int Command;

    /// <summary>Worker heartbeat, incremented each loop iteration (at least every 100 ms); a stalled value means hung.</summary>
    [FieldOffset(32)] public long Heartbeat;

    /// <summary>The <see cref="FrameSceneHeader.FrameSequenceNumber"/> of the frame presented last.</summary>
    [FieldOffset(40)] public long PresentedSequence;

    /// <summary>Which output texture holds it (0 to <see cref="CompositorProtocol.OutputRing"/> - 1).</summary>
    [FieldOffset(48)] public int PresentedIndex;

    /// <summary>Output ring generation; each new worker creates new textures (<see cref="CompositorProtocol.OutputTexture"/>).</summary>
    [FieldOffset(52)] public int OutputGeneration;

    /// <summary>
    /// Output ring texture size, published with its generation. A frame may be smaller than its texture (a resized player keeps the
    /// ring while it fits); the slot's frame is its top-left <see cref="OutputContentSize"/>.
    /// </summary>
    [FieldOffset(56)] public int OutputWidth;
    [FieldOffset(60)] public int OutputHeight;

    /// <summary>Render adapter LUID; readers must open the output on the same adapter.</summary>
    [FieldOffset(64)] public long AdapterLuid;

    /// <summary>The HRESULT behind the last failure, 0 if none.</summary>
    [FieldOffset(72)] public int LastError;
    [FieldOffset(76)] public float LastFrameGpuMs;
    [FieldOffset(80)] public float LastFrameCpuMs;
    [FieldOffset(84)] public int FramesRendered;

    /// <summary>Raised by the client after it changes the source table (the worker re-reads it).</summary>
    [FieldOffset(88)] public int SourcesVersion;

    /// <summary>Raised by the client after it changes the resource table.</summary>
    [FieldOffset(92)] public int ResourcesVersion;

    /// <summary>How long the worker took from its start (or take-over) to its first presented frame, in ms.</summary>
    [FieldOffset(96)] public float StartupMs;

    /// <summary>How many times a decoder was opened, sought, and decoded a frame (diagnostics).</summary>
    [FieldOffset(100)] public int DecoderOpens;
    [FieldOffset(104)] public int DecoderSeeks;
    [FieldOffset(108)] public int DecodedFrames;

    /// <summary>The adapter index (DXGI order) the worker created its device on.</summary>
    [FieldOffset(112)] public int AdapterIndex;

    /// <summary>1 when the device is WARP (no hardware): tests still run, decode goes through the processor.</summary>
    [FieldOffset(116)] public int IsSoftwareDevice;

    /// <summary>
    /// Diagnostics for tools and goldens: frames back-filled over a decoder-dropped gap, seeks that overshot and restarted earlier,
    /// and stream holes no frame covers (shown with the nearest frame; an export counts these as failures).
    /// </summary>
    [FieldOffset(120)] public int Stretches;
    [FieldOffset(124)] public int LateLandings;
    [FieldOffset(128)] public int MissingFrames;

    /// <summary>Client flag: 1 forces CPU decode (hardware decoding disabled for drivers with corrupt output).</summary>
    [FieldOffset(132)] public int SoftwareDecodeRequest;

    // ----- 136..287: Export Quality Preview (ExportLoopbackPreview), worker-written while a scene requests it -----

    /// <summary>Encode loopback state (<see cref="ExportLoopbackState"/>); Off while no scene requests it.</summary>
    [FieldOffset(136)] public int LoopbackState;

    /// <summary>The HRESULT behind <see cref="ExportLoopbackState.Failed"/> (or a missing transform's step), 0 if none.</summary>
    [FieldOffset(140)] public int LoopbackHResult;

    /// <summary>Last frame's encode-to-decode round trip (ms).</summary>
    [FieldOffset(144)] public float LoopbackRoundTripMs;

    /// <summary>Frames round-tripped since the loopback started, and how many returned too late for their slot.</summary>
    [FieldOffset(148)] public int LoopbackFramesEncoded;
    [FieldOffset(152)] public int LoopbackLateFrames;

    /// <summary>Requested codec once both encoder and decoder were found: bit 1 H.264, 2 HEVC, 4 AV1.</summary>
    [FieldOffset(156)] public int LoopbackCodecsAvailable;

    /// <summary>Encoder MFT friendly name (UTF-16, NUL-terminated) shown on the preview.</summary>
    [FieldOffset(160)] public LoopbackNameChars LoopbackEncoderName;

    /// <summary>
    /// 1 when the running encoder matches the export's encoder family and adapter (QSV on Intel, ...) or the requested software encoder;
    /// 0 for a substitute on another adapter. Second flag: 1 when only paused frames are encoded (software encoder via FFmpeg).
    /// </summary>
    [FieldOffset(288)] public int LoopbackOnAskedCard;
    [FieldOffset(292)] public int LoopbackPausedOnly;

    // ----- 296..319: per-output-slot scene number (Codex UGLY-07) -----
    // The FrameSceneHeader.FrameSequenceNumber of the frame in each slot, written with its content size under the slot's keyed mutex, so
    // a reader holding the mutex gets the number of the picture it copies (never the global PresentedSequence of a newer slot).
    [FieldOffset(296)] public long OutputContentSequence0;
    [FieldOffset(304)] public long OutputContentSequence1;
    [FieldOffset(312)] public long OutputContentSequence2;

    /// <summary>Scene number of the frame in output slot <paramref name="index"/> (read under its keyed mutex); 0 if never drawn.</summary>
    public readonly long OutputContentSequence(int index) => index switch
    {
        0 => OutputContentSequence0,
        1 => OutputContentSequence1,
        _ => OutputContentSequence2,
    };

    // 320..511: free.

    // 512..1023: per-shader-slot GPU time of the last frame (ms), when the scene requests timing.
    [FieldOffset(512)] public SlotTimings SlotGpuMs;

    // 1024..1535: per-slot invocation count for that frame.
    [FieldOffset(1024)] public SlotCounts SlotRuns;

    // 2048..3071: last worker message (UTF-16, NUL-terminated).
    [FieldOffset(2048)] public MessageChars Message;

    // ----- 3072..4095: Instant Seek (protocol 2) -----

    /// <summary>Incremented by the client after rewriting <see cref="Hints"/>; the worker re-reads them without rendering.</summary>
    [FieldOffset(3072)] public int HintsVersion;
    [FieldOffset(3076)] public int HintCount;

    /// <summary>
    /// Client-requested frame cache cap (bytes): 0 automatic (grow on demand from baseline to the hardware ceiling, Compositor.MemoryTier),
    /// N = min(N, ceiling), -1 disables caching. The worker also stays within the OS-granted video memory budget.
    /// </summary>
    [FieldOffset(3080)] public long CacheBudgetRequest;

    /// <summary>Decode threads the client asks for: 0 automatic (two on a card, one on WARP), at most 4.</summary>
    [FieldOffset(3088)] public int DecodeThreadsRequest;

    /// <summary>1 when every media layer of the last presented frame was exact; 0 when any used the nearest available frame.</summary>
    [FieldOffset(3092)] public int PresentedExact;

    /// <summary>1 when, additionally, the next frames of every video are decoded (DecoderPool.NextFramesReady); updated every loop.</summary>
    [FieldOffset(3096)] public int PresentedAhead;

    /// <summary>
    /// Last-frame timing breakdown in ms (AvSync trace): keyed-mutex acquire, SceneRenderer.Render, flush. <see cref="LastFrameCpuMs"/>
    /// is the whole worker frame.
    /// </summary>
    [FieldOffset(3100)] public float LastAcquireMs;
    [FieldOffset(3104)] public float LastDrawMs;
    [FieldOffset(3108)] public float LastFlushMs;

    /// <summary>Decode-lane frames completed during the last draw (diagnostic for draw/decode contention).</summary>
    [FieldOffset(3112)] public int LastDrawDecodes;

    /// <summary>Textures created by the worker device since start (GpuDevice.TexturesMade; diagnostics and goldens).</summary>
    [FieldOffset(3116)] public int TexturesMade;

    // ----- 3120..3175: worker memory trim and adaptive cache -----
    // Requests are counters the client increments (Interlocked) and the worker echoes once served; unlike the single Command slot,
    // a counter is never overwritten. A starting worker (respawn, standby takeover) echoes pending counts immediately: it holds nothing.

    /// <summary>
    /// Client trim request (CompositorClient.TrimWorker, on tab deactivation): frees the cache free list, pooled and idle renderer
    /// textures, readback staging, driver allocations (IDXGIDevice3::Trim) and heap. On unified memory or cards of 4 GB or less the
    /// cached frames are freed too (CompositorWorker.TrimEmptiesCache).
    /// </summary>
    [FieldOffset(3120)] public int TrimRequests;

    /// <summary>
    /// <see cref="TrimRequests"/> count served, written after the trim completes and on start. Driver Trim and GC wait for 250 ms of
    /// idle with nothing playing, so rapid tab switches coalesce into one trim.
    /// </summary>
    [FieldOffset(3124)] public int TrimsDone;

    /// <summary>Worker private bytes (PrivateUsage, commit including video memory), updated every 2 s and after a trim.</summary>
    [FieldOffset(3128)] public long WorkerPrivateBytes;

    /// <summary>Cache free-list bytes (retired frames kept for reuse; excluded from <see cref="CacheBytes"/>).</summary>
    [FieldOffset(3136)] public long CacheFreeBytes;

    /// <summary>Client clear request (CompositorClient.ClearFrameCache): drops every frame and resets the budget to baseline.</summary>
    [FieldOffset(3144)] public int ClearRequests;

    /// <summary>The <see cref="ClearRequests"/> count the worker has served.</summary>
    [FieldOffset(3148)] public int ClearsDone;

    /// <summary>Current budget ceiling (bytes): hardware ceiling or the client maximum, whichever is lower.</summary>
    [FieldOffset(3152)] public long CacheMaximumBytes;

    /// <summary>The hardware's ceiling now (bytes): what Auto grows to at most.</summary>
    [FieldOffset(3160)] public long CacheCeilingBytes;

    /// <summary>1 GB growth steps above baseline since the cache was last cleared.</summary>
    [FieldOffset(3168)] public int CacheGrowSteps;

    /// <summary>Self-trims after 30 s idle while paused.</summary>
    [FieldOffset(3172)] public int IdleTrims;

    // ----- 3176..3199: per-output-slot content size -----
    // Size of the frame in a slot's top-left corner, written under the slot's keyed mutex before release, so a reader holding the
    // mutex always sees the matching size. 0 (never drawn) means the whole texture.
    [FieldOffset(3176)] public int OutputContentWidth0;
    [FieldOffset(3180)] public int OutputContentHeight0;
    [FieldOffset(3184)] public int OutputContentWidth1;
    [FieldOffset(3188)] public int OutputContentHeight1;
    [FieldOffset(3192)] public int OutputContentWidth2;
    [FieldOffset(3196)] public int OutputContentHeight2;

    /// <summary>
    /// Frame size in output slot <paramref name="index"/> (read under its keyed mutex), clamped to the ring
    /// (<see cref="OutputWidth"/> x <see cref="OutputHeight"/>); the whole ring when unwritten.
    /// </summary>
    public readonly (int Width, int Height) OutputContentSize(int index)
    {
        var (width, height) = index switch
        {
            0 => (OutputContentWidth0, OutputContentHeight0),
            1 => (OutputContentWidth1, OutputContentHeight1),
            _ => (OutputContentWidth2, OutputContentHeight2),
        };
        return width <= 0 || height <= 0 ? (OutputWidth, OutputHeight) : (Math.Min(width, OutputWidth), Math.Min(height, OutputHeight));
    }

    /// <summary>
    /// Worker side of <see cref="OutputContentSize"/> and <see cref="OutputContentTag"/>: written under the slot's keyed mutex before
    /// release, with the ring generation, whether the frame was a readback, and its scene number (<see cref="OutputContentSequence"/>).
    /// </summary>
    public void SetOutputContentSize(int index, int width, int height, int generation, bool readback, long sequence)
    {
        var tag = generation * 2 + (readback ? 1 : 0);
        switch (index)
        {
            case 0: (OutputContentWidth0, OutputContentHeight0, OutputContentTag0, OutputContentSequence0) = (width, height, tag, sequence); break;
            case 1: (OutputContentWidth1, OutputContentHeight1, OutputContentTag1, OutputContentSequence1) = (width, height, tag, sequence); break;
            default: (OutputContentWidth2, OutputContentHeight2, OutputContentTag2, OutputContentSequence2) = (width, height, tag, sequence); break;
        }
    }

    // ----- 3200..3211: per-output-slot tag -----
    // Ring generation * 2, + 1 for a readback frame (capture, thumbnail, scopes; never presented). Written with the content size, so a
    // reader on an older ring can detect that the size belongs to another generation.
    [FieldOffset(3200)] public int OutputContentTag0;
    [FieldOffset(3204)] public int OutputContentTag1;
    [FieldOffset(3208)] public int OutputContentTag2;

    /// <summary>Raw size written for output slot <paramref name="index"/> (read under its keyed mutex); 0 x 0 if unwritten.</summary>
    public readonly (int Width, int Height) OutputContentWritten(int index) => index switch
    {
        0 => (OutputContentWidth0, OutputContentHeight0),
        1 => (OutputContentWidth1, OutputContentHeight1),
        _ => (OutputContentWidth2, OutputContentHeight2),
    };

    /// <summary>Tag of output slot <paramref name="index"/> (read under its keyed mutex): generation * 2, + 1 for a readback.</summary>
    public readonly int OutputContentTag(int index) => index switch
    {
        0 => OutputContentTag0,
        1 => OutputContentTag1,
        _ => OutputContentTag2,
    };

    // ----- 3212..3219: worker adapter memory model and parked-tab source policy -----
    /// <summary>
    /// Worker-written after device creation: 1 for unified memory (GpuDevice.UnifiedMemory: integrated or ARM64 GPU), else 0.
    /// The editor process never loads DXCore; it reads this (GpuPreviewEngine.UnifiedMemory).
    /// </summary>
    [FieldOffset(3212)] public int UnifiedMemory;

    /// <summary>
    /// Client-written parked-tab policy (CompositorClient.TabParking): 0 keep everything (strip, export, bench, test clients);
    /// 1 player: lean parking (playhead frame only) on unified memory or battery; 2 always lean (goldens).
    /// </summary>
    [FieldOffset(3216)] public int TabParkingRequest;

    /// <summary>
    /// Client-written FFmpeg video arguments for a software-encoder Live Encoded Preview ("-c:v libx264 -preset veryslow -crf 22"),
    /// UTF-8, NUL-terminated, max 255 bytes. Seqlock: the worker reads only between equal even <see cref="LoopbackArgumentsVersion"/> values.
    /// </summary>
    [FieldOffset(3220)] public int LoopbackArgumentsVersion;
    [FieldOffset(3224)] public LoopbackArgumentBytes LoopbackSoftwareArguments;

    // 3480..: cache counters for SeekBench: exact hits, misses, stand-in presents, frames/bytes held, active budget, last exact-frame
    // latency, and exact hits that were decoded ahead (hints, pre-roll, play-ahead).
    [FieldOffset(3480)] public long CacheHits;
    [FieldOffset(3488)] public long CacheMisses;
    [FieldOffset(3496)] public long ApproximatePresents;
    [FieldOffset(3504)] public long CacheBytes;
    [FieldOffset(3512)] public long CacheBudgetBytes;
    [FieldOffset(3520)] public int CachedFrames;
    [FieldOffset(3524)] public float LastExactLatencyMs;
    [FieldOffset(3528)] public float WorstExactLatencyMs;
    [FieldOffset(3532)] public int BusyDecodeThreads;
    [FieldOffset(3536)] public long PredictedHits;
    [FieldOffset(3544)] public long Evictions;

    // ----- 3552..3583: client readback frame (FlagReadback: GPU exports, filmstrips) -----

    /// <summary>The <see cref="FrameSceneHeader.FrameSequenceNumber"/> of the frame last written into the readback section.</summary>
    [FieldOffset(3552)] public long ReadbackSequence;

    /// <summary>The readback section's size in bytes (set by the client with <see cref="ReadbackGeneration"/>).</summary>
    [FieldOffset(3560)] public long ReadbackCapacity;

    /// <summary>Incremented by the client whenever it recreates the section (<see cref="CompositorProtocol.ReadbackSection"/>).</summary>
    [FieldOffset(3568)] public int ReadbackGeneration;

    /// <summary>The frame written: its size, and the bytes of one row (BGRA8, rows packed).</summary>
    [FieldOffset(3572)] public int ReadbackWidth;
    [FieldOffset(3576)] public int ReadbackHeight;
    [FieldOffset(3580)] public int ReadbackRowBytes;

    // ----- 3584..4095: seek hints (protocol 3) -----

    /// <summary>
    /// Up to 16 predicted times/ranges (scrub target, upcoming cuts, timeline hover); <see cref="HintCount"/> valid, re-read when
    /// <see cref="HintsVersion"/> changes.
    /// </summary>
    [FieldOffset(3584)] public SeekHints Hints;
}

/// <summary>Seek hint kind (<see cref="GpuSeekHint.Kind"/>), declared in urgency order.</summary>
public enum GpuSeekHintKind
{
    None = 0,
    /// <summary>Where a scrub is heading (its speed carried a little ahead).</summary>
    ScrubAhead = 1,
    /// <summary>The moment under the pointer while it rests on the timeline: a click there is likely.</summary>
    Pointer = 2,
    /// <summary>
    /// Upcoming cut within the lookahead horizon: pre-decode the first <see cref="GpuSeekHint.Span"/> (capped at clip length) of the
    /// clip starting at <see cref="GpuSeekHint.At"/>; pinned in cache until the hint is withdrawn.
    /// </summary>
    CutEntry = 3,
    /// <summary>The playhead itself: its neighbourhood decoded in idle time (frame steps, J/K/L).</summary>
    Playhead = 4,
    /// <summary>Timeline hover: short play-ahead (<see cref="GpuSeekHint.Span"/>) from the hovered time so click-and-play starts instantly.</summary>
    HoverRoll = 5,
    /// <summary>
    /// Likely next tab or pending drop: <see cref="HoverRoll"/>-style play-ahead decoded only when idle (never while the active tab
    /// plays, scrubs or awaits a frame).
    /// </summary>
    TabRoll = 6,
}

/// <summary>
/// Seek hint (32 bytes, protocol 3): source-table entry, file time, and optional range length.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct GpuSeekHint
{
    [FieldOffset(0)] public int SourceId;
    [FieldOffset(4)] public int Kind;
    [FieldOffset(8)] public double MediaSeconds;

    /// <summary>Source path hash (<see cref="SourceEntry.PathHash"/>); hints for a since-changed entry are dropped.</summary>
    [FieldOffset(16)] public long PathHash;

    /// <summary>Range length past <see cref="MediaSeconds"/> in file seconds (0: single frame).</summary>
    [FieldOffset(24)] public float Span;

    /// <summary>Sequence time the frames are needed (e.g. a cut); earlier hints of the same kind decode first.</summary>
    [FieldOffset(28)] public float At;
}

[InlineArray(CompositorProtocol.MaxHints)]
public struct SeekHints
{
    private GpuSeekHint _first;
}

/// <summary>One float per shader slot number, 0 to 127.</summary>
[InlineArray(128)]
public struct SlotTimings
{
    private float _first;
}

[InlineArray(128)]
public struct SlotCounts
{
    private int _first;
}

[InlineArray(512)]
public struct MessageChars
{
    private char _first;
}

/// <summary>The encoder's name in the control block (64 UTF-16 characters, 128 bytes).</summary>
[InlineArray(64)]
public struct LoopbackNameChars
{
    private char _first;
}

/// <summary>Software-encoder FFmpeg video arguments in the control block: 256 bytes UTF-8, NUL-terminated.</summary>
[InlineArray(256)]
public struct LoopbackArgumentBytes
{
    private byte _first;
}

/// <summary>Export Quality Preview loopback state (<see cref="ControlBlock.LoopbackState"/>).</summary>
public enum ExportLoopbackState
{
    Off = 0,

    /// <summary>Encoder and decoder being created (first frame after enable or a settings change).</summary>
    Starting = 1,

    /// <summary>Frames go through the encoder and the decoder and the player shows them.</summary>
    Running = 2,

    /// <summary>No hardware encoder of the codec on this PC: the plain picture is shown.</summary>
    NoEncoder = 3,

    /// <summary>Encoder found but no decoder (HEVC/AV1 need Store extensions); the source picture is shown.</summary>
    NoDecoder = 4,

    /// <summary>The export's codec has no Media Foundation loopback (VP9, ProRes, DNxHR, GIF, a stream copy).</summary>
    Unsupported = 5,

    /// <summary>Encoder or decoder failed (<see cref="ControlBlock.LoopbackHResult"/>); the source picture is shown.</summary>
    Failed = 6,

    /// <summary>Not in this view (the 3D exploded view, a clip's source shown alone).</summary>
    Unavailable = 7,
}

/// <summary>
/// Per-frame scene header of a 64 KB slot: canvas, clock, camera. Layers follow bottom-first at <see cref="FrameScene.LayersOffset"/>.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = FrameScene.HeaderBytes)]
public struct FrameSceneHeader
{
    /// <summary>Incremented per published scene; echoed by the worker in <see cref="ControlBlock.PresentedSequence"/>.</summary>
    [FieldOffset(0)] public long FrameSequenceNumber;
    [FieldOffset(8)] public double PlayheadSeconds;
    [FieldOffset(16)] public int IsPlaying;

    /// <summary>Canvas size in pixels (1920 x 1080, 1080 x 1920, ...); layer boxes use this space.</summary>
    [FieldOffset(20)] public int CanvasWidth;
    [FieldOffset(24)] public int CanvasHeight;

    /// <summary>0 flat composite; 1 3D Exploded View layer stack.</summary>
    [FieldOffset(28)] public int ViewMode;
    [FieldOffset(32)] public float CameraYaw;
    [FieldOffset(36)] public float CameraPitch;
    [FieldOffset(40)] public float CameraZoom;
    [FieldOffset(44)] public float ExplodedZSpread;
    [FieldOffset(48)] public int ActiveLayerCount;

    /// <summary>
    /// Bit 0: time every pass (<see cref="ControlBlock.SlotGpuMs"/>); bit 1: wait for timings before presenting; bit 2: present the
    /// raw premultiplied canvas with alpha and no background (goldens).
    /// </summary>
    [FieldOffset(52)] public int Flags;

    /// <summary>What shows where no layer covers the frame (straight RGB, 0 to 1).</summary>
    [FieldOffset(56)] public float BackgroundR;
    [FieldOffset(60)] public float BackgroundG;
    [FieldOffset(64)] public float BackgroundB;

    /// <summary>The size rendered, in pixels (the player's size); 0 for the canvas's own size.</summary>
    [FieldOffset(72)] public int OutputWidth;
    [FieldOffset(76)] public int OutputHeight;

    /// <summary>
    /// With <see cref="FlagLetterbox"/>: player zoom (1 = fit) and pan as fractions of the fitted picture (centre offset = pan x size x zoom).
    /// </summary>
    [FieldOffset(80)] public float ViewScale;
    [FieldOffset(84)] public float ViewPanX;
    [FieldOffset(88)] public float ViewPanY;

    /// <summary>
    /// Edge loupe: while an edge slider is held, the clip's layer (<see cref="GpuLayerDescriptor.ClipKey"/>; 0 none) is redrawn alone
    /// at <see cref="LoupeZoom"/> x player scale around a GPU-found edge point, over a checkerboard, in a corner inset.
    /// <see cref="LoupeKind"/>: 1 feather, 2 mask, 3 matte, 4 key.
    /// </summary>
    [FieldOffset(96)] public long LoupeClipKey;
    [FieldOffset(104)] public int LoupeKind;
    [FieldOffset(108)] public float LoupeZoom;

    /// <summary>
    /// 3D Exploded View depth in plan slots (one per track, GpuScenePlan.PlaceLayers), independent of active layers so the camera
    /// centre (ExplodedCamera) stays fixed. 0 uses <see cref="ActiveLayerCount"/> (tests).
    /// </summary>
    [FieldOffset(112)] public int ExplodedDepth;

    /// <summary>
    /// Audio-reactive bus (AudioReactive.cs): band envelopes 0-1 at this frame (Bass 20-160 Hz, Mid 160 Hz-2 kHz, High 2-10 kHz;
    /// 5 ms attack, 120 ms release) driving Audio Reactive Pulse effects (SceneRenderer.Fx.cs). Uses reserved header bytes.
    /// </summary>
    [FieldOffset(116)] public float AudioBass;
    [FieldOffset(120)] public float AudioMid;
    [FieldOffset(124)] public float AudioHigh;

    /// <summary>
    /// Export Quality Preview (<see cref="FlagLoopback"/>): codec (1 H.264, 2 HEVC, 3 AV1), rate control (eAVEncCommonRateControlMode:
    /// 0 CBR, 2 unconstrained VBR, 3 quality), bitrate (kbps), quality 1-100, and quality-vs-speed 0-100. Reserved header bytes; read only with the flag.
    /// </summary>
    [FieldOffset(128)] public int LoopbackCodec;
    [FieldOffset(132)] public int LoopbackRateControl;
    [FieldOffset(136)] public int LoopbackBitrateKbps;
    [FieldOffset(140)] public int LoopbackQuality;
    [FieldOffset(144)] public int LoopbackQualityVsSpeed;

    /// <summary>Display mode: 0 encoded, 1 split wipe (source left of <see cref="LoopbackWipeX"/>), 2 difference x <see cref="LoopbackHeatGain"/>.</summary>
    [FieldOffset(148)] public int LoopbackMode;

    /// <summary>Where the wipe stands, 0..1 of the picture's width.</summary>
    [FieldOffset(152)] public float LoopbackWipeX;

    /// <summary>The export's frame rate: the rate control's budget per frame, and the samples' clock.</summary>
    [FieldOffset(156)] public float LoopbackFrameRate;

    /// <summary>Incremented when the picture may have changed; wipe/mode changes alone do not re-encode the paused frame.</summary>
    [FieldOffset(160)] public long LoopbackContentStamp;

    /// <summary>How much the difference is magnified (10 by default).</summary>
    [FieldOffset(168)] public float LoopbackHeatGain;

    /// <summary>Bit 0: bypass the codec (decoded = source NV12) so goldens can test the compare pass on any machine.</summary>
    [FieldOffset(172)] public int LoopbackTestFlags;

    /// <summary>
    /// Export Quality Preview magnifier point (<see cref="FlagLoopbackLoupe"/>) as output fractions, so DIPs and pixels never mix.
    /// The loupe magnifies the compare at <see cref="LoupeZoom"/>; an active edge loupe (<see cref="LoupeClipKey"/>) takes precedence.
    /// </summary>
    [FieldOffset(176)] public float LoopbackLoupeX;
    [FieldOffset(180)] public float LoopbackLoupeY;

    /// <summary>
    /// Dynamic Preview Scaling: fraction of the output a flat player frame renders at, upscaled by the final pass (0 or 1 = full).
    /// Ignored for readbacks, exact frames, 3D view and the loupe (SceneRenderer.DrawScale).
    /// </summary>
    [FieldOffset(184)] public float PreviewScale;

    /// <summary>
    /// Loopback encoder (<see cref="FlagLoopback"/>): family (ExportLoopbackSettings.Family: 0 MF on the compositor GPU, 1 QSV on Intel,
    /// 2 AMF on AMD, 3 NVENC on NVIDIA, 4 FFmpeg software via <see cref="ControlBlock.LoopbackSoftwareArguments"/>), export frame size
    /// (0 = canvas), 10-bit (P010/Main10), CRF, and a settings stamp; a stamp change re-encodes the paused frame without redrawing layers.
    /// </summary>
    [FieldOffset(188)] public int LoopbackFamily;
    [FieldOffset(192)] public int LoopbackEncodedWidth;
    [FieldOffset(196)] public int LoopbackEncodedHeight;
    [FieldOffset(200)] public int LoopbackTenBit;
    [FieldOffset(204)] public int LoopbackCrf;
    [FieldOffset(208)] public long LoopbackSettingsStamp;

    // 216..255: free.

    /// <summary>
    /// Bit 3 (<see cref="FlagExactFrames"/>): render only when every media layer has its exact frame (exports, filmstrips, tests);
    /// otherwise the nearest frame is shown and the frame re-renders when the exact one arrives (Instant Seek).
    /// </summary>
    /// <summary>
    /// Bit 4 (<see cref="FlagLetterbox"/>): fit the canvas into the output as the player does (centred, black bars, <see cref="ViewScale"/>,
    /// <see cref="ViewPanX"/> / <see cref="ViewPanY"/>, clipped to the picture); otherwise stretch to the output (tests, exports, filmstrips).
    /// </summary>
    /// <summary>
    /// Bit 5 (<see cref="FlagReadback"/>): also copy the frame to the client readback section (BGRA8, output size) and set
    /// <see cref="ControlBlock.ReadbackSequence"/> (GPU exports, filmstrips; never on the player path).
    /// Bit 6 (<see cref="FlagReadbackNv12"/>, with bit 5): read back GPU-converted NV12 (62 % fewer bytes); <see cref="ControlBlock.ReadbackRowBytes"/>
    /// then equals the width.
    /// </summary>
    /// <summary>
    /// Bit 8 (<see cref="FlagLoopback"/>): Export Quality Preview. Render once at canvas size, round-trip through the export codec's
    /// encoder and decoder (<see cref="LoopbackCodec"/>, ...), letterbox the compare (ExportLoopbackPreview); status in
    /// <see cref="ControlBlock.LoopbackState"/>. Requires <see cref="FlagLetterbox"/> and the flat view; never with a readback.
    /// Bit 9 (<see cref="FlagReadbackP010"/>, with bit 5): HDR readback as P010: 16-bit float target, BT.2020 NCL limited range, 10 bits
    /// MSB-aligned in 16; <see cref="ControlBlock.ReadbackRowBytes"/> is then twice the width.
    /// Bit 10 (<see cref="FlagLoopbackLoupe"/>, with bit 8): magnifier at <see cref="LoopbackLoupeX"/> / <see cref="LoopbackLoupeY"/>.
    /// </summary>
    /// <summary>
    /// Bit 7 (<see cref="FlagDepthRails"/>): draw 3D depth rails (four hairlines from the canvas corners back through the stack);
    /// always set by the editor, omitted by plane-only tests.
    /// </summary>
    public const int FlagTimePasses = 1, FlagWaitForTimings = 2, FlagRawOutput = 4, FlagExactFrames = 8, FlagLetterbox = 16, FlagReadback = 32, FlagReadbackNv12 = 64,
        FlagDepthRails = 128, FlagLoopback = 256, FlagReadbackP010 = 512, FlagLoopbackLoupe = 1024;
}

/// <summary>
/// One layer: source, source crop, canvas box, blend, and up to 16 ordered effect packets (<see cref="GpuEffectPacket"/>, from
/// <c>EffectInstance.EvaluateGpuPacket</c> and <c>TransitionStyles.GpuPacket</c>). Composite-stage packets (TransformComposite,
/// DropShadow, BlendMode, transitions 64-68) apply at composite time regardless of list position.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = FrameScene.LayerBytes)]
public struct GpuLayerDescriptor
{
    /// <summary>A hash of the clip's id (diagnostics and per-clip history such as decode position).</summary>
    [FieldOffset(0)] public long ClipKey;

    /// <summary>Source path hash (<see cref="SourceEntry.PathHash"/>), guarding against a changed source-table entry.</summary>
    [FieldOffset(8)] public long SourcePathHash;
    [FieldOffset(16)] public int SourceId;
    [FieldOffset(20)] public int SourceKind;

    /// <summary>Source time to show, in file seconds (first frame = 0).</summary>
    [FieldOffset(24)] public double MediaSeconds;

    /// <summary>The part of the source shown, as fractions of its width and height: left, top, right and bottom edges.</summary>
    [FieldOffset(32)] public float CropLeft;
    [FieldOffset(36)] public float CropTop;
    [FieldOffset(40)] public float CropRight;
    [FieldOffset(44)] public float CropBottom;

    /// <summary>The layer's box on the canvas in canvas pixels (before rotation): its top left corner and size.</summary>
    [FieldOffset(48)] public float X;
    [FieldOffset(52)] public float Y;
    [FieldOffset(56)] public float Width;
    [FieldOffset(60)] public float Height;

    /// <summary>Degrees clockwise about the anchor.</summary>
    [FieldOffset(64)] public float Rotation;

    /// <summary>The point it turns about, as fractions of the box (0.5, 0.5 its middle).</summary>
    [FieldOffset(68)] public float AnchorX;
    [FieldOffset(72)] public float AnchorY;

    /// <summary>0 to 1, multiplied with the TransformComposite packet's opacity when there is one.</summary>
    [FieldOffset(76)] public float Opacity;

    /// <summary>0 normal, 1 multiply, 2 screen, 3 add, 4 overlay, 5 soft light, 6 hard light, 7 difference (the Blend Mode card's choices).</summary>
    [FieldOffset(80)] public int BlendMode;

    /// <summary>Blur-fill background radius in canvas pixels (0 off).</summary>
    [FieldOffset(84)] public float BlurFillRadius;
    [FieldOffset(88)] public int BlurFillPasses;

    /// <summary>The blurred background's brightness change, -0.5 to 0 (eq's brightness).</summary>
    [FieldOffset(92)] public float BlurFillDim;

    /// <summary>The mask (a <see cref="GpuResourceKind.Mask2D"/> resource) for the LayerMask packet; -1 none.</summary>
    [FieldOffset(96)] public int MaskResourceId;

    /// <summary>The LUT (a <see cref="GpuResourceKind.Lut3D"/> resource) for the Lut3D packet; -1 none.</summary>
    [FieldOffset(100)] public int LutResourceId;

    /// <summary>The curve (a <see cref="GpuResourceKind.Curve1D"/> resource) for the ToneCurve packet; -1 none.</summary>
    [FieldOffset(104)] public int CurveResourceId;

    /// <summary>The picture of a <see cref="GpuSourceKind.Still"/> layer; -1 none.</summary>
    [FieldOffset(108)] public int StillResourceId;

    /// <summary>A <see cref="GpuSourceKind.SolidColor"/> layer's colour, or a pattern's tint (straight RGBA, 0 to 1).</summary>
    [FieldOffset(112)] public float SolidR;
    [FieldOffset(116)] public float SolidG;
    [FieldOffset(120)] public float SolidB;
    [FieldOffset(124)] public float SolidA;

    /// <summary>A <see cref="GpuSourceKind.TestPattern"/>: 0 colour bars, 1 gradient ramp, 2 checkerboard, 3 radial target.</summary>
    [FieldOffset(128)] public int PatternKind;

    /// <summary>Bit 0 visible (else skipped); bit 1 adjustment layer (effects apply to the canvas under its box); other bits below.</summary>
    [FieldOffset(132)] public int Flags;

    /// <summary>3D Exploded View stack slot.</summary>
    [FieldOffset(136)] public float ZDepth;
    [FieldOffset(140)] public int EffectCount;
    [FieldOffset(144)] public EffectPackets Effects;

    // ----- Motion Blur shutter: layer placement at shutter open/close and the previous source time -----

    /// <summary>Box and rotation at shutter open (<see cref="FlagShutterPlace"/>); sub-frames interpolate toward the closing placement.</summary>
    [FieldOffset(720)] public float ShutterFromX;
    [FieldOffset(724)] public float ShutterFromY;
    [FieldOffset(728)] public float ShutterFromWidth;
    [FieldOffset(732)] public float ShutterFromHeight;
    [FieldOffset(736)] public float ShutterFromRotation;
    [FieldOffset(740)] public float ShutterToX;
    [FieldOffset(744)] public float ShutterToY;
    [FieldOffset(748)] public float ShutterToWidth;
    [FieldOffset(752)] public float ShutterToHeight;
    [FieldOffset(756)] public float ShutterToRotation;

    /// <summary>
    /// With <see cref="FlagPreviousMoment"/>: file time one sequence frame earlier (media layers, so speed ramps blur correctly), or
    /// sequence time one frame earlier (adjustment layers, to detect jumps).
    /// </summary>
    [FieldOffset(760)] public double PreviousMoment;

    /// <summary>
    /// True 3D tilt in degrees about the anchor: Pitch (top away) and Yaw (right edge away), applied after <see cref="Rotation"/> (Roll)
    /// and drawn in perspective (Matrix4.Box). Both 0 renders flat.
    /// </summary>
    [FieldOffset(768)] public float TiltX;
    [FieldOffset(772)] public float TiltY;

    /// <summary>
    /// Bit 2 (<see cref="FlagBlurFillOnly"/>): draw only the full-frame blurred background. Bit 3 (<see cref="FlagSelected"/>): selected
    /// clip (outlined in 3D view). Bit 4 (<see cref="FlagSelectedTrack"/>): another clip on the selected track (fainter outline so the
    /// plane never blinks across cuts). Bits 5-6: shutter fields valid (<see cref="FlagShutterPlace"/>, <see cref="FlagPreviousMoment"/>).
    /// </summary>
    public const int FlagVisible = 1, FlagAdjustment = 2, FlagBlurFillOnly = 4, FlagSelected = 8, FlagSelectedTrack = 16, FlagShutterPlace = 32, FlagPreviousMoment = 64;

    /// <summary>
    /// Deinterlace: top field only, line-doubled, to remove combing (PSYuvSource).
    /// </summary>
    public const int FlagDeinterlace = 128;

    /// <summary>
    /// Tone-map PQ/HLG to BT.709 SDR with the Hable curve, matching FFmpeg zscale+tonemap (PSYuvSource); SDR sources pass through.
    /// </summary>
    public const int FlagToneMapSdr = 256;

    /// <summary>
    /// Smooth Slow-Mo: below 1x speed, synthesize in-between frames by optical flow (SceneRenderer.Interpolate.cs) instead of repeating.
    /// </summary>
    public const int FlagInterpolate = 512;
}

/// <summary>The 16 packets of a layer, 36 bytes each (<see cref="GpuEffectPacket.SizeInBytes"/>), back to back.</summary>
[InlineArray(CompositorProtocol.MaxEffectsPerLayer)]
public struct EffectPackets
{
    private GpuEffectPacket _first;
}

/// <summary>The sizes and offsets of a scene slot.</summary>
public static class FrameScene
{
    public const int HeaderBytes = 256;
    public const int LayerBytes = 784;
    public const int LayersOffset = HeaderBytes;
}

/// <summary>A media file the layers can draw from: an entry of the source table (1 KB).</summary>
[StructLayout(LayoutKind.Explicit, Size = CompositorProtocol.SourceEntryBytes)]
public struct SourceEntry
{
    /// <summary>Raised by the client whenever the entry changes; 0 an empty entry.</summary>
    [FieldOffset(0)] public int Generation;
    [FieldOffset(4)] public int Kind;

    /// <summary>Which video stream of the file (0 the first).</summary>
    [FieldOffset(8)] public int StreamIndex;

    /// <summary>I-frame time count in the source's section (<see cref="CompositorProtocol.IFrameSection"/>); 0 none.</summary>
    [FieldOffset(12)] public int IFrameCount;
    [FieldOffset(16)] public long PathHash;

    /// <summary>Where the file's own time 0 lies in its container (its first video frame's time), seconds.</summary>
    [FieldOffset(24)] public double VideoStart;

    [FieldOffset(32)] public PathChars Path;
}

[InlineArray(CompositorProtocol.MaxPathChars)]
public struct PathChars
{
    private char _first;
}

/// <summary>Resource-table entry (128 B) for a client-supplied texture; pixels live in a separate section.</summary>
[StructLayout(LayoutKind.Explicit, Size = CompositorProtocol.ResourceEntryBytes)]
public struct ResourceEntry
{
    [FieldOffset(0)] public int Generation;
    [FieldOffset(4)] public int Kind;
    [FieldOffset(8)] public int Width;
    [FieldOffset(12)] public int Height;
    [FieldOffset(16)] public int Depth;

    /// <summary>The section's size in bytes (the pixels, rows packed with no padding).</summary>
    [FieldOffset(20)] public int Bytes;
    [FieldOffset(24)] public long ContentHash;
}

/// <summary>
/// Layout guard: verifies each shared struct fits its stride/slot (an inline array can grow past an explicit Size, which is only a
/// minimum, and overwrite its neighbour).
/// </summary>
public static class SharedLayout
{
    private static bool _verified;

    /// <summary>Throws <see cref="InvalidOperationException"/> when a shared struct does not fit the layout's constants.</summary>
    public static void Verify()
    {
        if (_verified)
            return;

        var problems = new List<string>();
        void Fits(string name, int size, int room, bool exact)
        {
            if (exact ? size != room : size > room)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} is {size} bytes, its room {room}{(exact ? " (must match)" : "")}"));
        }

        Fits(nameof(ControlBlock), Unsafe.SizeOf<ControlBlock>(), CompositorProtocol.ControlBytes, exact: false);
        Fits(nameof(FrameSceneHeader), Unsafe.SizeOf<FrameSceneHeader>(), FrameScene.HeaderBytes, exact: false);
        Fits(nameof(GpuLayerDescriptor), Unsafe.SizeOf<GpuLayerDescriptor>(), FrameScene.LayerBytes, exact: true);
        Fits("a scene slot's layers", FrameScene.LayersOffset + CompositorProtocol.MaxLayers * FrameScene.LayerBytes, CompositorProtocol.SceneBytes, exact: false);
        Fits(nameof(SourceEntry), Unsafe.SizeOf<SourceEntry>(), CompositorProtocol.SourceEntryBytes, exact: true);
        Fits(nameof(ResourceEntry), Unsafe.SizeOf<ResourceEntry>(), CompositorProtocol.ResourceEntryBytes, exact: true);
        if (problems.Count > 0)
            throw new InvalidOperationException("The compositor's shared layout does not fit its structs: " + string.Join("; ", problems));
        _verified = true;
    }
}

/// <summary>
/// Shared mapping as held by either process: typed views plus the lock-free scene triple buffer. The client owns the writer slot,
/// the worker the reader slot; neither side ever blocks.
/// </summary>
public sealed unsafe class CompositorSharedMemory : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private int _writerSlot;

    public string BaseName { get; }

    private CompositorSharedMemory(string baseName, MemoryMappedFile file)
    {
        SharedLayout.Verify();
        BaseName = baseName;
        _file = file;
        _view = file.CreateViewAccessor(0, CompositorProtocol.TotalBytes, MemoryMappedFileAccess.ReadWrite);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;
    }

    /// <summary>Creates a zeroed control plane (client side) with the triple buffer initialized.</summary>
    public static CompositorSharedMemory Create(string baseName)
    {
        var memory = new CompositorSharedMemory(baseName, MemoryMappedFile.CreateNew(baseName, CompositorProtocol.TotalBytes, MemoryMappedFileAccess.ReadWrite));
        new Span<byte>(memory._base, CompositorProtocol.TotalBytes).Clear();
        ref var control = ref memory.Control;
        control.Magic = CompositorProtocol.Magic;
        control.Version = CompositorProtocol.Version;
        control.ClientPid = Environment.ProcessId;

        // Writer in slot 0, the last published 1 (not new), the reader in 2.
        memory._writerSlot = 0;
        control.SceneState = 1;
        control.ReaderSlot = 2;
        return memory;
    }

    /// <summary>Opens the client's control plane (worker side); null when missing or of another protocol version.</summary>
    public static CompositorSharedMemory? Open(string baseName)
    {
        try
        {
            var memory = new CompositorSharedMemory(baseName, MemoryMappedFile.OpenExisting(baseName, MemoryMappedFileRights.ReadWrite));
            if (memory.Control.Magic == CompositorProtocol.Magic && memory.Control.Version == CompositorProtocol.Version)
                return memory;
            memory.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public byte* Base => _base;

    public ref ControlBlock Control => ref Unsafe.AsRef<ControlBlock>(_base + CompositorProtocol.ControlOffset);

    /// <summary>A scene slot's 64 KB.</summary>
    public Span<byte> Scene(int slot) => new(_base + CompositorProtocol.SceneOffset + slot * CompositorProtocol.SceneBytes, CompositorProtocol.SceneBytes);

    public ref FrameSceneHeader SceneHeader(int slot) => ref Unsafe.AsRef<FrameSceneHeader>(_base + CompositorProtocol.SceneOffset + slot * CompositorProtocol.SceneBytes);

    public ref GpuLayerDescriptor Layer(int slot, int index) =>
        ref Unsafe.AsRef<GpuLayerDescriptor>(_base + CompositorProtocol.SceneOffset + slot * CompositorProtocol.SceneBytes + FrameScene.LayersOffset + index * FrameScene.LayerBytes);

    public ref SourceEntry Source(int id) => ref Unsafe.AsRef<SourceEntry>(_base + CompositorProtocol.SourceTableOffset + id * CompositorProtocol.SourceEntryBytes);

    public ref ResourceEntry Resource(int id) => ref Unsafe.AsRef<ResourceEntry>(_base + CompositorProtocol.ResourceTableOffset + id * CompositorProtocol.ResourceEntryBytes);

    private ref int State => ref Control.SceneState;

    // ----- Writer (client) -----

    /// <summary>The slot the client writes its next scene into (its own until <see cref="Publish"/>).</summary>
    public int WriterSlot => _writerSlot;

    /// <summary>Publishes the writer slot; the client receives the previously published or worker-released slot.</summary>
    public void Publish()
    {
        Thread.MemoryBarrier();
        var previous = Interlocked.Exchange(ref State, _writerSlot | 4);
        _writerSlot = previous & 3;
    }

    // ----- Reader (worker) -----

    /// <summary>
    /// Slot with the newest scene: a newly published one (taken so the client cannot overwrite it), else the last taken;
    /// <paramref name="isNew"/> reports which.
    /// </summary>
    public int TakeLatest(out bool isNew)
    {
        ref var control = ref Control;
        var reader = Volatile.Read(ref control.ReaderSlot);
        if ((Volatile.Read(ref State) & 4) == 0)
        {
            isNew = false;
            return reader;
        }

        var previous = Interlocked.Exchange(ref State, reader);
        Thread.MemoryBarrier();
        reader = previous & 3;
        Volatile.Write(ref control.ReaderSlot, reader);
        isNew = true;
        return reader;
    }

    // ----- Strings in fixed fields -----

    public static void WriteString(Span<char> field, string text)
    {
        var length = Math.Min(text.Length, field.Length - 1);
        text.AsSpan(0, length).CopyTo(field);
        field[length] = '\0';
    }

    public static string ReadString(ReadOnlySpan<char> field)
    {
        var end = field.IndexOf('\0');
        return new string(end >= 0 ? field[..end] : field);
    }

    /// <summary>The 64-bit FNV-1a hash of a path, case-folded (paths on Windows are compared without case).</summary>
    public static long PathHash(string path)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var c in path)
        {
            hash ^= char.ToUpperInvariant(c);
            hash *= 0x100000001b3UL;
        }

        return (long)hash;
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}

