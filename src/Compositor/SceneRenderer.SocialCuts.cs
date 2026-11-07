using System.Numerics;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- The social cuts (Pass 5.2.1, slots 70-73: SocialCuts.hlsl) -----
// Whip Pan, Zoom Warp, RGB Glitch and Spin: one pass each over a layer's working picture after its effects (SocialCut), and
// what they do as it is laid on: a zoom warp and a spin come on over the first quarter of their fade in (and go over the last
// quarter of their fade out), a glitch blinks out at some of its moments, a spin turns its box a whole turn about its anchor.
// Every packet is TransitionStyles.GpuPacket's: P0 how much of the clip is there, P3 how far through the fade, P4 1 for a fade in.
internal sealed unsafe partial class SceneRenderer
{
    /// <summary>The social cut's pass over the working picture (the same one when there is nothing to do: the clip all there).</summary>
    private GpuTexture SocialCut(in GpuLayerDescriptor layer, GpuEffectPacket cut, GpuTexture work)
    {
        var (shown, through) = (Math.Clamp(cut.P0, 0, 1), Math.Clamp(cut.P3, 0, 1));
        return cut.Slot switch
        {
            GpuShaderSlot.TransitionWhipPan when shown < 1 || (through > 0 && through < 1) => Effect("PSWhipPan", cut, default, work),
            GpuShaderSlot.TransitionZoomWarp when shown < 1 => Effect("PSZoomWarp", cut, default, work),
            GpuShaderSlot.TransitionRgbGlitch when shown < 1 => Effect("PSRgbGlitch", cut, default, work),
            GpuShaderSlot.TransitionSpin when through > 0 && through < 1 =>
                Effect("PSSpin", cut, default, work, params2: new Vector4(layer.AnchorX, layer.AnchorY, 0, 0)),
            _ => work,
        };
    }

    /// <summary>How much of the layer a social cut lets through as it is laid on (the transition alpha's share).</summary>
    internal static float SocialCutDissolve(GpuEffectPacket cut)
    {
        var through = Math.Clamp(cut.P3, 0, 1);
        return cut.Slot switch
        {
            GpuShaderSlot.TransitionZoomWarp or GpuShaderSlot.TransitionSpin => Math.Clamp((cut.P4 > 0.5f ? through : 1 - through) * 4, 0, 1),
            GpuShaderSlot.TransitionRgbGlitch => GlitchBlinks(cut) ? 0 : 1,
            _ => 1,
        };
    }

    /// <summary>
    /// Whether a glitch's frame blinks out at its moment (what is under it shows): by chance, more often the less of the clip
    /// is there, the same for the same moment. SocialCuts.hlsl's hash and step.
    /// </summary>
    internal static bool GlitchBlinks(GpuEffectPacket cut) =>
        GlitchRandom(GlitchStep(cut.P3), 13) < 0.35f * (1 - Math.Clamp(cut.P0, 0, 1));

    /// <summary>A spin's turn of its box at its moment, in degrees: a whole turn clockwise over the fade, upright where the clip is all there.</summary>
    internal static float SpinTurn(GpuEffectPacket cut) => (cut.P4 > 0.5f ? -360 : 360) * (1 - Math.Clamp(cut.P0, 0, 1));

    // SocialCuts.hlsl's GlitchSteps, GlitchHash, GlitchRandom and GlitchStep.
    private const int GlitchSteps = 30;

    private static uint GlitchHash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    private static float GlitchRandom(uint a, uint b) => (GlitchHash(unchecked(a * 0x9e3779b9u) ^ GlitchHash(b)) >> 8) / 16777216f;

    private static uint GlitchStep(float through) => (uint)Math.Min(MathF.Floor(Math.Clamp(through, 0, 1) * GlitchSteps), GlitchSteps - 1);
}
