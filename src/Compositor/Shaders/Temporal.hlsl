// Temporal effects.
//
// Matte Smooth (tmix-style): alpha averaged over the last N frames with Hann weights, colour from the current frame. The host keeps
// each layer's alpha history in R8 textures (PSAlphaOf) and binds up to eight at t5..t12, oldest first, during sequential playback;
// a jump falls back to the spatial PSMatteSmooth (Effects.hlsl).

#include "Common.hlsli"

Texture2D<float> History0 : register(t5);
Texture2D<float> History1 : register(t6);
Texture2D<float> History2 : register(t7);
Texture2D<float> History3 : register(t8);
Texture2D<float> History4 : register(t9);
Texture2D<float> History5 : register(t10);
Texture2D<float> History6 : register(t11);
Texture2D<float> History7 : register(t12);

// Input alpha into an R8 target (one history frame).
float4 PSAlphaOf(VsOut input) : SV_Target
{
    return Sample(input.Uv).aaaa;
}

// Params2/Params3: weights of History0..3 / History4..7 (0 when unbound); Color.x current-frame weight; Color.y weight sum.
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

    // Fully transparent pixels lost their colour (premultiplied): borrow it from the 3x3 ring, else the 5x5 ring
    // (tmix keeps the keyed-out colour there).
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

        // Premultiplied sums: rgb / a is the alpha-weighted straight colour.
        float4 sum = nearSum.a > 1e-4 ? nearSum : farSum;
        s.rgb = sum.a > 1e-4 ? sum.rgb / sum.a : 0;
        if (sum.a <= 1e-4)
            a = 0;
    }

    return Premul(s.rgb, a);
}
