// HandPeg compositor: the magnifier loupe (Pass 5.0-Verify, the user's bug 6). While a slider that shapes a layer's edge is
// held, SceneRenderer.Loupe.cs finds a point of that edge here (PSLoupeFind, on a small picture of the layer alone, its
// answer read back a frame later), draws the layer alone again much larger round it, and lays that in a corner of the
// picture (PSLoupeView) over a checkerboard, so its alpha shows. Compiled at build time (HandPegApp.csproj).

#include "Common.hlsli"

// One pixel: where the layer's edge is nearest Params2.xy (uv of the picture of the layer, its box's top middle), searched
// on a Params2.z x Params2.z grid of that picture: a soft pixel (alpha between 0.1 and 0.9) is on it, and so is the
// crossing of alpha 0.5 between two neighbours with a hard step between them. Out: (u, v, found, 1).
float4 PSLoupeFind(VsOut i) : SV_Target
{
    int n = clamp((int)Params2.z, 8, 96);
    float step = 1.0 / n;
    float2 probe = Params2.xy;
    float best = 1e9;
    float2 at = probe;
    float found = 0;

    [loop]
    for (int y = 0; y < n; y++)
    {
        [loop]
        for (int x = 0; x < n; x++)
        {
            float2 uv = (float2(x, y) + 0.5) * step;
            float a = Input.SampleLevel(LinearClamp, uv, 0).a;

            // A soft pixel of the edge itself.
            if (a > 0.1 && a < 0.9)
            {
                float d = distance(uv, probe);
                if (d < best)
                {
                    best = d;
                    at = uv;
                    found = 1;
                }
            }

            // A hard step to the right or below: the edge is where alpha crosses one half between them.
            float right = x + 1 < n ? Input.SampleLevel(LinearClamp, uv + float2(step, 0), 0).a : a;
            float below = y + 1 < n ? Input.SampleLevel(LinearClamp, uv + float2(0, step), 0).a : a;
            if ((a - 0.5) * (right - 0.5) < 0)
            {
                float2 cross = uv + float2(step * saturate((0.5 - a) / (right - a)), 0);
                float d = distance(cross, probe);
                if (d < best)
                {
                    best = d;
                    at = cross;
                    found = 1;
                }
            }

            if ((a - 0.5) * (below - 0.5) < 0)
            {
                float2 cross = uv + float2(0, step * saturate((0.5 - a) / (below - a)));
                float d = distance(cross, probe);
                if (d < best)
                {
                    best = d;
                    at = cross;
                    found = 1;
                }
            }
        }
    }

    return float4(at, found, 1);
}

// The loupe laid on: its picture (premultiplied) over a checkerboard of Params2.x pixels a cell (two quiet greys, so the
// picture's alpha shows), a frame Params2.y pixels wide in Color (straight RGBA) and faint cross hairs at its middle,
// Params2.w of their strength. Params3.xy: the loupe's size in pixels. Opaque.
float4 PSLoupeView(VsOut i) : SV_Target
{
    float2 size = max(Params3.xy, 1);
    float2 px = i.Local * size;
    float2 cell = floor(px / max(Params2.x, 1));
    float checker = fmod(cell.x + cell.y, 2) < 1 ? 0.19 : 0.27;
    float4 picture = Input.SampleLevel(LinearClamp, i.Uv, 0);
    float3 rgb = picture.rgb + checker * (1 - picture.a);

    // Cross hairs through the middle, short of it (the edge itself stays clear).
    float2 fromMiddle = abs(px - size * 0.5);
    float hair = (fromMiddle.x < 0.5 && fromMiddle.y > 6 && fromMiddle.y < 18) || (fromMiddle.y < 0.5 && fromMiddle.x > 6 && fromMiddle.x < 18) ? Params2.w : 0;
    rgb = lerp(rgb, Color.rgb, hair);

    // The frame round it.
    float2 toEdge = min(px, size - px);
    float frame = saturate(Params2.y - min(toEdge.x, toEdge.y));
    rgb = lerp(rgb, Color.rgb, frame * Color.a);
    return float4(rgb, 1);
}
