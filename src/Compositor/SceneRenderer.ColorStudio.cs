using System.Numerics;
using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Compositor;

// ----- Color Studio grade (GpuShaderSlot.ColorStudio, PSColorStudio): two packets, curves (Aux), LUT -----
// Single pass: white balance, exposure, wheels, contrast, saturation, vibrance (mirrors ColorGradeMath.Apply), then the card's curves
// and LUT lerped by mix. GpuColorTables.StudioTablesLane marks which layer tables belong to this card, so none is applied twice.
internal sealed unsafe partial class SceneRenderer
{
    private GpuTexture RunColorStudio(in GpuLayerDescriptor layer, GpuEffectPacket packet, GpuEffectPacket second, GpuTexture work)
    {
        // Packets always arrive as a pair (the plan drops a card with no room for the second); a lone first packet is a no-op.
        if (second.Slot != GpuShaderSlot.ColorStudio)
            return work;

        var (holdsLut, holdsCurves, mix) = GpuColorTables.StudioTablesOf(second.P7);
        var lut = holdsLut && mix > 0 ? _resources.Lut(layer.LutResourceId) : null;
        var curve = holdsCurves ? _resources.Texture2D(layer.CurveResourceId) : null;

        // Neutral values and no tables: skip the pass so the frame passes through bit-exact.
        if (lut is null && curve is null && StudioNeutral(packet, second))
            return work;

        return Effect("PSColorStudio", packet, second, work, aux: curve?.Srv ?? 0, lut: lut?.Srv ?? 0,
            params2: new Vector4(lut?.Size ?? 0, mix, curve is null ? 0 : 1, 0));
    }

    /// <summary>True when every lane is neutral (ColorGradeValues.IsNeutral in packet form).</summary>
    internal static bool StudioNeutral(in GpuEffectPacket p, in GpuEffectPacket q) =>
        p.P0 == 0 && p.P1 == 0 && p.P2 == 0 && p.P3 == 0 && p.P4 == 0 && p.P5 == 0 && p.P6 == 0 && p.P7 == 1
        && q.P0 == 0 && q.P1 == 0 && q.P2 == 0 && q.P3 == 0 && q.P4 == 0 && q.P5 == 1 && q.P6 == 0;
}
