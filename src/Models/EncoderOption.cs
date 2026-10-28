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
}

/// <summary>A video encoder as offered in the Video tab.</summary>
public sealed record EncoderOption(string Name, string DisplayName, EncoderFamily Family)
{
    /// <summary>Passes the source stream through untouched.</summary>
    public static EncoderOption Copy { get; } = new("copy", "Copy (Stream Copy)", EncoderFamily.Copy);

    public bool IsHardware => Family is EncoderFamily.Nvenc or EncoderFamily.Qsv or EncoderFamily.Amf;

    public static IReadOnlyList<EncoderOption> Software { get; } =
    [
        new("libx264", "H.264 (libx264)", EncoderFamily.Software),
        new("libx265", "H.265 (libx265)", EncoderFamily.Software),
        new("libsvtav1", "AV1 (libsvtav1)", EncoderFamily.Software),
        new("libvpx-vp9", "VP9 (libvpx-vp9)", EncoderFamily.Software),
        new("prores_ks", "Apple ProRes (prores_ks)", EncoderFamily.Intermediate),
        new("dnxhd", "Avid DNxHR (dnxhd)", EncoderFamily.Intermediate),
    ];

    /// <summary>Describes an FFmpeg hardware encoder such as "hevc_qsv" as "Intel QuickSync H.265 (hevc_qsv)".</summary>
    public static EncoderOption FromHardwareName(string name)
    {
        var parts = name.Split('_');
        var codec = parts[0] switch
        {
            "h264" => "H.264",
            "hevc" => "H.265",
            "av1" => "AV1",
            _ => parts[0].ToUpperInvariant(),
        };
        var (vendor, family) = parts[^1] switch
        {
            "nvenc" => ("NVIDIA NVENC", EncoderFamily.Nvenc),
            "qsv" => ("Intel QuickSync", EncoderFamily.Qsv),
            "amf" => ("AMD AMF", EncoderFamily.Amf),
            _ => ("Hardware", EncoderFamily.Software),
        };

        return new EncoderOption(name, $"{vendor} {codec} ({name})", family);
    }

    public override string ToString() => DisplayName;
}
