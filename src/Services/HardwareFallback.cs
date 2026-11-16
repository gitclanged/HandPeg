using System.Text.RegularExpressions;

namespace HandPegApp.Services;

/// <summary>
/// Rewrites a GPU-encoder FFmpeg command for a software retry after hardware failure (driver issue, busy GPU, unsupported format).
/// </summary>
public static partial class HardwareFallback
{
    [GeneratedRegex(@"-c:v (?<codec>h264|hevc|av1)_(?<vendor>nvenc|qsv|amf|mf)\b")]
    private static partial Regex HardwareEncoderRegex();

    // The constant-quality arguments of each vendor; the number carries over as a CRF.
    [GeneratedRegex(@"-rc vbr -cq (?<q>\d+) -b:v 0")]
    private static partial Regex NvencQualityRegex();

    // NVENC's own tuning, which a software encoder either does not know or means something else by.
    [GeneratedRegex(@" -(?:tune (?:hq|uhq|ll|ull|lossless)|multipass \w+|(?:spatial|temporal)[-_]aq \d|rc-lookahead \d+)\b")]
    private static partial Regex NvencTuningRegex();

    // AMF's usage and QuickSync's lookahead, which no software encoder knows.
    [GeneratedRegex(@" -(?:usage \w+|look_ahead \d|look_ahead_depth \d+)\b")]
    private static partial Regex AmfQsvTuningRegex();

    [GeneratedRegex(@"-(?:q:v|global_quality) (?<q>\d+)")]
    private static partial Regex QsvQualityRegex();

    [GeneratedRegex(@"-rc cqp -qp_i (?<q>\d+) -qp_p \d+")]
    private static partial Regex AmfQualityRegex();

    // MF: quality (100 best) maps back to CRF; MF-only switches (including -compression_level) are removed.
    [GeneratedRegex(@"-rate_control quality -quality (?<q>\d+)")]
    private static partial Regex MfQualityRegex();

    [GeneratedRegex(@" -(?:rate_control \w+|hw_encoding \d|scenario \w+|compression_level \d+)\b")]
    private static partial Regex MfTuningRegex();

    // AMF high_quality must be matched before quality, or it would leak into the software retry.
    [GeneratedRegex(@" -(?:preset|quality) (?:p[1-7]|speed|balanced|high_quality|quality|veryfast|faster|fast|medium|slow|slower|veryslow)\b")]
    private static partial Regex VendorPresetRegex();

    // Vendor fastest steps (NVENC p1, AMF speed, QSV veryfast, MF -compression_level 0) retry as ultrafast; all others as veryslow.
    [GeneratedRegex(@" -(?:(?:preset|quality) (?:p1|speed|veryfast)|compression_level 0)(?=\s|$)")]
    private static partial Regex FastestStepRegex();

    [GeneratedRegex(@" -rc (?:vbr_peak|vbr|cbr)\b")]
    private static partial Regex VendorRateModeRegex();

    // Hardware decoding, on whichever card: "-hwaccel auto", or Direct3D 11 with the number of a card.
    [GeneratedRegex(@" -hwaccel \S+(?: -hwaccel_device \S+)?")]
    private static partial Regex HardwareDecodeRegex();

    /// <summary>The software encoder that stands in for a hardware one, or null for anything else.</summary>
    public static string? GetSoftwareEncoder(string encoderName)
    {
        var match = HardwareEncoderRegex().Match($"-c:v {encoderName}");
        return match.Success ? SoftwareFor(match.Groups["codec"].Value) : null;
    }

    /// <summary>
    /// Returns false for non-hardware commands; otherwise outputs the rewritten command and the replaced/replacement encoder names.
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

        // Detect speed-first before vendor switches are stripped.
        var fastest = HadFastestStep(command);

        // Only the failed vendor's own arguments are touched.
        rewritten = match.Groups["vendor"].Value switch
        {
            "nvenc" => NvencQualityRegex().Replace(rewritten, "-crf ${q}"),
            "qsv" => QsvQualityRegex().Replace(rewritten, "-crf ${q}"),
            "mf" => MfQualityRegex().Replace(rewritten, quality => "-crf " + CrfFromMfQuality(quality.Groups["q"].Value)),
            _ => AmfQualityRegex().Replace(rewritten, "-crf ${q}"),
        };

        if (match.Groups["vendor"].Value == "nvenc")
        {
            // Lossless had no quality number to carry over: x264 and x265 are asked for the best they have.
            var lossless = rewritten.Contains(" -tune lossless", StringComparison.Ordinal) && software != "libsvtav1";
            rewritten = NvencTuningRegex().Replace(rewritten, "");
            if (lossless)
                rewritten = HardwareEncoderRegex().Replace(rewritten, "${0} -crf 0", 1);
        }
        else if (match.Groups["vendor"].Value == "mf")
        {
            rewritten = MfTuningRegex().Replace(rewritten, "");
        }
        else
        {
            rewritten = AmfQsvTuningRegex().Replace(rewritten, "");
        }

        // Map 10-bit to the software pixel format name, and MF NV12 to planar 4:2:0 (SVT-AV1 rejects NV12).
        rewritten = rewritten.Replace("-pix_fmt p010le", "-pix_fmt yuv420p10le").Replace("-pix_fmt nv12", "-pix_fmt yuv420p");

        // In the bitrate modes only the vendor's "-rc" switch has to go; the bitrate itself carries over.
        rewritten = VendorRateModeRegex().Replace(rewritten, "");

        // The vendor's own speed step (NVENC's p4, AMF's "balanced") means nothing to a software encoder.
        rewritten = VendorPresetRegex().Replace(rewritten, "");

        // x264/x265 get veryslow, or ultrafast for speed-first; SVT-AV1 uses its own scale and keeps its default.
        var replacement = software == "libsvtav1" ? $"-c:v {software}" : $"-c:v {software} -preset {SoftwareStepFor(fastest)}";
        rewritten = HardwareEncoderRegex().Replace(rewritten, replacement, 1);

        // If the GPU is the problem, do not ask it to decode either.
        rewritten = HardwareDecodeRegex().Replace(rewritten, "");
        return true;
    }

    // Inverse of MediaFoundationEncoder.QualityFromCrf: MF quality (0-100) to CRF.
    private static string CrfFromMfQuality(string quality) =>
        Math.Clamp((int)Math.Round(51 - int.Parse(quality, System.Globalization.CultureInfo.InvariantCulture) * 0.51), 0, 51)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// True when the command uses a vendor fastest step (NVENC p1, AMF speed, QSV veryfast, MF -compression_level 0). Shared rule for
    /// <see cref="TryRewrite"/> and MainViewModel.PlaceSoftwareRetry: such retries use the fastest software step.
    /// </summary>
    public static bool HadFastestStep(string command) => FastestStepRegex().IsMatch(command);

    /// <summary>x264/x265 retry preset: ultrafast for speed-first commands, else veryslow.</summary>
    internal static string SoftwareStepFor(bool fastest) => fastest ? "ultrafast" : "veryslow";

    private static string SoftwareFor(string codec) => codec switch
    {
        "hevc" => "libx265",
        "av1" => "libsvtav1",
        _ => "libx264",
    };
}
