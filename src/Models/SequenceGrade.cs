namespace HandPegApp.Models;

// ----- Colour slider grade -----
// Typed whole-picture adjustments from the colour sliders, converted directly into ColorGrade, HueSaturation and Sharpen packets
// (GpuScenePlan.Whole).

/// <summary>Whole-picture colour slider grade.</summary>
public readonly record struct SequenceGrade
{
    /// <summary>No grade: the picture as it is.</summary>
    public static SequenceGrade Neutral => default;

    /// <summary>Contrast, brightness, saturation, gamma and per-channel gammas (1 = neutral), or null when all neutral.</summary>
    public ColorBalance? Balance { get; init; }

    /// <summary>A hue turn in degrees, or null for none.</summary>
    public double? Hue { get; init; }

    /// <summary>The Sharpen strength (0..1, the slider's), or null for none.</summary>
    public double? Sharpen { get; init; }

    /// <summary>Nothing to do: no balance, no hue turn, no sharpening.</summary>
    public bool IsNeutral => Balance is null && Hue is null && Sharpen is null;
}

/// <summary>
/// EQ component: contrast (1 neutral), brightness (0), saturation (1), gamma (1) and R/G/B balance gammas (1 neutral; slider -1 = 0.5,
/// +1 = 2).
/// </summary>
public readonly record struct ColorBalance(double Contrast, double Brightness, double Saturation, double Gamma,
    double GammaRed, double GammaGreen, double GammaBlue);
