using System.Globalization;

namespace HandPegApp.Services;

/// <summary>
/// What the Export Quality Preview (Pass 5.4B) asks the compositor's encode loopback for, made from the export's settings: the codec
/// (1 H.264, 2 HEVC, 3 AV1, 4 VP9), the rate control as Media Foundation names it (eAVEncCommonRateControlMode: 0 CBR, 2 unconstrained VBR,
/// 3 quality), the bitrate in kbps, the quality mode's 1..100 (<see cref="MediaFoundationEncoder.QualityFromCrf"/>), quality against
/// speed 0..100 (the nearest a hardware encoder has to the export's preset) and the export's frame rate.
/// <para>5.4.4-Final (the user's: the exact encoder the Export tab chose, with every setting it has): the encoder's family
/// (<see cref="Family"/>: QuickSync on the Intel card, AMF on the AMD card, NVENC on the NVIDIA card, Media Foundation on the compositor's
/// card, or the software encoder itself through FFmpeg), its FFmpeg name, the preset and CRF as typed, the export's frame size
/// (<see cref="EncodedWidth"/> x <see cref="EncodedHeight"/>: a 720p export is encoded at 720p and shown against the full canvas), 10-bit,
/// and for a software encoder the export's own video arguments (<see cref="SoftwareArguments"/>: -c:v, -preset, -crf or -b:v, ...).</para>
/// <para>Pure (the golden export-loopback reads it): it takes the settings as words and numbers, not the view model, so the Export tab's
/// own names may change without it (MainViewModel.CurrentLoopbackSettings is the one place that reads them). A paused frame is a key frame.</para>
/// </summary>
public sealed record ExportLoopbackSettings(int Codec, int RateControl, int BitrateKbps, int Quality, int QualityVsSpeed, double FrameRate)
{
    /// <summary>eAVEncCommonRateControlMode's values the export's three rate controls become.</summary>
    public const int ConstantBitrate = 0, UnconstrainedVbr = 2, QualityMode = 3;

    /// <summary>
    /// The encoder families the loopback routes to (5.4.4-Final, FrameSceneHeader.LoopbackFamily): Media Foundation (h264_mf and the like, or
    /// anything without a card of its own: the compositor's card first), QuickSync (the Intel card's encoder), AMF (the AMD card's), NVENC
    /// (the NVIDIA card's), or a software encoder (libx264, libx265, libsvtav1, libvpx-vp9...), run through FFmpeg one frame at a time.
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

    /// <summary>The export's frame size (0 x 0: the canvas's). The loopback encodes at it, the canvas's shape kept.</summary>
    public int EncodedWidth { get; init; }

    public int EncodedHeight { get; init; }

    /// <summary>The export is 10-bit (Main10 / P010 in, the decoder's P010 read back).</summary>
    public bool TenBit { get; init; }

    /// <summary>For a software encoder: the export's own video arguments (MainViewModel.BuildVideoCodecArguments), given to FFmpeg as they are.</summary>
    public string SoftwareArguments { get; init; } = "";

    /// <summary>
    /// The loopback for an export's settings; null, and why in words, for a codec with no loopback (a stream copy, ProRes, DNxHR, a GIF or
    /// WebP). <paramref name="bitrateKbps"/> is the bitrate in force (typed, worked out from a target size, or the export's own automatic one).
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

    /// <summary>The codec of an FFmpeg encoder's name: h264_* and libx264 1, hevc_* and libx265 2, av1_* and the AV1 libraries 3, vp9_* and libvpx-vp9 4, else 0.</summary>
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

    /// <summary>The family of an FFmpeg encoder's name: *_qsv QuickSync, *_amf AMF, *_nvenc NVENC, lib* software, anything else (*_mf) Media Foundation.</summary>
    public static int FamilyOf(string encoderName)
    {
        var name = (encoderName ?? "").Trim().ToLowerInvariant();
        return name.EndsWith("_qsv", StringComparison.Ordinal) ? FamilyQuickSync
            : name.EndsWith("_amf", StringComparison.Ordinal) ? FamilyAmf
            : name.EndsWith("_nvenc", StringComparison.Ordinal) ? FamilyNvenc
            : name.StartsWith("lib", StringComparison.Ordinal) ? FamilySoftware
            : FamilyMediaFoundation;
    }

    /// <summary>The PCI vendor whose card a family's encoder is on: Intel for QuickSync, AMD for AMF, NVIDIA for NVENC; 0 for none (the compositor's card).</summary>
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
    /// The nearest a hardware encoder's quality against speed (0 fastest, 100 best) comes to an export preset: veryslow, slower, p7,
    /// quality and high_quality 100; slow and p6 83; medium, p4, p5 and balanced 50; fast, faster, p2 and p3 33; the fastest ones 0; anything else 50.
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
    /// The encoder as the ENCODED pill names it (5.4.4-Final): the codec and the family's short name for a hardware one ("HEVC QSV",
    /// "H.264 AMF", "AV1 NVENC", "HEVC MF"), the library and its preset for a software one ("x264 veryslow", "x265 slow", "SVT-AV1 6").
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

    /// <summary>The rate as the ENCODED pill says it: "12 Mbps", "CBR 8 Mbps", "CQ 22" (hardware) or "CRF 22" (software).</summary>
    public string RateShort => RateControl switch
    {
        QualityMode => string.Create(CultureInfo.InvariantCulture, $"{(Family == FamilySoftware ? "CRF" : "CQ")} {Crf}"),
        ConstantBitrate => "CBR " + BitrateText(BitrateKbps),
        _ => BitrateText(BitrateKbps),
    };

    /// <summary>A bitrate in words: "12 Mbps", "6.5 Mbps", "800 kbps".</summary>
    public static string BitrateText(int kbps) => kbps >= 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{kbps / 1000.0:0.#} Mbps")
        : string.Create(CultureInfo.InvariantCulture, $"{kbps} kbps");

    /// <summary>The ENCODED pill's text while the loopback runs: "ENCODED · HEVC QSV · 12 Mbps".</summary>
    public string PillText => $"ENCODED · {ShortName} · {RateShort}";

    /// <summary>
    /// The size the loopback encodes at (5.4.4-Final): the export's frame size, the canvas's own shape kept (the export's short side over
    /// the canvas's, both sides scaled by it and made even, at least 64 and at most 8192); the canvas's own size when the export names none.
    /// Pure: the worker (ExportLoopbackPreview) and the golden test both call it.
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
