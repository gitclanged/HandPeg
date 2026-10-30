using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HandPegApp.Models.Effects;

namespace HandPegApp.Services;

// ----- The v5.0 compositor's control plane (Pass 5.0-Core, ARCHITECTURE_CONTRACTS.md section 6) -----
//
// One named memory-mapped file, made by HandPeg (the client) and opened by the compositor worker
// (HandPeg.exe --compositor-worker). Everything in it is fixed-size, blittable and append-only: a field is never moved
// or renumbered, new ones take reserved room. Nothing is serialized: the client writes structs in place and the worker
// reads them in place.
//
//   Control block       4 KB    who is running, the frame shown last, commands, timings, the last error
//   Scene slots     3 x 64 KB   FrameScene, a lock-free triple buffer (CompositorSharedMemory.Publish / TakeLatest)
//   Source table   64 x 1 KB    media files the layers draw from (path, stream, generation)
//   Resource table 64 x 128 B   textures the client hands over (masks, LUTs, curves, stills), each in its own section

/// <summary>The fixed numbers of the control plane: sizes, offsets and the names derived from one base name.</summary>
public static class CompositorProtocol
{
    /// <summary>"HPGC": a mapping that is not a compositor's control plane is refused.</summary>
    public const uint Magic = 0x43475048;

    /// <summary>
    /// Raised only by an incompatible change; a worker of another version refuses the mapping (2: Instant Seek's hints and
    /// counters, 5.0-AudioAnd3D; 3: hints of 32 bytes with a stretch and a moment, at 3584, 5.0-Release).
    /// </summary>
    public const int Version = 3;

    public const int MaxLayers = 64;
    public const int MaxEffectsPerLayer = 16;
    public const int SceneBytes = 64 * 1024;
    public const int SceneSlots = 3;
    public const int MaxSources = 64;
    public const int MaxResources = 64;
    public const int OutputRing = 3;
    public const int MaxPathChars = 500;

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

    /// <summary>The worker's exit code after the graphics card was removed or reset (a TDR): respawn, it is not a bug.</summary>
    public const int DeviceLostExitCode = 0x0D3D;

    /// <summary>The worker's exit code when the control plane could not be opened or is of another version.</summary>
    public const int BadControlPlaneExitCode = 0x0BAD;

    public static string WakeEvent(string baseName) => baseName + ".Wake";

    public static string FrameEvent(string baseName) => baseName + ".Frame";

    public static string TakeOverEvent(string baseName, int standby) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}.TakeOver{standby}");

    /// <summary>An output texture's name (ID3D11Device1::OpenSharedResourceByName): new names for every worker generation.</summary>
    public static string OutputTexture(string baseName, int generation, int index) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}.Out{generation}.{index}");

    /// <summary>The section a resource's pixels are in (made by the client, read once by the worker per generation).</summary>
    public static string ResourceSection(string baseName, int id, int generation) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}.Res{id}.{generation}");

    /// <summary>The section a frame is read back into (FlagReadback): BGRA8, rows packed, made by the client.</summary>
    public static string ReadbackSection(string baseName, int generation) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}.Readback{generation}");

    /// <summary>The section a source's I-frame times are in (doubles, seconds in the file's own time), when the client knows them.</summary>
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

    /// <summary>Written by the client's supervisor when it stopped bringing the engine back (it failed too often in a row).</summary>
    GaveUp = 6,
}

/// <summary>A command the client leaves for the worker (<see cref="ControlBlock.Command"/>), taken with Interlocked.Exchange.</summary>
public enum CompositorCommand
{
    None = 0,
    /// <summary>Behave as after a TDR: report the device lost and exit with <see cref="CompositorProtocol.DeviceLostExitCode"/>.</summary>
    SimulateDeviceLost = 1,
    /// <summary>Fall over (tests of the respawn).</summary>
    SimulateCrash = 2,
    Shutdown = 3,
    /// <summary>Render the latest scene again even though nothing changed (after a respawn, or a resource arrived).</summary>
    Redraw = 4,

    /// <summary>Let go of every frame Instant Seek's cache holds (Settings' Clear): the video memory goes back at once.</summary>
    ClearCache = 5,
}

/// <summary>Where a layer's picture comes from (<see cref="GpuLayerDescriptor.SourceKind"/>).</summary>
public enum GpuSourceKind
{
    None = 0,
    /// <summary>A video file of the source table, decoded on the graphics card at <see cref="GpuLayerDescriptor.MediaSeconds"/>.</summary>
    Media = 1,
    /// <summary>A flat colour (<see cref="GpuLayerDescriptor.SolidR"/>…).</summary>
    SolidColor = 2,
    /// <summary>A picture drawn by a shader (<see cref="GpuLayerDescriptor.PatternKind"/>): tests and the benchmark.</summary>
    TestPattern = 3,
    /// <summary>A still picture of the resource table (<see cref="GpuLayerDescriptor.StillResourceId"/>).</summary>
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
/// The control block at offset 0 (4 KB). The client writes <see cref="ClientPid"/>, <see cref="Command"/> and the
/// scene state; the worker the rest. Fields shared across processes are read and written with Volatile / Interlocked.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = CompositorProtocol.ControlBytes)]
public struct ControlBlock
{
    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public int Version;
    [FieldOffset(8)] public int ClientPid;
    [FieldOffset(12)] public int WorkerPid;

    /// <summary>The triple buffer's word: bits 0-1 the slot published last, bit 2 set while it has not been taken.</summary>
    [FieldOffset(16)] public int SceneState;

    /// <summary>The slot the worker holds (the scene it rendered last): a worker that takes over starts from it.</summary>
    [FieldOffset(20)] public int ReaderSlot;

    [FieldOffset(24)] public int WorkerState;
    [FieldOffset(28)] public int Command;

    /// <summary>Raised by the worker on every turn of its loop (at least every 100 ms): a worker that stops raising it is hung.</summary>
    [FieldOffset(32)] public long Heartbeat;

    /// <summary>The <see cref="FrameSceneHeader.FrameSequenceNumber"/> of the frame presented last.</summary>
    [FieldOffset(40)] public long PresentedSequence;

    /// <summary>Which output texture holds it (0 to <see cref="CompositorProtocol.OutputRing"/> - 1).</summary>
    [FieldOffset(48)] public int PresentedIndex;

    /// <summary>The output textures' generation: a new worker makes new ones (<see cref="CompositorProtocol.OutputTexture"/>).</summary>
    [FieldOffset(52)] public int OutputGeneration;
    [FieldOffset(56)] public int OutputWidth;
    [FieldOffset(60)] public int OutputHeight;

    /// <summary>The LUID of the graphics card the worker renders on: a reader opens the output on the same card.</summary>
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
    /// How often a decoded frame was made to stand for a moment just before it that nothing covered (a frame the decoder
    /// dropped), how often a seek landed after its moment and was made again from further back, and how many moments no
    /// frame will ever cover (a hole in the stream: shown with the nearest frame, which an export counts as a failure).
    /// Diagnostics for tools and GoldenTests (Pass 5.0-Verify).
    /// </summary>
    [FieldOffset(120)] public int Stretches;
    [FieldOffset(124)] public int LateLandings;
    [FieldOffset(128)] public int MissingFrames;

    /// <summary>Set by the client: 1 decodes on the processor even where the card could (Settings' hardware video decoding off: a driver that shows broken frames).</summary>
    [FieldOffset(132)] public int SoftwareDecodeRequest;

    // 512..1023: the GPU time of the last frame's passes per shader slot (ms), when the scene asks for timing.
    [FieldOffset(512)] public SlotTimings SlotGpuMs;

    // 1024..1535: how many times each slot ran in that frame.
    [FieldOffset(1024)] public SlotCounts SlotRuns;

    // 2048..3071: the worker's last message (UTF-16, NUL-terminated).
    [FieldOffset(2048)] public MessageChars Message;

    // ----- 3072..4095: Instant Seek (protocol 2) -----

    /// <summary>Raised by the client after it rewrote <see cref="Hints"/> (the worker reads them again; no frame is drawn for it).</summary>
    [FieldOffset(3072)] public int HintsVersion;
    [FieldOffset(3076)] public int HintCount;

    /// <summary>
    /// The frame cache's budget the client asks for, in bytes: 0 automatic (an eighth of the card's own memory, at most
    /// 3 GB), -1 no cache (every frame decoded when it is shown). The worker also keeps under the budget Windows gives it.
    /// </summary>
    [FieldOffset(3080)] public long CacheBudgetRequest;

    /// <summary>Decode threads the client asks for: 0 automatic (two on a card, one on WARP), at most 4.</summary>
    [FieldOffset(3088)] public int DecodeThreadsRequest;

    /// <summary>1 when every media layer of the frame presented last showed its exact frame; 0 when one showed the nearest it had.</summary>
    [FieldOffset(3092)] public int PresentedExact;

    // 3096..3479: free (protocol 2's hints).

    // 3480..: the cache's counters (diagnostics, tools\SeekBench): exact frames found, frames not there yet, presents
    // made with a stand-in, frames and bytes held, the budget in force, how long the last exact frame took after it was
    // first asked for, how many of the exact frames found had been decoded ahead (hints, pre-roll, play-ahead).
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

    // ----- 3552..3583: the frame read back for the client (FlagReadback: GPU exports, filmstrips) -----

    /// <summary>The <see cref="FrameSceneHeader.FrameSequenceNumber"/> of the frame last written into the readback section.</summary>
    [FieldOffset(3552)] public long ReadbackSequence;

    /// <summary>The readback section's size in bytes (set by the client with <see cref="ReadbackGeneration"/>).</summary>
    [FieldOffset(3560)] public long ReadbackCapacity;

    /// <summary>Raised by the client whenever it makes the section anew (<see cref="CompositorProtocol.ReadbackSection"/>).</summary>
    [FieldOffset(3568)] public int ReadbackGeneration;

    /// <summary>The frame written: its size, and the bytes of one row (BGRA8, rows packed).</summary>
    [FieldOffset(3572)] public int ReadbackWidth;
    [FieldOffset(3576)] public int ReadbackHeight;
    [FieldOffset(3580)] public int ReadbackRowBytes;

    // ----- 3584..4095: the seek hints (protocol 3) -----

    /// <summary>
    /// Up to 16 moments and stretches the client expects to be asked for soon (where a scrub is heading, the cuts ahead of
    /// the playhead, the pointer over the timeline): <see cref="HintCount"/> of them, read again when <see cref="HintsVersion"/> changes.
    /// </summary>
    [FieldOffset(3584)] public SeekHints Hints;
}

/// <summary>What a seek hint stands for (<see cref="GpuSeekHint.Kind"/>): the order is the order of urgency.</summary>
public enum GpuSeekHintKind
{
    None = 0,
    /// <summary>Where a scrub is heading (its speed carried a little ahead).</summary>
    ScrubAhead = 1,
    /// <summary>The moment under the pointer while it rests on the timeline: a click there is likely.</summary>
    Pointer = 2,
    /// <summary>
    /// A cut ahead of the playhead (5.0-Release: every one within the lookahead horizon): the first frames of the clip it
    /// starts (<see cref="GpuSeekHint.Span"/> of the file at most, its clip's length), wanted at <see cref="GpuSeekHint.At"/>;
    /// kept in the cache until the hint goes (the playhead passed it, or it left the horizon).
    /// </summary>
    CutEntry = 3,
    /// <summary>The playhead itself: its neighbourhood decoded in idle time (frame steps, J/K/L).</summary>
    Playhead = 4,
    /// <summary>The pointer resting on the timeline (5.0-Release): a short play-ahead from its moment (<see cref="GpuSeekHint.Span"/> of the file), so a click and Space there start at once.</summary>
    HoverRoll = 5,
}

/// <summary>
/// One seek hint: a source of the source table and a moment of it, in the file's own time, and for a stretch how far it
/// goes on (32 bytes, protocol 3).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct GpuSeekHint
{
    [FieldOffset(0)] public int SourceId;
    [FieldOffset(4)] public int Kind;
    [FieldOffset(8)] public double MediaSeconds;

    /// <summary>Path hash of the source (<see cref="SourceEntry.PathHash"/>): a hint for a table entry that changed since is dropped.</summary>
    [FieldOffset(16)] public long PathHash;

    /// <summary>How far past <see cref="MediaSeconds"/> the frames are wanted, seconds of the file (0: that frame alone).</summary>
    [FieldOffset(24)] public float Span;

    /// <summary>The moment of the sequence it is wanted at (a cut's): the earlier, the sooner it is decoded among hints of its kind.</summary>
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

/// <summary>
/// The scene for one frame (the header of a 64 KB scene slot): the canvas, the clock and the camera. The layers follow it,
/// bottom first, at <see cref="FrameScene.LayersOffset"/>.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = FrameScene.HeaderBytes)]
public struct FrameSceneHeader
{
    /// <summary>Raised by the client for every scene it publishes; the worker presents it back (<see cref="ControlBlock.PresentedSequence"/>).</summary>
    [FieldOffset(0)] public long FrameSequenceNumber;
    [FieldOffset(8)] public double PlayheadSeconds;
    [FieldOffset(16)] public int IsPlaying;

    /// <summary>The sequence's frame in its own pixels (1920 x 1080, 1080 x 1920 ...): the layers' boxes are in these.</summary>
    [FieldOffset(20)] public int CanvasWidth;
    [FieldOffset(24)] public int CanvasHeight;

    /// <summary>0 the flat picture; 1 the 3D exploded layer stack (5.0D).</summary>
    [FieldOffset(28)] public int ViewMode;
    [FieldOffset(32)] public float CameraYaw;
    [FieldOffset(36)] public float CameraPitch;
    [FieldOffset(40)] public float CameraZoom;
    [FieldOffset(44)] public float ExplodedZSpread;
    [FieldOffset(48)] public int ActiveLayerCount;

    /// <summary>
    /// Bit 0: time every pass (<see cref="ControlBlock.SlotGpuMs"/>); bit 1: wait for the timings before presenting; bit 2:
    /// present the canvas as it is (premultiplied, its alpha kept, no background: the golden tests read it).
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
    /// With <see cref="FlagLetterbox"/>: the player's zoom (1 fitted, 2 twice as large ...) and pan (fractions of the fitted
    /// picture, as MainWindow's player navigation keeps them: the picture's middle moved by pan x its size x zoom).
    /// </summary>
    [FieldOffset(80)] public float ViewScale;
    [FieldOffset(84)] public float ViewPanX;
    [FieldOffset(88)] public float ViewPanY;

    /// <summary>
    /// The magnifier loupe (5.0-Verify, the user's bug 6): while a slider shaping a layer's edge is held, the layer of this
    /// clip (<see cref="GpuLayerDescriptor.ClipKey"/>; 0 none) is drawn again alone at <see cref="LoupeZoom"/> times the
    /// player's scale round a point of its edge (found on the card), over a checkerboard, in a corner of the picture.
    /// <see cref="LoupeKind"/>: 1 a feather (the corner is looked at when nothing else is soft), 2 a mask, 3 a matte, 4 a key.
    /// </summary>
    [FieldOffset(96)] public long LoupeClipKey;
    [FieldOffset(104)] public int LoupeKind;
    [FieldOffset(108)] public float LoupeZoom;

    /// <summary>
    /// The 3D exploded view's stack depth in places (5.0-Release): how many places its plan has, whatever is on at the
    /// moment (each track has its own, GpuScenePlan.PlaceLayers), so the camera's middle (ExplodedCamera) stays still while
    /// layers come and go. 0: the stack is the layers drawn (<see cref="ActiveLayerCount"/>), as tests write it.
    /// </summary>
    [FieldOffset(112)] public int ExplodedDepth;

    /// <summary>
    /// Bit 3 (<see cref="FlagExactFrames"/>): draw only once every media layer has its exact frame (exports, filmstrips,
    /// tests); without it a layer whose frame is not decoded yet shows the nearest one there is and the frame is drawn
    /// again when the exact one arrives (Instant Seek).
    /// </summary>
    /// <summary>
    /// Bit 4 (<see cref="FlagLetterbox"/>): the canvas fitted into the output as the player shows it (centred, black bars,
    /// <see cref="ViewScale"/> and <see cref="ViewPanX"/> / <see cref="ViewPanY"/> applied, nothing drawn outside the picture); without it the canvas
    /// is stretched over the whole output (tests, exports, filmstrips).
    /// </summary>
    /// <summary>
    /// Bit 5 (<see cref="FlagReadback"/>): the frame drawn is also written into the client's readback section (BGRA8, its
    /// output's size) and <see cref="ControlBlock.ReadbackSequence"/> set: GPU exports and filmstrips. A readback, never on
    /// the player's path.
    /// </summary>
    public const int FlagTimePasses = 1, FlagWaitForTimings = 2, FlagRawOutput = 4, FlagExactFrames = 8, FlagLetterbox = 16, FlagReadback = 32;
}

/// <summary>
/// One layer of a frame: where its picture comes from, which part of it, where it goes on the canvas, how it is laid on,
/// and up to 16 effect packets (<see cref="GpuEffectPacket"/>, from <c>EffectInstance.EvaluateGpuPacket</c> and
/// <c>TransitionStyles.GpuPacket</c>) in the order they apply. Composite-stage packets (TransformComposite, DropShadow,
/// BlendMode, the transitions 64-68) act when the layer is laid on, wherever they stand in the list.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = FrameScene.LayerBytes)]
public struct GpuLayerDescriptor
{
    /// <summary>A hash of the clip's id (diagnostics and per-clip history such as decode position).</summary>
    [FieldOffset(0)] public long ClipKey;

    /// <summary>A hash of the source's path (<see cref="SourceEntry.PathHash"/>), a check that the source table was not changed under it.</summary>
    [FieldOffset(8)] public long SourcePathHash;
    [FieldOffset(16)] public int SourceId;
    [FieldOffset(20)] public int SourceKind;

    /// <summary>The moment of the source to show, in seconds of the file's own time (its first frame's time is 0).</summary>
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

    /// <summary>The blurred background made of this layer's picture under it: the blur's radius in canvas pixels (0 off).</summary>
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

    /// <summary>Bit 0 shown (a layer without it is skipped); bit 1 an adjustment layer (its effects work on the canvas under its box); see the constants for the rest.</summary>
    [FieldOffset(132)] public int Flags;

    /// <summary>Its depth in the 3D exploded view (5.0D): its place in the stack.</summary>
    [FieldOffset(136)] public float ZDepth;
    [FieldOffset(140)] public int EffectCount;
    [FieldOffset(144)] public EffectPackets Effects;

    /// <summary>
    /// Bit 2 (<see cref="FlagBlurFillOnly"/>): only the layer's blurred background is drawn, over the whole frame (the
    /// primary chain's background and the blur twins, at their own place in the stack, as FFmpeg makes them). Bit 3
    /// (<see cref="FlagSelected"/>): the clip selected in the editor (outlined in the 3D exploded view). Bit 4
    /// (<see cref="FlagSelectedTrack"/>, 5.0-Release): another clip of the selected clip's track, playing while it is not
    /// (its plane keeps a fainter outline, so the selected layer's plane never blinks across its cuts).
    /// </summary>
    public const int FlagVisible = 1, FlagAdjustment = 2, FlagBlurFillOnly = 4, FlagSelected = 8, FlagSelectedTrack = 16;
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
    public const int LayerBytes = 768;
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

    /// <summary>How many I-frame times the client put in the source's own section (<see cref="CompositorProtocol.IFrameSection"/>); 0 none.</summary>
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

/// <summary>A texture the client hands over: an entry of the resource table (128 B). Its pixels are in their own section.</summary>
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
/// The mapping itself, as either side holds it: typed references into the shared memory, and the triple buffer the
/// scenes go through. The client owns the writer's slot, the worker the reader's; neither ever waits for the other.
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
        BaseName = baseName;
        _file = file;
        _view = file.CreateViewAccessor(0, CompositorProtocol.TotalBytes, MemoryMappedFileAccess.ReadWrite);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;
    }

    /// <summary>Makes a new control plane (the client), zeroed, with the triple buffer in its starting state.</summary>
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

    /// <summary>Opens the client's control plane (the worker); null when it is not there or not of this version.</summary>
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

    // ----- The writer (the client) -----

    /// <summary>The slot the client writes its next scene into (its own until <see cref="Publish"/>).</summary>
    public int WriterSlot => _writerSlot;

    /// <summary>Hands the scene in the writer's slot to the worker; the client gets the slot published before (or one the worker let go of).</summary>
    public void Publish()
    {
        Thread.MemoryBarrier();
        var previous = Interlocked.Exchange(ref State, _writerSlot | 4);
        _writerSlot = previous & 3;
    }

    // ----- The reader (the worker) -----

    /// <summary>
    /// The slot holding the newest scene: a newly published one when there is one (taken, so the client cannot write
    /// into it), else the one taken last. <paramref name="isNew"/> says which.
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

