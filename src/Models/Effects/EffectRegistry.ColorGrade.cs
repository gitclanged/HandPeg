namespace HandPegApp.Models.Effects;

// ----- Color Studio: Color Grade card -----
// One card holds a clip's or Sequence Output's full grade: white balance, exposure, three wheels, contrast, saturation, vibrance, curves
// and a .cube LUT with mix. Sixteen numeric lanes (two packets, GpuShaderSlot.ColorStudio) whose keys and order are a contract with
// Effects.hlsl PSColorStudio and ColorGradeMath. No lane that can be -1 occupies a packet's last lane (GpuEffectPacket.PulseMarker).
// Wheels and curves are Color Studio-only (EffectParam.StudioOnly).
public static partial class EffectRegistry
{
    /// <summary>Color Studio grade card definition.</summary>
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
