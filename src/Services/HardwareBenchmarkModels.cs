using System.Text.Json.Serialization;

namespace HandPegApp.Services;

// ----- The hardware benchmark's results (Pass 5.0-Core, micro-step 5.0-0) -----
//
// Measured by HandPeg.exe --hw-benchmark (Compositor/HardwareBenchmark.cs, a process of its own: a driver that falls
// over takes only it down), which writes one JSON object per line on its standard output: progress lines while it
// works, and the result last. Read by HardwareBenchmarkService, kept in hardware-benchmark.json, drawn by the Settings
// stage. The names are the JSON's (camelCase), append-only.

/// <summary>One line of the benchmark's output: <see cref="Type"/> "progress" (with <see cref="Progress"/>) or "result" (with <see cref="Result"/>).</summary>
public sealed class HardwareBenchmarkLine
{
    public string Type { get; set; } = "";
    public HardwareBenchmarkProgress? Progress { get; set; }
    public HardwareBenchmarkResult? Result { get; set; }
}

/// <summary>Where the benchmark is: a stage, the card it is on, how far through the whole run, and the results so far.</summary>
public sealed class HardwareBenchmarkProgress
{
    /// <summary>"adapters", "decode-hw", "decode-sw", "seek", "accuracy", "transfer", "cross-adapter", "done".</summary>
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

    /// <summary>The four roles the results point to (applied by the auto-tune, each still the user's to change).</summary>
    public HardwareBenchmarkRoles Recommended { get; set; } = new();

    /// <summary>Why the benchmark stopped short, when it did; empty otherwise.</summary>
    public string Error { get; set; } = "";
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

    /// <summary>"AMD", "Intel", "NVIDIA", "Qualcomm", "Microsoft" or the vendor number in hex.</summary>
    public string Vendor { get; set; } = "";

    /// <summary>The hardware decoders its driver offers: "H.264", "HEVC", "HEVC 10-bit", "VP9", "AV1".</summary>
    public List<string> DecodeCodecs { get; set; } = [];

    /// <summary>The hardware encoders Media Foundation lists for it: "H.264", "HEVC", "AV1".</summary>
    public List<string> EncodeCodecs { get; set; } = [];

    /// <summary>Frames per second decoded into video memory (Media Foundation with Direct3D 11); 0 when it could not.</summary>
    public double HardwareDecodeFps { get; set; }

    /// <summary>Median time from a seek to the frame at that moment, hardware decoding, ms.</summary>
    public double HardwareSeekMs { get; set; }

    /// <summary>The same, decoded by the processor (measured once, on the first card's run).</summary>
    public double SoftwareDecodeFps { get; set; }

    public double SoftwareSeekMs { get; set; }

    /// <summary>The hardware frames against the processor's, luma and chroma: 99 when they are the same to the bit.</summary>
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
    /// DXGI can list one physical card more than once (two LUIDs, the same vendor and device: seen on an RX 7900 XTX): the
    /// index of the first listing when this one repeats it, else -1. Roles are given to first listings only.
    /// </summary>
    public int SameCardAs { get; set; } = -1;

    /// <summary>What went wrong on this card, when something did (the other numbers are then 0).</summary>
    public string Note { get; set; } = "";

    [JsonIgnore]
    public bool DecodesInHardware => HardwareDecodeFps > 0;
}

public sealed class HardwareBenchmarkCrossAdapter
{
    /// <summary>The card that decodes, and the one that composites.</summary>
    public int DecodeAdapter { get; set; }

    public int CompositeAdapter { get; set; }

    /// <summary>Decoding on the decode card and copying each frame across to the composite card, frames per second.</summary>
    public double CrossDecodeFps { get; set; }

    /// <summary>Decoding on the composite card itself, frames per second.</summary>
    public double LocalDecodeFps { get; set; }

    /// <summary>One 1080p frame across the bus, ms, and the rate that makes, MB/s.</summary>
    public double TransferMsPerFrame { get; set; }

    public double BandwidthMBps { get; set; }

    /// <summary>Whether the second card's decoding plus the copy beats decoding on the composite card.</summary>
    public bool CrossIsFaster { get; set; }
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
