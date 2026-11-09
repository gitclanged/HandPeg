namespace HandPegApp.Models;

/// <summary>Decides which rate-control arguments an encoder takes.</summary>
public enum EncoderFamily
{
    Copy,
    Software,

    /// <summary>Editing codecs (ProRes, DNxHR): quality comes from their profile, not from a rate control.</summary>
    Intermediate,
    Nvenc,
    Qsv,
    Amf,

    /// <summary>Windows' Media Foundation hardware encoders (h264_mf, hevc_mf, av1_mf): the ARM64 path, see MediaFoundationEncoder.</summary>
    MediaFoundation,
}

/// <summary>A video encoder as offered in the Export tab.</summary>
public sealed record EncoderOption(string Name, string DisplayName, EncoderFamily Family)
{
    /// <summary>Passes the source stream through untouched.</summary>
    public static EncoderOption Copy { get; } = new("copy", "Copy (Stream Copy)", EncoderFamily.Copy);

    public bool IsHardware => Family is EncoderFamily.Nvenc or EncoderFamily.Qsv or EncoderFamily.Amf or EncoderFamily.MediaFoundation;

    public static IReadOnlyList<EncoderOption> Software { get; } =
    [
        new("libx264", "H.264 (libx264)", EncoderFamily.Software),
        new("libx265", "H.265 (libx265)", EncoderFamily.Software),
        new("libsvtav1", "AV1 (libsvtav1)", EncoderFamily.Software),
        new("libvpx-vp9", "VP9 (libvpx-vp9)", EncoderFamily.Software),
        new("prores_ks", "Apple ProRes (prores_ks)", EncoderFamily.Intermediate),
        new("dnxhd", "Avid DNxHR (dnxhd)", EncoderFamily.Intermediate),
    ];

    /// <summary>
    /// Describes an FFmpeg hardware encoder by its codec, then the hardware behind it (5.2-Audit, the user's directive):
    /// "hevc_qsv" as "HEVC (Intel QuickSync)", and a Media Foundation one by the chip it reaches where that is a Qualcomm
    /// Adreno (<see cref="Services.GpuAdapters.HasQualcommAdreno"/>: "h264_mf" as "H.264 (Qualcomm Adreno)"), else as
    /// "H.264 (Media Foundation)".
    /// </summary>
    public static EncoderOption FromHardwareName(string name) => FromHardwareName(name, Services.GpuAdapters.HasQualcommAdreno);

    /// <summary><see cref="FromHardwareName(string)"/> on a machine with, or without, a Qualcomm Adreno GPU.</summary>
    public static EncoderOption FromHardwareName(string name, bool qualcommAdreno)
    {
        var parts = name.Split('_');
        var codec = parts[0] switch
        {
            "h264" => "H.264",
            "hevc" => "HEVC",
            "av1" => "AV1",
            _ => parts[0].ToUpperInvariant(),
        };
        var (vendor, family) = parts[^1] switch
        {
            "nvenc" => ("NVIDIA NVENC", EncoderFamily.Nvenc),
            "qsv" => ("Intel QuickSync", EncoderFamily.Qsv),
            "amf" => ("AMD AMF", EncoderFamily.Amf),
            "mf" => (qualcommAdreno ? "Qualcomm Adreno" : "Media Foundation", EncoderFamily.MediaFoundation),
            _ => ("Hardware", EncoderFamily.Software),
        };

        return new EncoderOption(name, $"{codec} ({vendor})", family);
    }

    public override string ToString() => DisplayName;
}
