// 3D exploded layer stack (SceneRenderer.Exploded.cs). Each layer's isolated render is a plane placed in perspective by Transform
// (ExplodedCamera.PlaneMatrix: quad corner to clip space, w = depth before the camera), drawn back to front over a backdrop with
// the canvas frame at the back. Outlines come from the picture's alpha; "screen pixel" distances from fwidth. Conventions: Common.hlsli.
// Plane pictures render at source resolution into the top-left of a shared texture: Params3.xy is their extent in the texture's uv;
// beyond it, and beyond the texture, is transparent.

#include "Common.hlsli"

// ----- Vertex shader -----

// Plane quad grown by Params.xy (fraction of its size) on every side; UvRect maps it onto the picture (transparent beyond).
// (Params holds effect pixel scales elsewhere; no pass in this file reads those.)
VsOut VSExploded(uint id : SV_VertexID)
{
    VsOut o;
    float2 c = float2(id & 1, id >> 1);
    float2 grown = c * (1 + 2 * Params.xy) - Params.xy;
    o.Position = mul(float4(grown, 0, 1), Transform);
    o.Uv = lerp(UvRect.xy, UvRect.zw, grown);
    o.Local = grown;
    return o;
}

// ----- Helpers -----

// Distance from a quad point to its nearest edge, in screen pixels.
float EdgeDistance(float2 local, float2 rate)
{
    float2 d = min(local, 1 - local) / max(rate, 1e-6);
    return min(d.x, d.y);
}

// Plane picture at uv: transparent beyond its sub-rect (Params3.xy plus half a texel of edge filtering, cleared by the renderer)
// and beyond the texture (border sampler).
float4 Tap(float2 uv)
{
    float4 c = Input.SampleLevel(LinearBorder, uv, 0);
    return any(uv > Params3.xy + 0.5 * SrcTexel) ? float4(0, 0, 0, 0) : c;
}

// Texels covered per screen pixel (larger axis).
float Footprint(float2 rate)
{
    float2 footprint = rate / max(SrcTexel, 1e-6);
    return max(footprint.x, footprint.y);
}

// Plane picture at uv: one tap at or above native size; when minified (distance, slant), the mean over the pixel footprint
// (4 taps up to 3 texels per pixel, 16 beyond) to prevent shimmer.
float4 SamplePlane(float2 uv, float2 rate)
{
    float most = Footprint(rate);
    float4 result = float4(0, 0, 0, 0);
    if (most <= 1.5)
    {
        result = Tap(uv);
    }
    else if (most <= 3)
    {
        float2 quarter = rate * 0.25;
        result = 0.25 * (Tap(uv + float2(-quarter.x, -quarter.y)) + Tap(uv + float2(quarter.x, -quarter.y))
                       + Tap(uv + float2(-quarter.x, quarter.y)) + Tap(uv + float2(quarter.x, quarter.y)));
    }
    else
    {
        [unroll] for (int y = 0; y < 4; y++)
        {
            [unroll] for (int x = 0; x < 4; x++)
                result += Tap(uv + (float2(x, y) - 1.5) * 0.25 * rate);
        }
        result /= 16;
    }
    return result;
}

// Picture alpha at uv, footprint-averaged when minified so thin edges stay stable frame to frame.
float AlphaAt(float2 uv, float2 rate)
{
    float alpha = 0;
    if (Footprint(rate) <= 1.5)
    {
        alpha = Tap(uv).a;
    }
    else
    {
        float2 quarter = rate * 0.25;
        alpha = 0.25 * (Tap(uv + float2(-quarter.x, -quarter.y)).a + Tap(uv + float2(quarter.x, -quarter.y)).a
                      + Tap(uv + float2(-quarter.x, quarter.y)).a + Tap(uv + float2(quarter.x, quarter.y)).a);
    }
    return alpha;
}

// ----- Pixel shaders -----

// Backdrop: vertical gradient from Color (top) to Params3 (bottom) with a corner vignette. Opaque.
float4 PSExplodedBackdrop(VsOut i) : SV_Target
{
    float3 rgb = lerp(Color.rgb, Params3.rgb, i.Uv.y);
    float2 d = i.Uv - 0.5;
    return float4(rgb * (1 - 0.6 * dot(d, d)), 1);
}

// Hairline quad (depth rails): Color, solid along the centre line, fading out at both ends (Local.x).
float4 PSExplodedLine(VsOut i) : SV_Target
{
    float across = 1 - abs(i.Local.y * 2 - 1);
    float ends = saturate(min(i.Local.x, 1 - i.Local.x) * 6);
    return Premul(Color.rgb, Color.a * saturate(across * 1.5) * ends);
}

// Layer plane: premultiplied picture at opacity Params4.x plus a Params2.x px edge hairline in Color (straight RGBA) so
// transparent regions still show the plane. Skips sampling when the footprint is wholly outside the picture.
float4 PSExplodedPlane(VsOut i) : SV_Target
{
    float2 rate = fwidth(i.Uv);
    float2 localRate = fwidth(i.Local);
    float4 c = float4(0, 0, 0, 0);
    if (all(i.Uv > -rate) && all(i.Uv < Params3.xy + rate))
        c = SamplePlane(i.Uv, rate) * Params4.x;
    float edge = EdgeDistance(i.Local, localRate);
    float stroke = saturate(Params2.x - edge) * Color.a;
    return float4(Color.rgb * stroke + c.rgb * (1 - stroke), stroke + c.a * (1 - stroke));
}

// Neon silhouette outline where the picture's alpha changes (mask/key edges, box edges): a core Params2.x px in and out and a
// Params2.y px outer glow, in Color (straight RGB, alpha = strength). Drawn over the plane on the glow-grown quad.
float4 PSExplodedOutline(VsOut i) : SV_Target
{
    float2 rate = fwidth(i.Uv);
    float here = AlphaAt(i.Uv, rate);
    float coreHigh = here, coreLow = here, glowHigh = here, glowLow = here;
    [unroll] for (int k = 0; k < 8; k++)
    {
        float angle = k * 0.785398163;
        float2 toward = float2(cos(angle), sin(angle)) * rate;
        float inner = AlphaAt(i.Uv + toward * Params2.x, rate);
        float outer = Tap(i.Uv + toward * Params2.y).a;
        coreHigh = max(coreHigh, inner);
        coreLow = min(coreLow, inner);
        glowHigh = max(glowHigh, outer);
        glowLow = min(glowLow, outer);
    }

    float core = saturate((coreHigh - coreLow) * 2);
    float glow = saturate(glowHigh - glowLow) * 0.4;
    float strength = max(core, glow) * Color.a;
    return float4(Color.rgb * strength, strength);
}

// Canvas frame at the back of the stack, and adjustment-layer glass: fill Color (straight RGBA), a Params2.x px edge line and a
// rule-of-thirds grid at Params2.y strength, both in Params3 (straight RGBA).
float4 PSExplodedFrame(VsOut i) : SV_Target
{
    float2 rate = max(fwidth(i.Local), 1e-6);
    float edge = saturate(Params2.x - EdgeDistance(i.Local, rate));
    float2 thirds = abs(frac(i.Local * 3 + 0.5) - 0.5) / (rate * 3);
    float grid = saturate(1 - min(thirds.x, thirds.y)) * Params2.y;
    float stroke = max(edge, grid) * Params3.a;
    float4 fill = Premul(Color.rgb, Color.a);
    return float4(Params3.rgb * stroke + fill.rgb * (1 - stroke), stroke + fill.a * (1 - stroke));
}
