using System.Globalization;

namespace HandPegApp.Services;

/// <summary>
/// What the Export Quality Preview (Pass 5.4B) asks the compositor's encode loopback for, made from the export's settings: the codec
/// (1 H.264, 2 HEVC, 3 AV1), the rate control as Media Foundation names it (eAVEncCommonRateControlMode: 0 CBR, 2 unconstrained VBR,
/// 3 quality), the bitrate in kbps, the quality mode's 1..100 (<see cref="MediaFoundationEncoder.QualityFromCrf"/>), quality against
/// speed 0..100 (the nearest a hardware encoder has to the export's preset) and the export's frame rate.
/// <para>Pure (the golden export-loopback reads it): it takes the settings as words and numbers, not the view model, so the Export tab's
/// own names may change without it (MainViewModel.CurrentLoopbackSettings is the one place that reads them). The loopback is an
/// approximation: the export's own encoder may be FFmpeg's software one or another card's, and a paused frame is a key frame.</para>
/// </summary>
public sealed record ExportLoopbackSettings(int Codec, int RateControl, int BitrateKbps, int Quality, int QualityVsSpeed, double FrameRate)
{
    /// <summary>eAVEncCommonRateControlMode's values the export's three rate controls become.</summary>
    public const int ConstantBitrate = 0, UnconstrainedVbr = 2, QualityMode = 3;

    /// <summary>
    /// The loopback for an export's settings; null, and why in words, for a codec with no Media Foundation loopback (a stream copy,
    /// VP9, ProRes, DNxHR, a GIF or WebP). <paramref name="bitrateKbps"/> is the bitrate in force (typed, or the export's own automatic one).
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
            QualityVsSpeedOf(encoderPreset), fps);
    }

    /// <summary>The codec of an FFmpeg encoder's name: h264_* and libx264 1, hevc_* and libx265 2, av1_* and the AV1 libraries 3, else 0.</summary>
    public static int CodecOf(string encoderName)
    {
        var name = (encoderName ?? "").Trim().ToLowerInvariant();
        if (name is "libx264" or "libx264rgb" || name.StartsWith("h264_", StringComparison.Ordinal))
            return 1;
        if (name is "libx265" || name.StartsWith("hevc_", StringComparison.Ordinal))
            return 2;
        if (name is "libsvtav1" or "libaom-av1" or "librav1e" || name.StartsWith("av1_", StringComparison.Ordinal))
            return 3;
        return 0;
    }

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
        _ => "",
    };

    /// <summary>The rate control as the player's flyout says it.</summary>
    public string RateControlName => RateControl switch
    {
        ConstantBitrate => string.Create(CultureInfo.InvariantCulture, $"CBR {BitrateKbps} kbps"),
        QualityMode => string.Create(CultureInfo.InvariantCulture, $"constant quality {Quality}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"VBR {BitrateKbps} kbps"),
    };

    private static string WhyNot(string encoderName)
    {
        var name = (encoderName ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0 || name == "copy")
            return "a stream copy is not encoded again, so it looks as its source does";
        if (name.Contains("vp9", StringComparison.Ordinal))
            return "VP9 has no hardware encoder loopback; pick H.264, HEVC or AV1 to preview the encode";
        if (name.Contains("prores", StringComparison.Ordinal) || name.Contains("dnxhd", StringComparison.Ordinal))
            return "an editing codec (ProRes, DNxHR) keeps nearly every detail; there is nothing to preview";
        return string.Create(CultureInfo.InvariantCulture, $"{encoderName} has no Media Foundation encoder to preview it with");
    }
}
