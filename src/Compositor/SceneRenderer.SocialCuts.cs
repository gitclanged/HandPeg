using System.Numerics;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Social cuts (slots 70-73, SocialCuts.hlsl) -----
// Whip Pan, Zoom Warp, RGB Glitch and Spin: one pass over the layer's working picture after its effects, plus their alpha: Zoom Warp
// and Spin dissolve over the first/last quarter of the fade, Glitch blinks out on random steps, Spin rotates the box about its anchor.
// Packets come from TransitionStyles.GpuPacket: P0 visible share, P3 fade progress, P4 1 for a fade in.
internal sealed unsafe partial class SceneRenderer
{
    /// <summary>Applies the social cut to the working picture; returns it unchanged when there is nothing to draw.</summary>
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

    /// <summary>Transition alpha factor contributed by the social cut.</summary>
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
    /// Whether the glitch blinks out (layer hidden) at this step: deterministic per step, likelier as the visible share drops.
    /// Mirrors SocialCuts.hlsl's hash and step.
    /// </summary>
    internal static bool GlitchBlinks(GpuEffectPacket cut) =>
        GlitchRandom(GlitchStep(cut.P3), 13) < 0.35f * (1 - Math.Clamp(cut.P0, 0, 1));

    /// <summary>Spin angle in degrees: one full turn over the fade (sign by direction), 0 when fully shown.</summary>
    internal static float SpinTurn(GpuEffectPacket cut) => (cut.P4 > 0.5f ? -360 : 360) * (1 - Math.Clamp(cut.P0, 0, 1));

    // Mirrors SocialCuts.hlsl's GlitchSteps, GlitchHash, GlitchRandom and GlitchStep.
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
