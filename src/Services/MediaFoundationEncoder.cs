using System.Globalization;

namespace HandPegApp.Services;

/// <summary>
/// FFmpeg Media Foundation encoders (h264_mf, hevc_mf, av1_mf): the Windows route to hardware encoders, and the only one on ARM64
/// Adreno (no NVENC/QSV/AMF). Options: "-rate_control" (cbr, u_vbr, pc_vbr, quality), "-quality 0-100" (CQ level,
/// CODECAPI_AVEncCommonQuality), "-compression_level 0-100" (CODECAPI_AVEncCommonQualityVsSpeed, 100 = slowest/best) and
/// "-hw_encoding 1" (rejects MF's silent software fallback). Shared by the probe and export.
/// </summary>
public static class MediaFoundationEncoder
{
    /// <summary>The Media Foundation encoders, in the probe's order (after every vendor's own).</summary>
    public static IReadOnlyList<string> Names { get; } = ["h264_mf", "hevc_mf", "av1_mf"];

    /// <summary>True for an FFmpeg Media Foundation encoder's name ("hevc_mf").</summary>
    public static bool Is(string encoder) => encoder.EndsWith("_mf", StringComparison.Ordinal);

    /// <summary>Forces a hardware MFT (never the Windows software encoder).</summary>
    public static IReadOnlyList<string> HardwareOnly { get; } = ["-hw_encoding", "1"];

    /// <summary>The same as one piece of a command line.</summary>
    public const string HardwareOnlyArgument = "-hw_encoding 1";

    /// <summary>
    /// Slowest/best step (default for all encoders): QualityVsSpeed 100. Not "-quality", which is the CQ level (a second one would
    /// override the CQ slider).
    /// </summary>
    public const string SlowestStepArgument = "-compression_level 100";

    /// <summary>
    /// Fastest step (Fast Proxy): QualityVsSpeed 0; replaces <see cref="SlowestStepArgument"/>, never combined.
    /// </summary>
    public const string FastestStepArgument = "-compression_level 0";

    /// <summary>Fastest step for speed-first presets, else slowest.</summary>
    public static string StepArgument(bool fastest) => fastest ? FastestStepArgument : SlowestStepArgument;

    /// <summary>
    /// Input pixel format: NV12, or P010 for 10-bit. MF hardware encoders accept only semi-planar 4:2:0; planar yuv420p fails on Adreno
    /// with "format negotiation failed" (the probe then found no encoder).
    /// </summary>
    public static string PixelFormat(bool tenBit) => tenBit ? "p010le" : "nv12";

    /// <summary>
    /// Bitrate rate control: "cbr" (buffer-constrained) or "u_vbr" (unconstrained VBR, supported by every MF hardware encoder).
    /// </summary>
    public static string BitrateArguments(int kilobits, bool constant) => constant
        ? string.Create(CultureInfo.InvariantCulture, $"-rate_control cbr -b:v {kilobits}k -maxrate {kilobits}k -bufsize {kilobits * 2L}k")
        : string.Create(CultureInfo.InvariantCulture, $"-rate_control u_vbr -b:v {kilobits}k");

    /// <summary>Constant quality: MF quality mode at <see cref="QualityFromCrf"/> of the slider value.</summary>
    public static string QualityArguments(int crf) =>
        string.Create(CultureInfo.InvariantCulture, $"-rate_control quality -quality {QualityFromCrf(crf)}");

    /// <summary>
    /// Linear CRF (0 best, 51 worst) to MF quality (100 best): 23 maps to ~55, 18 to ~65. Never 0 (read as "unset" by some encoders).
    /// </summary>
    public static int QualityFromCrf(int crf) => Math.Clamp((int)Math.Round(100 - Math.Clamp(crf, 0, 51) * (100.0 / 51)), 1, 100);

    /// <summary>
    /// Full MF option string after "-c:v name": rate control, slowest step (fastest with <paramref name="fastest"/>) and hardware-only.
    /// Used by MainViewModel.BuildVideoCodecArguments so exported commands match tested ones.
    /// </summary>
    public static string ExportArguments(bool bitrateMode, bool constantBitrate, int kilobits, int crf, bool fastest = false) =>
        (bitrateMode && kilobits > 0 ? BitrateArguments(kilobits, constantBitrate) : QualityArguments(crf)) + " " + StepArgument(fastest) + " " + HardwareOnlyArgument;
}
