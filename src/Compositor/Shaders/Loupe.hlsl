// Edge magnifier loupe. While an edge-shaping slider is held, SceneRenderer.Loupe.cs locates a point on the layer's edge
// (PSLoupeFind on a small isolated render, read back a frame later), re-renders the layer magnified around it and composites
// that into a corner over a checkerboard (PSLoupeView) so alpha is visible. Compiled at build time (HandPegApp.csproj).

#include "Common.hlsli"

// Single-pixel output: the edge point nearest Params2.xy (uv in the isolated layer, box top-centre), searched on a
// Params2.z x Params2.z grid. Edge = soft pixel (0.1 < alpha < 0.9) or the alpha-0.5 crossing between hard-stepped
// neighbours. Out: (u, v, found, 1).
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

            // Soft edge pixel.
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

            // Hard step to the right or below: the edge lies where alpha crosses 0.5.
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

// Composites the loupe: premultiplied picture over a two-grey checkerboard (Params2.x px cells), a Params2.y px frame in Color
// (straight RGBA) and centre cross hairs at Params2.w strength. Params3.xy = loupe size in px. Opaque output.
float4 PSLoupeView(VsOut i) : SV_Target
{
    float2 size = max(Params3.xy, 1);
    float2 px = i.Local * size;
    float2 cell = floor(px / max(Params2.x, 1));
    float checker = fmod(cell.x + cell.y, 2) < 1 ? 0.19 : 0.27;
    float4 picture = Input.SampleLevel(LinearClamp, i.Uv, 0);
    float3 rgb = picture.rgb + checker * (1 - picture.a);

    // Cross hairs stop short of the centre so the edge stays visible.
    float2 fromMiddle = abs(px - size * 0.5);
    float hair = (fromMiddle.x < 0.5 && fromMiddle.y > 6 && fromMiddle.y < 18) || (fromMiddle.y < 0.5 && fromMiddle.x > 6 && fromMiddle.x < 18) ? Params2.w : 0;
    rgb = lerp(rgb, Color.rgb, hair);

    // Border frame.
    float2 toEdge = min(px, size - px);
    float frame = saturate(Params2.y - min(toEdge.x, toEdge.y));
    rgb = lerp(rgb, Color.rgb, frame * Color.a);
    return float4(rgb, 1);
}
