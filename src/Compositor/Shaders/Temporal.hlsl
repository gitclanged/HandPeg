// HandPeg compositor: effects that look back over frames (Pass 5.0-AudioAnd3D, the user's answer Q3).
//
// Matte Smooth, as FFmpeg's tmix does it: the transparency averaged over the last N frames (Hann weights, the current
// frame the newest), the colour the current frame's. The host keeps each smoothed layer's last frames' alpha in R8
// textures (PSAlphaOf writes one) and binds up to eight of them at t5..t12, oldest first, while the player plays or
// steps in order; a jump falls back to the spatial pass (PSMatteSmooth in Effects.hlsl).

#include "Common.hlsli"

Texture2D<float> History0 : register(t5);
Texture2D<float> History1 : register(t6);
Texture2D<float> History2 : register(t7);
Texture2D<float> History3 : register(t8);
Texture2D<float> History4 : register(t9);
Texture2D<float> History5 : register(t10);
Texture2D<float> History6 : register(t11);
Texture2D<float> History7 : register(t12);

// The input's transparency, into an R8 target (one frame of a layer's history).
float4 PSAlphaOf(VsOut input) : SV_Target
{
    return Sample(input.Uv).aaaa;
}

// Params2: the weights of History0..3; Params3: of History4..7 (0 for those not bound); Color.x: the current frame's
// weight; Color.y: the sum of every weight used.
float4 PSMatteTemporal(VsOut input) : SV_Target
{
    float4 c = Sample(input.Uv);
    float2 uv = input.Uv;
    float a = c.a * Color.x;
    a += History0.SampleLevel(LinearClamp, uv, 0) * Params2.x;
    a += History1.SampleLevel(LinearClamp, uv, 0) * Params2.y;
    a += History2.SampleLevel(LinearClamp, uv, 0) * Params2.z;
    a += History3.SampleLevel(LinearClamp, uv, 0) * Params2.w;
    a += History4.SampleLevel(LinearClamp, uv, 0) * Params3.x;
    a += History5.SampleLevel(LinearClamp, uv, 0) * Params3.y;
    a += History6.SampleLevel(LinearClamp, uv, 0) * Params3.z;
    a += History7.SampleLevel(LinearClamp, uv, 0) * Params3.w;
    a = saturate(a / max(Color.y, 1e-6));

    // Where the current frame shows nothing its colour is lost (premultiplied): the edge's colour is borrowed from the
    // nearest shown pixels round it, one ring and then two out (FFmpeg keeps the keyed-out picture's own colour there).
    float4 s = Unpremul(c);
    if (c.a <= 1e-5 && a > 1e-4)
    {
        float4 nearSum = 0;
        float4 farSum = 0;
        [unroll] for (int y = -1; y <= 1; y++)
        {
            [unroll] for (int x = -1; x <= 1; x++)
            {
                nearSum += SampleAt(uv, float2(x, y));
                farSum += SampleAt(uv, float2(x, y) * 2);
            }
        }

        // Premultiplied sums: their colour over their alpha is the alpha-weighted straight colour round the pixel.
        float4 sum = nearSum.a > 1e-4 ? nearSum : farSum;
        s.rgb = sum.a > 1e-4 ? sum.rgb / sum.a : 0;
        if (sum.a <= 1e-4)
            a = 0;
    }

    return Premul(s.rgb, a);
}
