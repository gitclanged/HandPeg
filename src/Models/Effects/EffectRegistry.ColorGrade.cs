namespace HandPegApp.Models.Effects;

// ----- 5.6.0: the Color Grade card of the Color Studio -----
// One card holds the whole grade of a clip or of the Sequence Output: white balance, exposure, the three wheels, contrast, saturation,
// vibrance, the curves and a .cube LUT with its mix. Sixteen number lanes (two packets, GpuShaderSlot.ColorStudio); the lane order is a
// contract with Effects.hlsl (PSColorStudio) and ColorGradeMath: neither the keys nor their order change. No lane that can be -1 sits in
// a packet's last lane (GpuEffectPacket.PulseMarker). The wheels and the curves are edited in the Color Studio only (EffectParam.StudioOnly).
public static partial class EffectRegistry
{
    /// <summary>The Color Studio's grade (5.6.0).</summary>
    public const string ColorStudio = "builtin.color.studio";

    private static void RegisterColorStudio()
    {
        static EffectParam Wheel(string key, string label) =>
            new(key, label, Min: -1, Max: 1, Default: 0, Step: 0.001, Animation: ParamAnimation.Expression) { StudioOnly = true };

        static EffectParam Curve(string key, string label) => new(key, label, ParamKind.Text) { StudioOnly = true };

        Register(new(ColorStudio, "Color Grade", "Color", EffectZone.Look,
            [
                // Packet 1: P0..P7.
                new(ColorGradeKeys.Temperature, "Temperature", Min: -100, Max: 100, Default: 0, Step: 1, Animation: ParamAnimation.Expression) { Tip = "Cooler (left) or warmer (right)." },
                new(ColorGradeKeys.Tint, "Tint", Min: -100, Max: 100, Default: 0, Step: 1, Animation: ParamAnimation.Expression) { Tip = "Greener (left) or more magenta (right)." },
                new(ColorGradeKeys.Exposure, "Exposure", Min: -3, Max: 3, Default: 0, Step: 0.01, Unit: " EV", Animation: ParamAnimation.Expression) { Tip = "Brighter or darker, in stops." },
                Wheel(ColorGradeKeys.LiftR, "Lift Red"), Wheel(ColorGradeKeys.LiftG, "Lift Green"), Wheel(ColorGradeKeys.LiftB, "Lift Blue"),
                Wheel(ColorGradeKeys.GammaR, "Gamma Red"),
                new(ColorGradeKeys.Contrast, "Contrast", Min: 0, Max: 2, Default: 1, Animation: ParamAnimation.Expression),
                // Packet 2: P0..P7 (..).
                Wheel(ColorGradeKeys.GammaG, "Gamma Green"), Wheel(ColorGradeKeys.GammaB, "Gamma Blue"),
                Wheel(ColorGradeKeys.GainR, "Gain Red"), Wheel(ColorGradeKeys.GainG, "Gain Green"), Wheel(ColorGradeKeys.GainB, "Gain Blue"),
                new(ColorGradeKeys.Saturation, "Saturation", Min: 0, Max: 2, Default: 1, Animation: ParamAnimation.Expression),
                new(ColorGradeKeys.Vibrance, "Vibrance", Min: -1, Max: 1, Default: 0, Animation: ParamAnimation.Expression) { Tip = "Saturates the duller colours most." },
                new(ColorGradeKeys.Lut, "LUT", ParamKind.File) { Tip = "A .cube look file." },
                new(ColorGradeKeys.LutMix, "LUT Mix", Min: 0, Max: 100, Default: 100, Step: 1, Unit: "%", Animation: ParamAnimation.Expression) { GpuScale = 0.01 },
                // No lanes: the curves, as "x/y x/y ..." points from 0 to 1 (Master first, then each channel's).
                Curve(ColorGradeKeys.CurveMaster, "Master Curve"), Curve(ColorGradeKeys.CurveRed, "Red Curve"),
                Curve(ColorGradeKeys.CurveGreen, "Green Curve"), Curve(ColorGradeKeys.CurveBlue, "Blue Curve"),
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.ColorStudio, Once = true,
            Description = "White balance, exposure, wheels, curves and a LUT: the Color Studio's grade.",
        });
    }
}
