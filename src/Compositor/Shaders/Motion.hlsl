// Motion Blur (slot 22): shutter blur from measured motion.
//
// - Content motion: optical flow between the shown frame and the previous one, both read from FrameCache (no decode demand, so it
//   never makes a frame late). Coarse-to-fine Lucas-Kanade pyramid from quarter resolution down (PSFlowSolve, each level warped by
//   the coarser one), one smoothing pass (PSFlowSmooth), cached per source frame (SceneRenderer.Fx.cs). Adjustment layers and the
//   Sequence Output measure flow between successive canvases.
// - Layer motion (move, zoom, rotate): exact shutter sub-frames of the transformed picture (SceneRenderer.Fx.cs Shutter).
// Conventions: Common.hlsli.

#include "Common.hlsli"

// Coarser level's flow, in that level's texels: t5.
Texture2D<float4> Coarser : register(t5);

float LevelAt(Texture2D<float4> level, float2 uv) { return dot(level.SampleLevel(LinearClamp, uv, 0).rgb, Luma709); }

// One Lucas-Kanade solve. Input = this frame at this level, Aux = previous frame, Coarser = coarser flow in texels scaled by Params2.x
// (2 from the level above, 1 for a same-level refinement, 0 when unbound). Flow f satisfies this(x) = before(x - f). Warps the previous
// frame by the guess and solves the residual on a 3x3 lattice two texels apart, regularised so flat areas keep the guess.
float4 PSFlowSolve(VsOut i) : SV_Target
{
    float2 texel = SrcTexel;
    float2 guess = Coarser.SampleLevel(LinearClamp, i.Uv, 0).xy * Params2.x;
    float a11 = 0, a12 = 0, a22 = 0, b1 = 0, b2 = 0;
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float2 at = i.Uv + float2(x, y) * 2 * texel;
            float2 back = at - guess * texel;
            float gx = (LevelAt(Aux, back + float2(texel.x, 0)) - LevelAt(Aux, back - float2(texel.x, 0))) * 0.5;
            float gy = (LevelAt(Aux, back + float2(0, texel.y)) - LevelAt(Aux, back - float2(0, texel.y))) * 0.5;
            float gt = LevelAt(Aux, back) - LevelAt(Input, at);
            a11 += gx * gx;
            a12 += gx * gy;
            a22 += gy * gy;
            b1 += gx * gt;
            b2 += gy * gt;
        }
    }

    const float regularised = 2e-3;
    a11 += regularised;
    a22 += regularised;
    float2 step = float2(a22 * b1 - a12 * b2, a11 * b2 - a12 * b1) / (a11 * a22 - a12 * a12);
    return float4(clamp(guess + clamp(step, -3, 3), -256, 256), 0, 1);
}

// 3x3 tent smoothing of the final flow (removes lattice noise on fine detail).
float4 PSFlowSmooth(VsOut i) : SV_Target
{
    float2 sum = float2(0, 0);
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
            sum += SampleAt(i.Uv, float2(x, y)).xy * ((2 - abs(x)) * (2 - abs(y)));
    }

    return float4(sum / 16, 0, 1);
}

// Smears the picture along the flow. Params2 = this picture's sub-rect of the flow frame (u0, v0, u1, v1); Params3.xy = flow texel to uv
// over the shutter, z = max streak (uv), w = sample count. The host runs it twice (the second at one sample's length): N x N taps for
// N + N, so long streaks show no steps. Premultiplied so samples past a shape's edge blend correctly.
float4 PSMotionBlur(VsOut i) : SV_Target
{
    float2 at = lerp(Params2.xy, Params2.zw, i.Uv);
    float2 v = Aux.SampleLevel(LinearClamp, at, 0).xy * Params3.xy;
    float reach = length(v);
    if (reach > Params3.z)
        v *= Params3.z / reach;

    int n = clamp((int)Params3.w, 2, 16);
    float4 sum = float4(0, 0, 0, 0);
    [loop] for (int k = 0; k < n; k++)
        sum += Sample(i.Uv + v * ((k + 0.5) / n - 0.5));
    return sum / n;
}

// Smooth Slow-Mo: picture at phase phi (Params3.z) from Input to the next frame (Aux) along flow t5 (next(x) = first(x - f)).
// Params2 = sub-rect of the flow frame; Params3.xy = flow texel to uv over the frame gap. Samples first at x - phi f and next at
// x + (1 - phi) f, blended by phi; where they disagree (disocclusion or flow miss) the nearer frame wins. Premultiplied in and out.
float4 PSFlowInterpolate(VsOut i) : SV_Target
{
    float2 at = lerp(Params2.xy, Params2.zw, i.Uv);
    float2 f = Coarser.SampleLevel(LinearClamp, at, 0).xy * Params3.xy;
    float phi = Params3.z;
    float4 first = Input.SampleLevel(LinearClamp, i.Uv - phi * f, 0);
    float4 next = Aux.SampleLevel(LinearClamp, i.Uv + (1 - phi) * f, 0);
    float4 d = abs(first - next);
    float disagree = smoothstep(0.08, 0.25, max(max(d.r, d.g), max(d.b, d.a)));
    return lerp(lerp(first, next, phi), phi < 0.5 ? first : next, disagree);
}
