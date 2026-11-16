using System.Globalization;

namespace HandPegApp.Services;

/// <summary>
/// Live Encoded Preview request derived from export settings: codec (1 H.264, 2 HEVC, 3 AV1, 4 VP9), MF rate control
/// (eAVEncCommonRateControlMode: 0 CBR, 2 unconstrained VBR, 3 quality), bitrate (kbps), quality 1..100
/// (<see cref="MediaFoundationEncoder.QualityFromCrf"/>), quality-vs-speed 0..100 (nearest to the export preset) and frame rate.
/// <para>Mirrors the exact Export tab encoder: <see cref="Family"/> (QSV on Intel, AMF on AMD, NVENC on NVIDIA, MF on the compositor
/// adapter, or the software encoder via FFmpeg), FFmpeg name, preset and CRF as typed, export frame size (<see cref="EncodedWidth"/> x
/// <see cref="EncodedHeight"/>, shown against the full canvas), 10-bit, and software video arguments (<see cref="SoftwareArguments"/>).</para>
/// <para>Pure (golden export-loopback): takes plain values, not the view model (MainViewModel.CurrentLoopbackSettings is the sole
/// reader). Paused frames are key frames.</para>
/// </summary>
public sealed record ExportLoopbackSettings(int Codec, int RateControl, int BitrateKbps, int Quality, int QualityVsSpeed, double FrameRate)
{
    /// <summary>eAVEncCommonRateControlMode's values the export's three rate controls become.</summary>
    public const int ConstantBitrate = 0, UnconstrainedVbr = 2, QualityMode = 3;

    /// <summary>
    /// Loopback encoder families (FrameSceneHeader.LoopbackFamily): MF (*_mf or adapter-less, compositor adapter first), QSV (Intel), AMF
    /// (AMD), NVENC (NVIDIA), or software (libx264, libx265, libsvtav1, libvpx-vp9, ...) via per-frame FFmpeg.
    /// </summary>
    public const int FamilyMediaFoundation = 0, FamilyQuickSync = 1, FamilyAmf = 2, FamilyNvenc = 3, FamilySoftware = 4;

    /// <summary>The family the loopback routes the encode to (<see cref="FamilyQuickSync"/>...).</summary>
    public int Family { get; init; }

    /// <summary>The export's encoder as FFmpeg names it ("hevc_qsv", "libx264").</summary>
    public string EncoderName { get; init; } = "";

    /// <summary>The export's preset as typed ("veryslow", "p7", "high_quality", "6"); empty for none.</summary>
    public string Preset { get; init; } = "";

    /// <summary>The export's CRF / CQ value as typed (the constant-quality slider).</summary>
    public int Crf { get; init; }

    /// <summary>Export frame size (0 x 0 = canvas); the loopback encodes at it, preserving canvas aspect.</summary>
    public int EncodedWidth { get; init; }

    public int EncodedHeight { get; init; }

    /// <summary>The export is 10-bit (Main10 / P010 in, the decoder's P010 read back).</summary>
    public bool TenBit { get; init; }

    /// <summary>Software encoder video arguments (MainViewModel.BuildVideoCodecArguments), passed to FFmpeg verbatim.</summary>
    public string SoftwareArguments { get; init; } = "";

    /// <summary>
    /// Builds loopback settings, or null with a reason for unsupported outputs (stream copy, ProRes, DNxHR, GIF, WebP).
    /// <paramref name="bitrateKbps"/> is the effective bitrate (typed, from target size, or automatic).
    /// </summary>
    public static ExportLoopbackSettings? From(string videoEncoderName, bool constantQuality, bool constantBitrate, int bitrateKbps, int crf,
        string encoderPreset, double frameRate, out string? whyNot)
    {
        var codec = CodecOf(videoEncoderName);
        if (codec == 0)
        {
            whyNot = WhyNot(videoEncoderName);
            return null;
        }

        whyNot = null;
        var rateControl = constantQuality ? QualityMode : constantBitrate ? ConstantBitrate : UnconstrainedVbr;
        var fps = frameRate > 0 && double.IsFinite(frameRate) ? Math.Clamp(frameRate, 1, 1000) : 30;
        return new ExportLoopbackSettings(codec, rateControl, Math.Clamp(bitrateKbps, 100, 400_000), MediaFoundationEncoder.QualityFromCrf(crf),
            QualityVsSpeedOf(encoderPreset), fps)
        {
            Family = FamilyOf(videoEncoderName),
            EncoderName = (videoEncoderName ?? "").Trim(),
            Preset = (encoderPreset ?? "").Trim(),
            Crf = crf,
        };
    }

    /// <summary>Codec id from an FFmpeg encoder name: H.264 1, HEVC 2, AV1 3, VP9 4, else 0.</summary>
    public static int CodecOf(string encoderName)
    {
        var name = (encoderName ?? "").Trim().ToLowerInvariant();
        if (name is "libx264" or "libx264rgb" || name.StartsWith("h264_", StringComparison.Ordinal))
            return 1;
        if (name is "libx265" || name.StartsWith("hevc_", StringComparison.Ordinal))
            return 2;
        if (name is "libsvtav1" or "libaom-av1" or "librav1e" || name.StartsWith("av1_", StringComparison.Ordinal))
            return 3;
        if (name is "libvpx-vp9" || name.StartsWith("vp9_", StringComparison.Ordinal))
            return 4;
        return 0;
    }

    /// <summary>Family from an encoder name: *_qsv QSV, *_amf AMF, *_nvenc NVENC, lib* software, else MF.</summary>
    public static int FamilyOf(string encoderName)
    {
        var name = (encoderName ?? "").Trim().ToLowerInvariant();
        return name.EndsWith("_qsv", StringComparison.Ordinal) ? FamilyQuickSync
            : name.EndsWith("_amf", StringComparison.Ordinal) ? FamilyAmf
            : name.EndsWith("_nvenc", StringComparison.Ordinal) ? FamilyNvenc
            : name.StartsWith("lib", StringComparison.Ordinal) ? FamilySoftware
            : FamilyMediaFoundation;
    }

    /// <summary>PCI vendor for a family (Intel QSV, AMD AMF, NVIDIA NVENC); 0 for the compositor adapter.</summary>
    public static uint VendorOf(int family) => family switch
    {
        FamilyQuickSync => 0x8086,
        FamilyAmf => 0x1002,
        FamilyNvenc => 0x10DE,
        _ => 0,
    };

    /// <summary>The family's short name on the ENCODED pill: QSV, AMF, NVENC, MF.</summary>
    public static string FamilyShort(int family) => family switch
    {
        FamilyQuickSync => "QSV",
        FamilyAmf => "AMF",
        FamilyNvenc => "NVENC",
        FamilySoftware => "SW",
        _ => "MF",
    };

    /// <summary>
    /// Maps an export preset to hardware quality-vs-speed (0 fastest, 100 best): veryslow/slower/p7/quality/high_quality 100; slow/p6 83;
    /// medium/p4/p5/balanced 50; fast/faster/p2/p3 33; fastest 0; other 50.
    /// </summary>
    public static int QualityVsSpeedOf(string preset) => (preset ?? "").Trim().ToLowerInvariant() switch
    {
        "placebo" or "veryslow" or "slower" or "p7" or "quality" or "high_quality" => 100,
        "slow" or "p6" => 83,
        "medium" or "p4" or "p5" or "balanced" => 50,
        "fast" or "faster" or "p2" or "p3" => 33,
        "veryfast" or "superfast" or "ultrafast" or "p1" or "speed" => 0,
        _ => 50,
    };

    /// <summary>The codec's name as the player's flyout says it.</summary>
    public static string CodecName(int codec) => codec switch
    {
        1 => "H.264",
        2 => "HEVC",
        3 => "AV1",
        4 => "VP9",
        _ => "",
    };

    /// <summary>The rate control as the player's flyout says it.</summary>
    public string RateControlName => RateControl switch
    {
        ConstantBitrate => string.Create(CultureInfo.InvariantCulture, $"CBR {BitrateKbps} kbps"),
        QualityMode => Family == FamilySoftware
            ? string.Create(CultureInfo.InvariantCulture, $"CRF {Crf}")
            : string.Create(CultureInfo.InvariantCulture, $"constant quality {Quality}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"VBR {BitrateKbps} kbps"),
    };

    /// <summary>
    /// ENCODED pill label: codec + family for hardware ("HEVC QSV", "AV1 NVENC"), library + preset for software ("x264 veryslow", "SVT-AV1 6").
    /// </summary>
    public string ShortName
    {
        get
        {
            if (Family != FamilySoftware)
                return $"{CodecName(Codec)} {FamilyShort(Family)}";
            var library = EncoderName.ToLowerInvariant() switch
            {
                "libx264" or "libx264rgb" => "x264",
                "libx265" => "x265",
                "libsvtav1" => "SVT-AV1",
                "libaom-av1" => "aom AV1",
                "librav1e" => "rav1e",
                "libvpx-vp9" => "VP9",
                var other => other,
            };
            return Preset.Length > 0 ? $"{library} {Preset}" : library;
        }
    }

    /// <summary>ENCODED pill rate text: "12 Mbps", "CBR 8 Mbps", "CQ 22" (hardware) or "CRF 22" (software).</summary>
    public string RateShort => RateControl switch
    {
        QualityMode => string.Create(CultureInfo.InvariantCulture, $"{(Family == FamilySoftware ? "CRF" : "CQ")} {Crf}"),
        ConstantBitrate => "CBR " + BitrateText(BitrateKbps),
        _ => BitrateText(BitrateKbps),
    };

    /// <summary>Formats a bitrate, e.g. "12 Mbps" or "800 kbps".</summary>
    public static string BitrateText(int kbps) => kbps >= 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{kbps / 1000.0:0.#} Mbps")
        : string.Create(CultureInfo.InvariantCulture, $"{kbps} kbps");

    /// <summary>The ENCODED pill's text while the loopback runs: "ENCODED · HEVC QSV · 12 Mbps".</summary>
    public string PillText => $"ENCODED · {ShortName} · {RateShort}";

    /// <summary>
    /// Loopback encode size: canvas scaled by export/canvas short-side ratio (even, 64-8192), or the canvas size when unspecified. Pure;
    /// shared by ExportLoopbackPreview and the golden.
    /// </summary>
    public static (int Width, int Height) EncodedSizeOf(int canvasWidth, int canvasHeight, int exportWidth, int exportHeight)
    {
        static int Even(double side) => Math.Clamp(((int)Math.Round(side) + 1) & ~1, 64, 8192);
        var (cw, ch) = (Math.Max(canvasWidth, 2), Math.Max(canvasHeight, 2));
        if (exportWidth <= 0 || exportHeight <= 0 || (exportWidth == cw && exportHeight == ch))
            return (Math.Min((cw + 1) & ~1, 8192), Math.Min((ch + 1) & ~1, 8192));
        var scale = (double)Math.Min(exportWidth, exportHeight) / Math.Min(cw, ch);
        return (Even(cw * scale), Even(ch * scale));
    }

    private static string WhyNot(string encoderName)
    {
        var name = (encoderName ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0 || name == "copy")
            return "a stream copy is not encoded again, so it looks as its source does";
        if (name.Contains("prores", StringComparison.Ordinal) || name.Contains("dnxhd", StringComparison.Ordinal))
            return "an editing codec (ProRes, DNxHR) keeps nearly every detail; there is nothing to preview";
        return string.Create(CultureInfo.InvariantCulture, $"{encoderName} has no encoder loopback to preview it with");
    }
}
