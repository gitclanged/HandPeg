namespace HandPegApp.Models;

// ----- The colour sliders' grade (5.2-AuditB) -----
// What the colour sliders do to the whole finished picture, as numbers: the compositor's ColorGrade, HueSaturation and
// Sharpen packets are made from it directly (GpuScenePlan's Whole). Until 5.2-AuditB it travelled as FFmpeg eq, hue and
// unsharp filter text that the scene plan read back.

/// <summary>The colour sliders' grade of the whole picture (5.2-AuditB: typed, was FFmpeg eq / hue / unsharp text).</summary>
public readonly record struct SequenceGrade
{
    /// <summary>No grade: the picture as it is.</summary>
    public static SequenceGrade Neutral => default;

    /// <summary>Contrast, brightness, saturation, gamma and the per-channel gammas (1 = none), or null for none of them.</summary>
    public ColorBalance? Balance { get; init; }

    /// <summary>A hue turn in degrees, or null for none.</summary>
    public double? Hue { get; init; }

    /// <summary>The Sharpen strength (0..1, the slider's), or null for none.</summary>
    public double? Sharpen { get; init; }

    /// <summary>Nothing to do: no balance, no hue turn, no sharpening.</summary>
    public bool IsNeutral => Balance is null && Hue is null && Sharpen is null;
}

/// <summary>
/// The grade's eq part: contrast (1 none), brightness (0 none), saturation (1 none), gamma (1 none), and the red, green and
/// blue gammas of the channel balance (1 none; a slider at -1 is a half, at 1 two).
/// </summary>
public readonly record struct ColorBalance(double Contrast, double Brightness, double Saturation, double Gamma,
    double GammaRed, double GammaGreen, double GammaBlue);
