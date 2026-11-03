// HandPeg compositor: the Motion Blur card (slot 22, Pass 5.1-GpuFx): a real shutter, with no plugin's guesswork about what moved.
//
// Two kinds of motion, each measured where it is known:
// - What moves inside the picture (a whip-turn, a speed ramp): optical flow between the frame shown and the one before it, both read from
//   the FrameCache (no demand is made for it, so it can never make a frame late). A pyramid of Lucas-Kanade solves at a quarter of the frame's
//   size and below (PSFlowSolve, coarsest first, each level warping by the level above), smoothed once (PSFlowSmooth), kept per frame of a source
//   (SceneRenderer.Fx.cs). An adjustment layer or the Sequence Output has no file: it is measured between the canvas under it and the last one.
// - What the layer itself does (a move, a zoom, a turn): sub-frames of the laid-on picture, summed along the way from the shutter's opening to its
//   closing (SceneRenderer.Fx.cs Shutter, the plan's lanes), which is exact: nothing is estimated.
// Conventions: Common.hlsli.

#include "Common.hlsli"

// The flow of the level above (texels of that level): t5.
Texture2D<float4> Coarser : register(t5);

float LevelAt(Texture2D<float4> level, float2 uv) { return dot(level.SampleLevel(LinearClamp, uv, 0).rgb, Luma709); }

// One Lucas-Kanade solve. Input this frame at this level, Aux the frame before, Coarser the level above's flow, in texels, times Params2.x (2 from
// the level above; 1 for a second solve at one level; nothing bound at the top: 0). f is the flow of the picture: this(x) = before(x - f). The frame
// before is warped by the guess and what is left of the motion solved over a lattice two texels apart, regularised, so a flat wall keeps the guess.
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

// The finished flow smoothed once (a 3 x 3 tent): the solve's lattice leaves a little noise on fine detail.
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

// The picture smeared along the flow: Params2 the part of the flow's frame this picture is (u0, v0, u1, v1); Params3.xy what a texel of flow comes to
// in this picture's uv across the shutter, Params3.z the longest streak (uv), Params3.w the samples. Run twice by the host, the second with a sample's
// length: N x N samples for the price of N + N, so even a long streak has no steps. Premultiplied, so what lies beyond a shape's edge blends right.
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
