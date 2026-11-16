namespace HandPegApp.Models.Effects;

/// <summary>
/// Color Grade card parameter keys (<see cref="EffectRegistry.ColorStudio"/>), a contract shared with Effects.hlsl PSColorStudio, the
/// Color Studio controls and Color Match. Never rename.
/// </summary>
public static class ColorGradeKeys
{
    public const string Temperature = "temperature", Tint = "tint", Exposure = "exposure", Contrast = "contrast", Saturation = "saturation",
        Vibrance = "vibrance", LiftR = "lift_r", LiftG = "lift_g", LiftB = "lift_b", GammaR = "gamma_r", GammaG = "gamma_g", GammaB = "gamma_b",
        GainR = "gain_r", GainG = "gain_g", GainB = "gain_b", Lut = "lut", LutMix = "lut_mix",
        CurveMaster = "curve_m", CurveRed = "curve_r", CurveGreen = "curve_g", CurveBlue = "curve_b";

    /// <summary>The curves' keys: Master, Red, Green, Blue.</summary>
    public static readonly string[] Curves = [CurveMaster, CurveRed, CurveGreen, CurveBlue];
}

/// <summary>
/// Color Studio target: a clip (or blur fill) stack, the Sequence Output, or none.
/// </summary>
public readonly record struct ColorGradeTarget(Layer? Layer, bool Sequence)
{
    public static ColorGradeTarget None => default;

    public static ColorGradeTarget ForSequence => new(null, true);

    public static ColorGradeTarget For(Layer layer) => new(layer, false);

    public bool IsNone => Layer is null && !Sequence;
}

/// <summary>
/// Parametric Color Grade values (everything except curves and the LUT file).
/// </summary>
public readonly record struct ColorGradeValues(
    double Temperature, double Tint, double Exposure,
    double LiftR, double LiftG, double LiftB,
    double GammaR, double GammaG, double GammaB,
    double GainR, double GainG, double GainB,
    double Contrast, double Saturation, double Vibrance, double LutMix)
{
    /// <summary>A card that changes nothing (its LUT Mix at 100 %, as a new card has it).</summary>
    public static ColorGradeValues Neutral => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 100);

    public bool LiftActive => LiftR != 0 || LiftG != 0 || LiftB != 0;

    public bool GammaActive => GammaR != 0 || GammaG != 0 || GammaB != 0;

    public bool GainActive => GainR != 0 || GainG != 0 || GainB != 0;

    /// <summary>True when the parametric grade is identity (curves and LUT checked separately by <see cref="ColorGradeMath"/>).</summary>
    public bool IsNeutral => Temperature == 0 && Tint == 0 && Exposure == 0 && !LiftActive && !GammaActive && !GainActive
        && Contrast == 1 && Saturation == 1 && Vibrance == 0;

    /// <summary>The card's values now (its static values: keyframes are read where the card is drawn).</summary>
    public static ColorGradeValues From(EffectInstance card) => new(
        card.Get(ColorGradeKeys.Temperature), card.Get(ColorGradeKeys.Tint), card.Get(ColorGradeKeys.Exposure),
        card.Get(ColorGradeKeys.LiftR), card.Get(ColorGradeKeys.LiftG), card.Get(ColorGradeKeys.LiftB),
        card.Get(ColorGradeKeys.GammaR), card.Get(ColorGradeKeys.GammaG), card.Get(ColorGradeKeys.GammaB),
        card.Get(ColorGradeKeys.GainR), card.Get(ColorGradeKeys.GainG), card.Get(ColorGradeKeys.GainB),
        card.Get(ColorGradeKeys.Contrast), card.Get(ColorGradeKeys.Saturation), card.Get(ColorGradeKeys.Vibrance), card.Get(ColorGradeKeys.LutMix));

    /// <summary>Writes all values to the card (range-clamped), raising ValueChanged per changed value.</summary>
    public void WriteTo(EffectInstance card)
    {
        void Set(string key, double value)
        {
            var param = card.Definition?.Param(key);
            card.Set(key, param is null ? value : Math.Max(param.Min, Math.Min(value, param.Max)));
        }

        Set(ColorGradeKeys.Temperature, Temperature); Set(ColorGradeKeys.Tint, Tint); Set(ColorGradeKeys.Exposure, Exposure);
        Set(ColorGradeKeys.LiftR, LiftR); Set(ColorGradeKeys.LiftG, LiftG); Set(ColorGradeKeys.LiftB, LiftB);
        Set(ColorGradeKeys.GammaR, GammaR); Set(ColorGradeKeys.GammaG, GammaG); Set(ColorGradeKeys.GammaB, GammaB);
        Set(ColorGradeKeys.GainR, GainR); Set(ColorGradeKeys.GainG, GainG); Set(ColorGradeKeys.GainB, GainB);
        Set(ColorGradeKeys.Contrast, Contrast); Set(ColorGradeKeys.Saturation, Saturation); Set(ColorGradeKeys.Vibrance, Vibrance);
        Set(ColorGradeKeys.LutMix, LutMix);
    }
}

/// <summary>
/// Pure Color Grade reference math mirrored step for step by Effects.hlsl PSColorStudio (enforced by the compositor-parity golden) and
/// used as Color Match's model. Inputs are display-referred, gamma-encoded 0..1. Order: white balance and exposure in linear light,
/// lift/gamma/gain, contrast about 0.5, saturation and vibrance about BT.709 luma, clamp; then (GPU only, via tables) curves and LUT.
/// Neutral steps are skipped so a neutral card is an exact identity.
/// </summary>
public static class ColorGradeMath
{
    public const double LumaR = 0.2126, LumaG = 0.7152, LumaB = 0.0722;

    /// <summary>Linear-light gain swing at +-100: Temperature (red vs. blue) and Tint (green vs. red/blue).</summary>
    public const double TemperatureReach = 0.3, TintReach = 0.25;

    public static double SrgbToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static double LinearToSrgb(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    /// <summary>
    /// Linear-light white-balance RGB gains for Temperature and Tint (-100..100), normalized to preserve BT.709 luma.
    /// </summary>
    public static (double R, double G, double B) WhiteBalance(double temperature, double tint)
    {
        var t = Math.Max(-1, Math.Min(temperature / 100, 1));
        var n = Math.Max(-1, Math.Min(tint / 100, 1));
        var r = (1 + TemperatureReach * t) * (1 + TintReach / 2 * n);
        var g = 1 - TintReach * n;
        var b = (1 - TemperatureReach * t) * (1 + TintReach / 2 * n);
        var luma = LumaR * r + LumaG * g + LumaB * b;
        return (r / luma, g / luma, b / luma);
    }

    /// <summary>The parametric grade of one colour (red, green, blue 0..1), clamped to 0..1.</summary>
    public static (double R, double G, double B) Apply(double r, double g, double b, in ColorGradeValues v)
    {
        // 1. White balance and exposure, in linear light.
        if (v.Exposure != 0 || v.Temperature != 0 || v.Tint != 0)
        {
            var (wr, wg, wb) = WhiteBalance(v.Temperature, v.Tint);
            var gain = Math.Pow(2, v.Exposure);
            r = LinearToSrgb(Math.Max(SrgbToLinear(Math.Max(r, 0)) * wr * gain, 0));
            g = LinearToSrgb(Math.Max(SrgbToLinear(Math.Max(g, 0)) * wg * gain, 0));
            b = LinearToSrgb(Math.Max(SrgbToLinear(Math.Max(b, 0)) * wb * gain, 0));
        }

        // 2. Lift (0.5 of the range at black), gain (2^x) and gamma (exponent 2^-x), per channel.
        if (v.LiftActive || v.GammaActive || v.GainActive)
        {
            r = Wheels(r, v.LiftR, v.GammaR, v.GainR);
            g = Wheels(g, v.LiftG, v.GammaG, v.GainG);
            b = Wheels(b, v.LiftB, v.GammaB, v.GainB);
        }

        // 3. Contrast about the middle.
        if (v.Contrast != 1)
        {
            r = (r - 0.5) * v.Contrast + 0.5;
            g = (g - 0.5) * v.Contrast + 0.5;
            b = (b - 0.5) * v.Contrast + 0.5;
        }

        // 4. Saturation, and vibrance (more where the colour is duller), about BT.709 luma.
        if (v.Saturation != 1 || v.Vibrance != 0)
        {
            var y = LumaR * r + LumaG * g + LumaB * b;
            var spread = Math.Max(0, Math.Min(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)), 1));
            var amount = v.Saturation * (1 + v.Vibrance * (1 - spread));
            r = y + (r - y) * amount;
            g = y + (g - y) * amount;
            b = y + (b - y) * amount;
        }

        return (Clamp01(r), Clamp01(g), Clamp01(b));
    }

    /// <summary>One channel through lift, gain and gamma (each -1..1, 0 changes nothing).</summary>
    public static double Wheels(double c, double lift, double gamma, double gain)
    {
        var l = 0.5 * lift;
        c = c + l * (1 - c);
        c *= Math.Pow(2, gain);
        return Math.Pow(Math.Max(c, 0), Math.Pow(2, -gamma));
    }

    /// <summary>Whether a curve's points ("x/y x/y ...") change nothing: empty, or every point on the diagonal.</summary>
    public static bool CurveIsIdentity(string? points)
    {
        if (string.IsNullOrWhiteSpace(points))
            return true;

        foreach (var pair in points.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('/');
            if (parts.Length != 2
                || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
                || Math.Abs(x - y) > 1e-6)
                return false;
        }

        return true;
    }

    /// <summary>Whether any of the card's curves changes the picture (the Effects pane's curve indicator).</summary>
    public static bool CurvesActive(EffectInstance card)
    {
        foreach (var key in ColorGradeKeys.Curves)
        {
            if (!CurveIsIdentity(card.GetText(key)))
                return true;
        }

        return false;
    }

    /// <summary>The 16 numeric keys in lane order (P0..P7, ...), matching <see cref="ColorGradeValues"/>.</summary>
    public static readonly string[] NumberKeys =
    [
        ColorGradeKeys.Temperature, ColorGradeKeys.Tint, ColorGradeKeys.Exposure, ColorGradeKeys.LiftR, ColorGradeKeys.LiftG, ColorGradeKeys.LiftB,
        ColorGradeKeys.GammaR, ColorGradeKeys.Contrast, ColorGradeKeys.GammaG, ColorGradeKeys.GammaB, ColorGradeKeys.GainR, ColorGradeKeys.GainG,
        ColorGradeKeys.GainB, ColorGradeKeys.Saturation, ColorGradeKeys.Vibrance, ColorGradeKeys.LutMix,
    ];

    /// <summary>True when any numeric key is keyframed (rendered values then differ from the static ones Color Match reads).</summary>
    public static bool IsKeyframed(EffectInstance card)
    {
        foreach (var key in NumberKeys)
        {
            if (card.HasKeys(key))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when a LUT file is set and its mix is above 0. Allocation-free (Color Match calls it on every pick move).
    /// </summary>
    public static bool LutMixedIn(EffectInstance card) =>
        card.Texts.TryGetValue(ColorGradeKeys.Lut, out var lut) && !string.IsNullOrWhiteSpace(lut) && card.Get(ColorGradeKeys.LutMix) > 0;

    private static double Clamp01(double c) => c < 0 ? 0 : c > 1 ? 1 : c;
}
