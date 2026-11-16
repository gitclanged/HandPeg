using System.Numerics;
using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Compositor;

// ----- The Color Studio's grade (GpuShaderSlot.ColorStudio, PSColorStudio): two packets, the curves (Aux), the LUT -----
// One pass: white balance, exposure, the wheels, contrast, saturation and vibrance (ColorGradeMath.Apply, step for step), then the card's
// curves and its LUT lerped by its mix. The plan says in the second packet's last lane which of the layer's tables are the card's
// (GpuColorTables.StudioTablesLane: a LUT or Curves card on the same clip may hold them), so a table is never applied twice.
internal sealed unsafe partial class SceneRenderer
{
    private GpuTexture RunColorStudio(in GpuLayerDescriptor layer, GpuEffectPacket packet, GpuEffectPacket second, GpuTexture work)
    {
        // Always sent as a pair (the plan drops a card whose second packet found no room): a lone first packet does nothing.
        if (second.Slot != GpuShaderSlot.ColorStudio)
            return work;

        var (holdsLut, holdsCurves, mix) = GpuColorTables.StudioTablesOf(second.P7);
        var lut = holdsLut && mix > 0 ? _resources.Lut(layer.LutResourceId) : null;
        var curve = holdsCurves ? _resources.Texture2D(layer.CurveResourceId) : null;

        // A card at its neutral values with no table changes nothing: no pass (and the picture exactly as it came).
        if (lut is null && curve is null && StudioNeutral(packet, second))
            return work;

        return Effect("PSColorStudio", packet, second, work, aux: curve?.Srv ?? 0, lut: lut?.Srv ?? 0,
            params2: new Vector4(lut?.Size ?? 0, mix, curve is null ? 0 : 1, 0));
    }

    /// <summary>Whether the card's lanes are all at their neutral values (ColorGradeValues.IsNeutral, as the packets carry them).</summary>
    internal static bool StudioNeutral(in GpuEffectPacket p, in GpuEffectPacket q) =>
        p.P0 == 0 && p.P1 == 0 && p.P2 == 0 && p.P3 == 0 && p.P4 == 0 && p.P5 == 0 && p.P6 == 0 && p.P7 == 1
        && q.P0 == 0 && q.P1 == 0 && q.P2 == 0 && q.P3 == 0 && q.P4 == 0 && q.P5 == 1 && q.P6 == 0;
}
