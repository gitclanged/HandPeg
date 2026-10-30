// HandPeg compositor: the 3D exploded layer stack (Pass 5.0-AudioAnd3D, 5.0D; SceneRenderer.Exploded.cs). Each layer's
// finished picture (drawn alone, as the flat canvas would have it) is a plane placed in perspective by Transform
// (ExplodedCamera.PlaneMatrix: the quad's corner to clip space, w the depth before the camera), laid back to front over a
// quiet backdrop, with the canvas's frame at the back. Outlines are found from the picture's own alpha. Distances in
// "screen pixels" come from the quad's rate of change across the output (fwidth). Conventions: Common.hlsli.
// Since 5.0-Release a plane's picture is drawn at its source's own resolution into the top left of a texture that other
// planes use too: Params3.xy is how far across and down that picture goes (in the texture's uv); beyond it, as beyond the
// texture, is transparent.

#include "Common.hlsli"

// ----- Vertex shader -----

// A plane's quad, grown by Params.xy (fractions of it) on every side; UvRect maps it onto the picture (beyond the picture
// the taps give transparent black). (Params is the effects' pixel scales elsewhere; no pass of this file reads them.)
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

// How far a point of the quad is from its nearest edge, in screen pixels.
float EdgeDistance(float2 local, float2 rate)
{
    float2 d = min(local, 1 - local) / max(rate, 1e-6);
    return min(d.x, d.y);
}

// The plane's picture at uv: transparent beyond its part of the texture (Params3.xy, and half a texel more for its edge's
// filtering, which the renderer cleared) and beyond the texture (the border sampler).
float4 Tap(float2 uv)
{
    float4 c = Input.SampleLevel(LinearBorder, uv, 0);
    return any(uv > Params3.xy + 0.5 * SrcTexel) ? float4(0, 0, 0, 0) : c;
}

// How many of the picture's texels a screen pixel covers (the larger way).
float Footprint(float2 rate)
{
    float2 footprint = rate / max(SrcTexel, 1e-6);
    return max(footprint.x, footprint.y);
}

// The plane's picture at uv: one tap while it is drawn about its own size or larger; drawn smaller (seen far off, at a
// slant, or at its full resolution from the default distance), the mean over the pixel's footprint: four taps up to three
// texels a pixel, sixteen beyond (no shimmer).
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

// The picture's alpha at uv, over the pixel's footprint when it is drawn smaller than its own size (an edge a few texels
// wide at its full resolution then reads as it does on screen, frame after frame).
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

// Behind everything: a quiet vertical gradient from Color (top) to Params3 (bottom), darker towards the corners. Opaque.
float4 PSExplodedBackdrop(VsOut i) : SV_Target
{
    float3 rgb = lerp(Color.rgb, Params3.rgb, i.Uv.y);
    float2 d = i.Uv - 0.5;
    return float4(rgb * (1 - 0.6 * dot(d, d)), 1);
}

// A layer's plane (the canvas at its depth): its picture (premultiplied) at Params4.x where it has one, and over it a
// hairline round the plane's edge in Color (straight RGBA), Params2.x pixels wide, so the plane shows where its picture is
// see-through. Where the pixel's footprint is wholly outside the picture nothing is read.
float4 PSExplodedPlane(VsOut i) : SV_Target
{
    float2 rate = fwidth(i.Uv);
    float2 localRate = fwidth(i.Local);
    float4 c = float4(0, 0, 0, 0);
    if (all(i.Uv > -rate) && all(i.Uv < Params3.xy + rate))
        c = SamplePlane(i.Uv, rate) * Params4.x;
    float stroke = saturate(Params2.x - EdgeDistance(i.Local, localRate)) * Color.a;
    return float4(Color.rgb * stroke + c.rgb * (1 - stroke), stroke + c.a * (1 - stroke));
}

// The neon outline round a plane's silhouette: where its picture's alpha changes (a mask's or a key's edge, the box's
// own edges), a thin core Params2.x pixels out and in and a soft glow Params2.y pixels out, in Color (straight RGB, its
// alpha the strength). Drawn on the picture's part of the plane grown for the glow, over the plane.
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

// The canvas's frame at the back of the stack, and an adjustment layer's glass: a fill (Color, straight RGBA), an edge
// line Params2.x pixels wide and the frame's thirds at Params2.y of its strength, both in Params3 (straight RGBA).
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
