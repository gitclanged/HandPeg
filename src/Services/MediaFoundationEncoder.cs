using System.Globalization;

namespace HandPegApp.Services;

/// <summary>
/// FFmpeg's Media Foundation encoders (h264_mf, hevc_mf, av1_mf, Pass 5.0): Windows' own door to a card's hardware
/// encoder, and the one there is on Snapdragon / Adreno ARM64 machines, which have no NVENC, QuickSync or AMF. They take
/// none of the vendor encoders' options: rate control is "-rate_control" (cbr, u_vbr, pc_vbr, quality) with
/// "-quality 0-100" for the constant-quality mode, and "-hw_encoding 1" refuses Windows' software encoder, which Media
/// Foundation would otherwise hand over without a word. The probe and the export build their options here.
/// </summary>
public static class MediaFoundationEncoder
{
    /// <summary>The Media Foundation encoders, in the probe's order (after every vendor's own).</summary>
    public static IReadOnlyList<string> Names { get; } = ["h264_mf", "hevc_mf", "av1_mf"];

    /// <summary>True for an FFmpeg Media Foundation encoder's name ("hevc_mf").</summary>
    public static bool Is(string encoder) => encoder.EndsWith("_mf", StringComparison.Ordinal);

    /// <summary>The option that makes FFmpeg take only a hardware encoder: never Windows' software one in its place.</summary>
    public static IReadOnlyList<string> HardwareOnly { get; } = ["-hw_encoding", "1"];

    /// <summary>The same as one piece of a command line.</summary>
    public const string HardwareOnlyArgument = "-hw_encoding 1";

    /// <summary>
    /// The rate control for a bitrate: constant ("cbr", held to it by the buffer) or average (unconstrained VBR, "u_vbr",
    /// which every hardware encoder Media Foundation lists takes).
    /// </summary>
    public static string BitrateArguments(int kilobits, bool constant) => constant
        ? string.Create(CultureInfo.InvariantCulture, $"-rate_control cbr -b:v {kilobits}k -maxrate {kilobits}k -bufsize {kilobits * 2L}k")
        : string.Create(CultureInfo.InvariantCulture, $"-rate_control u_vbr -b:v {kilobits}k");

    /// <summary>Constant quality: Media Foundation's quality mode at <see cref="QualityFromCrf"/> of the slider's value.</summary>
    public static string QualityArguments(int crf) =>
        string.Create(CultureInfo.InvariantCulture, $"-rate_control quality -quality {QualityFromCrf(crf)}");

    /// <summary>
    /// The slider's CRF (0 the best, 51 the worst) as Media Foundation's quality (100 the best): a straight line, 23 (the
    /// usual default) about 55, 18 about 65. Never 0, which some encoders read as "not set".
    /// </summary>
    public static int QualityFromCrf(int crf) => Math.Clamp((int)Math.Round(100 - Math.Clamp(crf, 0, 51) * (100.0 / 51)), 1, 100);

    /// <summary>The whole option string an export adds after "-c:v name" for a Media Foundation encoder.</summary>
    public static string ExportArguments(bool bitrateMode, bool constantBitrate, int kilobits, int crf) =>
        (bitrateMode && kilobits > 0 ? BitrateArguments(kilobits, constantBitrate) : QualityArguments(crf)) + " " + HardwareOnlyArgument;
}
