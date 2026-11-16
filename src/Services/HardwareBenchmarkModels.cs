using System.Text.Json.Serialization;

namespace HandPegApp.Services;

// ----- Hardware benchmark results -----
//
// Produced by HandPeg.exe --hw-benchmark (Compositor/HardwareBenchmark.cs, isolated so driver crashes stay contained) as JSON lines on
// stdout: progress lines, then the result. Read by HardwareBenchmarkService, stored in hardware-benchmark.json, shown in Settings.
// Property names are camelCase and append-only.

/// <summary>One output line: <see cref="Type"/> "progress" (<see cref="Progress"/>) or "result" (<see cref="Result"/>).</summary>
public sealed class HardwareBenchmarkLine
{
    public string Type { get; set; } = "";
    public HardwareBenchmarkProgress? Progress { get; set; }
    public HardwareBenchmarkResult? Result { get; set; }
}

/// <summary>Progress: stage, current adapter, overall fraction and partial results.</summary>
public sealed class HardwareBenchmarkProgress
{
    /// <summary>"adapters", "decode-hw", "decode-sw", "seek", "accuracy", "transfer", "compositor", "cross-adapter", "done".</summary>
    public string Stage { get; set; } = "";

    /// <summary>The card's DXGI index; -1 when the stage is not about one card.</summary>
    public int Adapter { get; set; } = -1;

    /// <summary>0 to 1 over the whole benchmark.</summary>
    public double Fraction { get; set; }

    public string Message { get; set; } = "";

    /// <summary>Everything measured so far (the stage draws its bars from this while the benchmark runs).</summary>
    public HardwareBenchmarkResult? Partial { get; set; }
}

public sealed class HardwareBenchmarkResult
{
    /// <summary>When it ran (UTC, ISO 8601).</summary>
    public string MeasuredAt { get; set; } = "";

    /// <summary>The cards and drivers it ran on (GpuAdapters.Fingerprint): a result for other cards is stale.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>What was decoded, e.g. "H.264 1920x1080 60 fps, 90 frames".</summary>
    public string Clip { get; set; } = "";

    public double TotalSeconds { get; set; }

    public List<HardwareBenchmarkAdapter> Adapters { get; set; } = [];

    /// <summary>Decoding on the second card and handing the frames to the first; null on a machine with one card.</summary>
    public HardwareBenchmarkCrossAdapter? CrossAdapter { get; set; }

    /// <summary>Recommended GPU roles (applied by auto-tune; user-editable).</summary>
    public HardwareBenchmarkRoles Recommended { get; set; } = new();

    /// <summary>Codecs with any Media Foundation decoder here (hardware, software or Store): "H.264", "HEVC", "VP9", "AV1".</summary>
    public List<string> MediaFoundationDecodeCodecs { get; set; } = [];

    /// <summary>
    /// Codecs lacking an MF decoder that the compositor decodes via FFmpeg on the CPU with upload (FfmpegVideoDecoder), per FFmpeg's
    /// decoder list; empty without FFmpeg (e.g. HEVC without the HEVC Video Extensions).
    /// </summary>
    public List<string> FfmpegFallbackCodecs { get; set; } = [];

    /// <summary>NPUs enumerated by DXCore and their uses.</summary>
    public List<HardwareBenchmarkNpu> Npus { get; set; } = [];

    /// <summary>Why the benchmark stopped short, when it did; empty otherwise.</summary>
    public string Error { get; set; } = "";

    /// <summary>
    /// Result kind: "" full test (Test &amp; Auto-Tune Hardware; also legacy files), <see cref="KindEstimate"/> first-run enumeration only
    /// (unmeasured, never saved), <see cref="KindQuick"/> first-run quick check (decode and bus on role adapters only).
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>The kinds of <see cref="Kind"/> besides the full test's "".</summary>
    public const string KindEstimate = "estimate", KindQuick = "quick";
}

public sealed class HardwareBenchmarkAdapter
{
    /// <summary>The DXGI index (the number FFmpeg's -hwaccel_device and the GPU roles take).</summary>
    public int Index { get; set; }

    public string Name { get; set; } = "";
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public long Luid { get; set; }
    public long DedicatedVideoMemoryMb { get; set; }
    public long SharedSystemMemoryMb { get; set; }

    /// <summary>
    /// True for unified-memory GPUs (DXCore IsIntegrated, else DXGI sizes); the frame cache's Auto budget then derives from system
    /// memory (MemoryTier).
    /// </summary>
    public bool UnifiedMemory { get; set; }

    /// <summary>"AMD", "Intel", "NVIDIA", "Qualcomm", "Microsoft" or the vendor number in hex.</summary>
    public string Vendor { get; set; } = "";

    /// <summary>The hardware decoders its driver offers: "H.264", "HEVC", "HEVC 10-bit", "VP9", "AV1".</summary>
    public List<string> DecodeCodecs { get; set; } = [];

    /// <summary>The hardware encoders Media Foundation lists for it: "H.264", "HEVC", "AV1".</summary>
    public List<string> EncodeCodecs { get; set; } = [];

    /// <summary>MF + D3D11 decode-to-VRAM rate (fps); 0 on failure.</summary>
    public double HardwareDecodeFps { get; set; }

    /// <summary>Median time from a seek to the frame at that moment, hardware decoding, ms.</summary>
    public double HardwareSeekMs { get; set; }

    /// <summary>The same, decoded by the processor (measured once, on the first card's run).</summary>
    public double SoftwareDecodeFps { get; set; }

    public double SoftwareSeekMs { get; set; }

    /// <summary>Hardware vs. software decode PSNR (luma, chroma); 99 when bit-identical.</summary>
    public double PsnrDb { get; set; }

    /// <summary>The structural similarity of the same frames (luma), 0 to 1.</summary>
    public double Ssim { get; set; }

    /// <summary>Whether every compared frame matched the processor's exactly.</summary>
    public bool BitExact { get; set; }

    /// <summary>Video memory to the computer's memory (a frame read back), MB/s.</summary>
    public double ReadbackMBps { get; set; }

    /// <summary>The computer's memory to video memory (a frame uploaded), MB/s.</summary>
    public double UploadMBps { get; set; }

    /// <summary>
    /// DXGI may list one physical GPU twice (two LUIDs, same vendor/device): index of the first listing if this repeats it, else -1.
    /// Roles go to first listings only.
    /// </summary>
    public int SameCardAs { get; set; } = -1;

    /// <summary>What went wrong on this card, when something did (the other numbers are then 0).</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// All-slots compositor frame time on this adapter (GPU ms, 1080p, every shader slot on test patterns, best of four after 90 warm-up
    /// frames; the compositor-parity golden's Timing scene, HardwareBenchmark.Compositor.cs). 0 when untimed (only role adapters are).
    /// </summary>
    public double CompositorMs { get; set; }

    [JsonIgnore]
    public bool DecodesInHardware => HardwareDecodeFps > 0;
}

public sealed class HardwareBenchmarkCrossAdapter
{
    /// <summary>The card that decodes, and the one that composites.</summary>
    public int DecodeAdapter { get; set; }

    public int CompositeAdapter { get; set; }

    /// <summary>Cross-adapter rate (fps): decode on the decode adapter, copy to the compositor adapter.</summary>
    public double CrossDecodeFps { get; set; }

    /// <summary>Decoding on the composite card itself, frames per second.</summary>
    public double LocalDecodeFps { get; set; }

    /// <summary>One 1080p frame across the bus, ms, and the rate that makes, MB/s.</summary>
    public double TransferMsPerFrame { get; set; }

    public double BandwidthMBps { get; set; }

    /// <summary>Whether the second card's decoding plus the copy beats decoding on the composite card.</summary>
    public bool CrossIsFaster { get; set; }
}

/// <summary>An NPU found via DXCore (not yet measured).</summary>
public sealed class HardwareBenchmarkNpu
{
    public string Name { get; set; } = "";

    /// <summary>"Qualcomm", "Intel", "AMD" or the vendor number in hex.</summary>
    public string Vendor { get; set; } = "";

    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public string DriverVersion { get; set; } = "";

    /// <summary>The computer's memory it may use, MB.</summary>
    public long SharedSystemMemoryMb { get; set; }

    /// <summary>DirectML and ONNX Runtime can run models on it.</summary>
    public bool GenericMl { get; set; }

    /// <summary>What HandPeg uses it for, in a sentence (today: nothing yet, and why).</summary>
    public string UsedFor { get; set; } = "";
}

/// <summary>The four GPU roles, by DXGI index (-1 Auto; -2 for Background means the processor).</summary>
public sealed class HardwareBenchmarkRoles
{
    public int Compositor { get; set; } = -1;
    public int Decode { get; set; } = -1;
    public int Background { get; set; } = -1;
    public int Export { get; set; } = -1;

    /// <summary>One sentence per role saying why.</summary>
    public List<string> Reasons { get; set; } = [];
}
