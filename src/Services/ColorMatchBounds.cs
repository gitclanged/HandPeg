using HandPegApp.Models.Effects;

namespace HandPegApp.Services;

/// <summary>
/// Grading-wheel push math (WPF-free; ColorWheelsControl draws and drags it): a wheel's RGB splits into a master (the channel mean) and a
/// puck offset in push units, <see cref="Reach"/> at the disc's rim. Primary push directions are 120 degrees apart (vectorscope order: red
/// upper left, green lower left, blue right); offset P gives channel i the push P . e_i, and a push c maps back to P = 2/3 sum c_i e_i.
/// </summary>
public static class WheelPush
{
    /// <summary>Push magnitude at the disc's rim (channels range -1..1; the push is the channels minus their mean).</summary>
    public const double Reach = 0.5;

    /// <summary>Push directions of red, green and blue, degrees anticlockwise from +X.</summary>
    public static ReadOnlySpan<double> HueAngles => [105, 225, 345];

    /// <summary>Splits RGB into the master (channel mean) and the puck offset in push units (+Y up).</summary>
    public static (double Master, double X, double Y) Split((double R, double G, double B) value)
    {
        var master = (value.R + value.G + value.B) / 3;
        var (x, y) = (0.0, 0.0);
        for (var i = 0; i < 3; i++)
        {
            var push = (i == 0 ? value.R : i == 1 ? value.G : value.B) - master;
            var angle = HueAngles[i] * Math.PI / 180;
            (x, y) = (x + 2.0 / 3 * push * Math.Cos(angle), y + 2.0 / 3 * push * Math.Sin(angle));
        }

        return (master, x, y);
    }

    /// <summary>Inverse of <see cref="Split"/>: master + puck offset to RGB, each channel clamped to -1..1.</summary>
    public static (double R, double G, double B) Join(double master, double x, double y)
    {
        var angles = HueAngles;
        double Channel(double degrees) => Math.Max(-1, Math.Min(master + x * Math.Cos(degrees * Math.PI / 180) + y * Math.Sin(degrees * Math.PI / 180), 1));
        return (Channel(angles[0]), Channel(angles[1]), Channel(angles[2]));
    }

    /// <summary>
    /// An offset held inside the disc of radius <paramref name="limit"/> (push units): longer offsets scale back along their direction, a
    /// non-finite one is the centre. The wheel's puck, its stem and its pointer math all go through this (sqrt(x^2 + y^2) never exceeds it).
    /// </summary>
    public static (double X, double Y) ClampToDisc(double x, double y, double limit = Reach)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return (0, 0);
        var length = Math.Sqrt(x * x + y * y);
        return length > limit && length > 0 ? (x * limit / length, y * limit / length) : (x, y);
    }

    /// <summary>The share of the rim (0 = centre, 1 = rim) an RGB's push reaches, unclamped.</summary>
    public static double RimShare((double R, double G, double B) value)
    {
        var (_, x, y) = Split(value);
        return Math.Sqrt(x * x + y * y) / Reach;
    }
}

/// <summary>
/// Color Match's natural bounds and strength (Pass 5.6.4B). The solver's chromatic adaptation (Temperature, Tint) and its Lift/Gamma/Gain
/// pushes are held within <see cref="WheelLimit"/> of their reach, so a match never paints a cast a colourist would not; the bar's Match
/// Strength blends from the grade before the match (0 %) to the solved grade (100 %). Pure and golden-tested; ColorMatch.SolveFrom
/// regularizes its solution through <see cref="Regularize"/> and ColorMatchBar applies <see cref="Blend"/>.
/// </summary>
public static class ColorMatchBounds
{
    /// <summary>The most a solved wheel push reaches, as a share of the wheel radius; and Temperature/Tint as a share of their reach.</summary>
    public const double WheelLimit = 0.75;

    /// <summary>Temperature and Tint reach (the sliders run -100..100): solved values stay within +/- 75.</summary>
    public const double BalanceReach = 100;

    /// <summary>Match Strength's default and its reset (double-click): the whole solved grade.</summary>
    public const double FullStrength = 1;

    /// <summary>The solved grade held within the natural bounds: Temperature/Tint clamped, each wheel's push scaled back (its master kept).</summary>
    public static ColorGradeValues Regularize(in ColorGradeValues values)
    {
        var balance = WheelLimit * BalanceReach;
        var lift = Wheel((values.LiftR, values.LiftG, values.LiftB));
        var gamma = Wheel((values.GammaR, values.GammaG, values.GammaB));
        var gain = Wheel((values.GainR, values.GainG, values.GainB));
        return values with
        {
            Temperature = Math.Clamp(Finite(values.Temperature), -balance, balance),
            Tint = Math.Clamp(Finite(values.Tint), -balance, balance),
            LiftR = lift.R, LiftG = lift.G, LiftB = lift.B,
            GammaR = gamma.R, GammaG = gamma.G, GammaB = gamma.B,
            GainR = gain.R, GainG = gain.G, GainB = gain.B,
        };
    }

    /// <summary>True when the values sit within the natural bounds (to <paramref name="slack"/>).</summary>
    public static bool Within(in ColorGradeValues values, double slack = 1e-9)
    {
        var balance = WheelLimit * BalanceReach + slack;
        return Math.Abs(values.Temperature) <= balance && Math.Abs(values.Tint) <= balance
            && WheelPush.RimShare((values.LiftR, values.LiftG, values.LiftB)) <= WheelLimit + slack
            && WheelPush.RimShare((values.GammaR, values.GammaG, values.GammaB)) <= WheelLimit + slack
            && WheelPush.RimShare((values.GainR, values.GainG, values.GainB)) <= WheelLimit + slack;
    }

    /// <summary>One wheel's RGB with its push held within <see cref="WheelLimit"/> of the rim (its master unchanged).</summary>
    public static (double R, double G, double B) Wheel((double R, double G, double B) value)
    {
        value = (Finite(value.R), Finite(value.G), Finite(value.B));
        if (WheelPush.RimShare(value) <= WheelLimit)
            return value;
        var (master, x, y) = WheelPush.Split(value);
        var (cx, cy) = WheelPush.ClampToDisc(x, y, WheelLimit * WheelPush.Reach);
        return WheelPush.Join(master, cx, cy);
    }

    /// <summary>
    /// The grade at Match Strength <paramref name="strength"/> (0..1; NaN reads as full): every value eased from <paramref name="before"/>
    /// (the grade before the match) to <paramref name="solved"/>. 0 gives the grade before, 1 the solved grade.
    /// </summary>
    public static ColorGradeValues Blend(in ColorGradeValues before, in ColorGradeValues solved, double strength)
    {
        var s = double.IsNaN(strength) ? FullStrength : Math.Clamp(strength, 0, 1);
        double L(double a, double b) => s >= 1 ? b : s <= 0 ? a : a + (b - a) * s;
        return new ColorGradeValues(
            L(before.Temperature, solved.Temperature), L(before.Tint, solved.Tint), L(before.Exposure, solved.Exposure),
            L(before.LiftR, solved.LiftR), L(before.LiftG, solved.LiftG), L(before.LiftB, solved.LiftB),
            L(before.GammaR, solved.GammaR), L(before.GammaG, solved.GammaG), L(before.GammaB, solved.GammaB),
            L(before.GainR, solved.GainR), L(before.GainG, solved.GainG), L(before.GainB, solved.GainB),
            L(before.Contrast, solved.Contrast), L(before.Saturation, solved.Saturation), L(before.Vibrance, solved.Vibrance), L(before.LutMix, solved.LutMix));
    }

    /// <summary>Match Strength as the slider's value box shows it ("75%").</summary>
    public static string StrengthText(double strength) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Round(Math.Clamp(double.IsNaN(strength) ? FullStrength : strength, 0, 1) * 100):0}%");

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
