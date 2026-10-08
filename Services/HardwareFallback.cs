using System.Text.RegularExpressions;

namespace HandPegApp.Services;

/// <summary>
/// Rewrites an FFmpeg command that uses a GPU encoder into the same command on a software encoder,
/// for a second attempt when the hardware one fails (driver trouble, a busy GPU, an unsupported format).
/// </summary>
public static partial class HardwareFallback
{
    [GeneratedRegex(@"-c:v (?<codec>h264|hevc|av1)_(?<vendor>nvenc|qsv|amf)\b")]
    private static partial Regex HardwareEncoderRegex();

    // The constant-quality arguments of each vendor; the number carries over as a CRF.
    [GeneratedRegex(@"-rc vbr -cq (?<q>\d+) -b:v 0")]
    private static partial Regex NvencQualityRegex();

    [GeneratedRegex(@"-(?:q:v|global_quality) (?<q>\d+)")]
    private static partial Regex QsvQualityRegex();

    [GeneratedRegex(@"-rc cqp -qp_i (?<q>\d+) -qp_p \d+")]
    private static partial Regex AmfQualityRegex();

    [GeneratedRegex(@" -rc (?:vbr_peak|vbr|cbr)\b")]
    private static partial Regex VendorRateModeRegex();

    /// <summary>The software encoder that stands in for a hardware one, or null for anything else.</summary>
    public static string? GetSoftwareEncoder(string encoderName)
    {
        var match = HardwareEncoderRegex().Match($"-c:v {encoderName}");
        return match.Success ? SoftwareFor(match.Groups["codec"].Value) : null;
    }

    /// <summary>
    /// Returns false when the command does not use a hardware encoder. Otherwise gives the rewritten
    /// command and the names of the encoder that was replaced and its replacement.
    /// </summary>
    public static bool TryRewrite(string command, out string rewritten, out string hardware, out string software)
    {
        rewritten = command;
        hardware = software = "";

        var match = HardwareEncoderRegex().Match(command);
        if (!match.Success)
            return false;

        hardware = $"{match.Groups["codec"].Value}_{match.Groups["vendor"].Value}";
        software = SoftwareFor(match.Groups["codec"].Value);

        // Only the failed vendor's own arguments are touched.
        rewritten = match.Groups["vendor"].Value switch
        {
            "nvenc" => NvencQualityRegex().Replace(rewritten, "-crf ${q}"),
            "qsv" => QsvQualityRegex().Replace(rewritten, "-crf ${q}"),
            _ => AmfQualityRegex().Replace(rewritten, "-crf ${q}"),
        };

        // In the bitrate modes only the vendor's "-rc" switch has to go; the bitrate itself carries over.
        rewritten = VendorRateModeRegex().Replace(rewritten, "");

        // x264 and x265 take a preset; SVT-AV1 has its own numbering and is left at its default.
        var replacement = software == "libsvtav1" ? $"-c:v {software}" : $"-c:v {software} -preset medium";
        rewritten = HardwareEncoderRegex().Replace(rewritten, replacement, 1);

        // If the GPU is the problem, do not ask it to decode either.
        rewritten = rewritten.Replace(" -hwaccel auto", "");
        return true;
    }

    private static string SoftwareFor(string codec) => codec switch
    {
        "hevc" => "libx265",
        "av1" => "libsvtav1",
        _ => "libx264",
    };
}
