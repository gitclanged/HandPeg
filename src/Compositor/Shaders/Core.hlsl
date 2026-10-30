// HandPeg compositor: the core passes (Pass 5.0-Core). Zone 0 (sources: YUV, RGB, solid, pattern), Zone 4 (laying a
// layer on: transform, opacity, transitions, blend modes, shadow), and the final pass to the shared output.
// The effects of zones 1-3 are in Effects.hlsl. Conventions: Common.hlsli.

#include "Common.hlsli"

// ----- Vertex shaders -----

// A triangle covering the target: uv 0..1 across it (effect passes, sources, the final pass).
VsOut VSFull(uint id : SV_VertexID)
{
    VsOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.Position = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = p;
    o.Local = p;
    return o;
}

// A quad (a strip of four) placed by Transform: a layer, its shadow, a blurred background.
VsOut VSQuad(uint id : SV_VertexID)
{
    VsOut o;
    float2 c = float2(id & 1, id >> 1);
    o.Position = mul(float4(c, 0, 1), Transform);
    o.Uv = lerp(UvRect.xy, UvRect.zw, c);
    o.Local = c;
    return o;
}

// ----- High-quality resampling (Zone 0) -----
//
// Catmull-Rom in nine bilinear taps (exact at texel centres, so a picture drawn 1:1 is unchanged); when the source is
// more than 1.5 times larger than the target, a box prefilter over its footprint instead (no aliasing on a 4K picture
// drawn small).

#define CATMULL_ROM(T, tex, uv, size)                                                       \
    {                                                                                       \
        float2 sp = uv * size;                                                              \
        float2 t1 = floor(sp - 0.5) + 0.5;                                                  \
        float2 f = sp - t1;                                                                 \
        float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));                                       \
        float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);                                         \
        float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));                                        \
        float2 w3 = f * f * (-0.5 + 0.5 * f);                                               \
        float2 w12 = w1 + w2;                                                               \
        float2 t12 = (t1 + w2 / max(w12, 1e-5)) / size;                                     \
        float2 t0 = (t1 - 1) / size;                                                        \
        float2 t3 = (t1 + 2) / size;                                                        \
        result = (T)0;                                                                      \
        result += tex.SampleLevel(LinearClamp, float2(t0.x, t0.y), 0) * (w0.x * w0.y);      \
        result += tex.SampleLevel(LinearClamp, float2(t12.x, t0.y), 0) * (w12.x * w0.y);    \
        result += tex.SampleLevel(LinearClamp, float2(t3.x, t0.y), 0) * (w3.x * w0.y);      \
        result += tex.SampleLevel(LinearClamp, float2(t0.x, t12.y), 0) * (w0.x * w12.y);    \
        result += tex.SampleLevel(LinearClamp, float2(t12.x, t12.y), 0) * (w12.x * w12.y);  \
        result += tex.SampleLevel(LinearClamp, float2(t3.x, t12.y), 0) * (w3.x * w12.y);    \
        result += tex.SampleLevel(LinearClamp, float2(t0.x, t3.y), 0) * (w0.x * w3.y);      \
        result += tex.SampleLevel(LinearClamp, float2(t12.x, t3.y), 0) * (w12.x * w3.y);    \
        result += tex.SampleLevel(LinearClamp, float2(t3.x, t3.y), 0) * (w3.x * w3.y);      \
    }

// Box prefilter: n x n bilinear taps spread over the target pixel's footprint (n from 2 to 4).
#define FOOTPRINT(T, tex, uv, size, footprint)                                              \
    {                                                                                       \
        int n = (int)clamp(ceil(max(footprint.x, footprint.y)), 2, 4);                      \
        float2 step = footprint / size / n;                                                 \
        float2 start = uv - step * (n - 1) * 0.5;                                           \
        result = (T)0;                                                                      \
        [unroll(4)] for (int y = 0; y < n; y++)                                             \
            [unroll(4)] for (int x = 0; x < n; x++)                                         \
                result += tex.SampleLevel(LinearClamp, start + step * float2(x, y), 0);     \
        result /= (float)(n * n);                                                           \
    }

float SampleLuma(float2 uv, float2 size, float2 footprint)
{
    float result;
    if (max(footprint.x, footprint.y) > 1.5)
        FOOTPRINT(float, LumaPlane, uv, size, footprint)
    else
        CATMULL_ROM(float, LumaPlane, uv, size)
    return result;
}

float2 SampleChroma(float2 uv, float2 size, float2 footprint)
{
    float2 result;
    if (max(footprint.x, footprint.y) > 1.5)
        FOOTPRINT(float2, ChromaPlane, uv, size, footprint)
    else
        CATMULL_ROM(float2, ChromaPlane, uv, size)
    return result;
}

float4 SampleRgb(float2 uv, float2 size, float2 footprint)
{
    float4 result;
    if (max(footprint.x, footprint.y) > 1.5)
        FOOTPRINT(float4, Input, uv, size, footprint)
    else
        CATMULL_ROM(float4, Input, uv, size)
    return result;
}

// ----- Zone 0: sources, drawn into the layer's working texture -----
//
// UvRect: the part of the source shown (its crop; edges swapped for a flip). Params2.xy the luma plane's size in texels,
// Params2.zw the chroma plane's. Params3.xy the footprint: source texels per target pixel. YuvRow0..2 the matrix.

float4 PSYuvSource(VsOut i) : SV_Target
{
    float2 uv = lerp(UvRect.xy, UvRect.zw, i.Uv);
    float y = SampleLuma(uv, Params2.xy, Params3.xy);
    float2 cbcr = SampleChroma(uv, Params2.zw, Params3.xy * Params2.zw / Params2.xy);
    float3 yuv = float3(y, cbcr);
    float3 rgb = float3(dot(YuvRow0.xyz, yuv) + YuvRow0.w, dot(YuvRow1.xyz, yuv) + YuvRow1.w, dot(YuvRow2.xyz, yuv) + YuvRow2.w);
    return float4(saturate(rgb), 1);
}

// A straight-alpha RGBA picture (a still, a mask shown as a layer): Params2.xy its size.
float4 PSRgbSource(VsOut i) : SV_Target
{
    float2 uv = lerp(UvRect.xy, UvRect.zw, i.Uv);
    float4 c = saturate(SampleRgb(uv, Params2.xy, Params3.xy));
    return Premul(c.rgb, c.a);
}

float4 PSSolid(VsOut i) : SV_Target
{
    return Premul(Color.rgb, Color.a);
}

// Test pictures, computed from the pixel's position alone (the golden tests compute the same on the processor):
// Params2.x the kind: 0 colour bars, 1 gradient ramp, 2 checkerboard, 3 radial target (soft-edged, see-through outside).
float4 PSPattern(VsOut i) : SV_Target
{
    float2 p = i.Position.xy;
    float2 uv = p / DstSize;
    int kind = (int)Params2.x;
    float3 rgb;
    float a = 1;
    if (kind == 0)
    {
        int bar = min((int)(uv.x * 8), 7);
        static const float3 bars[8] = { float3(1, 1, 1), float3(1, 1, 0), float3(0, 1, 1), float3(0, 1, 0),
                                        float3(1, 0, 1), float3(1, 0, 0), float3(0, 0, 1), float3(0, 0, 0) };
        rgb = bars[bar] * (uv.y < 0.75 ? 0.75 : 1.0);
    }
    else if (kind == 1)
    {
        rgb = float3(uv.x, uv.y, 1 - uv.x * 0.5 - uv.y * 0.5);
    }
    else if (kind == 2)
    {
        int2 cell = (int2)(p / 32);
        rgb = ((cell.x + cell.y) & 1) ? float3(0.9, 0.85, 0.8) : float3(0.15, 0.2, 0.25);
    }
    else
    {
        float2 d = uv - 0.5;
        float r = length(d * float2(DstSize.x / DstSize.y, 1));
        float angle = atan2(d.y, d.x) / 6.28318530718 + 0.5;
        rgb = HsvToRgb(float3(angle, 0.8, 0.6 + 0.4 * frac(r * 8)));
        a = saturate((0.45 - r) * 20);
    }
    return Premul(rgb * Color.rgb, a * Color.a);
}

// ----- Zone 4: laying a layer on -----
//
// Input: the layer's working texture (premultiplied). Params4.x its opacity (the layer's, times the TransformComposite
// packet's), Params4.y a dissolve's factor (dissolve, blur dissolve, the start or end of a zoom). A wipe: Params2.x 1,
// Params2.y 1 to keep the low side, Params2.z the axis (0 x, 1 y), Params2.w how much is shown (q); Params3.x the
// feather in pixels, Params3.zw the box's size in pixels. (FilterGraphBuilder.Video.cs AppendPictureFades, the same math.)

float WipeFactor(float2 local)
{
    if (Params2.x < 0.5)
        return 1;
    float size = Params2.z < 0.5 ? Params3.z : Params3.w;
    float along = (Params2.z < 0.5 ? local.x : local.y) * size;
    float feather = max(Params3.x, 1e-3);
    float q = Params2.w;
    return Params2.y > 0.5
        ? saturate(((size + feather) * q - along) / feather)
        : saturate((along - (size + feather) * (1 - q)) / feather + 1);
}

float4 LayerColor(VsOut i)
{
    float4 c = Input.SampleLevel(LinearClamp, i.Uv, 0);
    return c * (Params4.x * Params4.y * WipeFactor(i.Local));
}

float4 PSComposite(VsOut i) : SV_Target
{
    return LayerColor(i);
}

// The blend modes, on straight colours, as FFmpeg's blend filter has them with the picture under as its top input (A)
// and the layer as its bottom (B) (FilterGraphBuilder.Video.cs AppendBlend).
float3 BlendColors(float3 a, float3 b, int mode)
{
    if (mode == 1) return a * b;
    if (mode == 2) return 1 - (1 - a) * (1 - b);
    if (mode == 3) return min(a + b, 1);
    if (mode == 4) return a < 0.5 ? 2 * a * b : 1 - 2 * (1 - a) * (1 - b);
    if (mode == 5) return a > 0.5 ? b + (1 - b) * (a - 0.5) / 0.5 * (0.5 - abs(b - 0.5))
                                  : b - b * ((0.5 - a) / 0.5) * (0.5 - abs(b - 0.5));
    if (mode == 6) return b < 0.5 ? 2 * a * b : 1 - 2 * (1 - a) * (1 - b);
    if (mode == 7) return abs(a - b);
    return b;
}

// A layer with a blend mode: Aux is a copy of the canvas under it (premultiplied). Written over the canvas without
// blending: source-over with the mixed colour where the canvas has picture (W3C compositing with blending).
float4 PSCompositeBlend(VsOut i) : SV_Target
{
    float4 s = LayerColor(i);
    float4 d = Aux.SampleLevel(PointClamp, i.Position.xy / DstSize, 0);
    float4 su = Unpremul(s);
    float4 du = Unpremul(d);
    float3 mixed = saturate(BlendColors(du.rgb, su.rgb, (int)Params4.z));
    float3 rgb = (1 - s.a) * d.rgb + s.a * ((1 - d.a) * su.rgb + d.a * mixed);
    return float4(rgb, s.a + d.a * (1 - s.a));
}

// A drop shadow's start (4.1.1-B): the layer's outline in the shadow's colour at its opacity (Color), on a texture with
// room round it for the blur (UvRect maps this texture onto the layer's, beyond its edges).
float4 PSShadowSeed(VsOut i) : SV_Target
{
    float2 uv = lerp(UvRect.xy, UvRect.zw, i.Uv);
    float a = Input.SampleLevel(LinearBorder, uv, 0).a * Color.a;
    return Premul(Color.rgb, a);
}

// The blurred background's brightness (eq's brightness: added to luma alone, so to every channel alike): Params2.x.
float4 PSDim(VsOut i) : SV_Target
{
    float4 c = Unpremul(Sample(i.Uv));
    return Premul(saturate(c.rgb + Params2.x), c.a);
}

// A plain copy of the input over the part UvRect names (downsizing for the blurred background, the 3D view's planes).
float4 PSCopy(VsOut i) : SV_Target
{
    return Input.SampleLevel(LinearClamp, lerp(UvRect.xy, UvRect.zw, i.Uv), 0);
}

// ----- The output -----
//
// The canvas (premultiplied) over the background colour (Color.rgb), opaque, into the shared BGRA texture. With
// Params4.z = 1 the canvas as it is, premultiplied (the golden tests read its alpha).
float4 PSFinal(VsOut i) : SV_Target
{
    float4 c = Input.SampleLevel(PointClamp, i.Uv, 0);
    if (Params4.z > 0.5)
        return c;
    return float4(saturate(c.rgb + Color.rgb * (1 - c.a)), 1);
}
