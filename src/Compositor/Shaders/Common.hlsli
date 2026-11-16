// Shared shader declarations. Mirrored in C# by Compositor/PassConstants.cs (change both together). Compiled at build time
// (HandPegApp.csproj, CompileShaders) for shader model 5.0.
//
// Conventions
// - Layer textures are RGBA16F with PREMULTIPLIED alpha in the source's gamma-encoded RGB (BT.709/sRGB values, as FFmpeg
//   filters see them): no linearisation, so looks match the FFmpeg graph.
// - Colour effects unpremultiply (Unpremul), work on straight RGB, then premultiply (Premul).
// - Pixel distances: lanes "in px of a 1080-wide frame" scale by PxScale1080 (Params.x), lanes "in output pixels" by
//   PixelScale (Params.w); both yield texels of the layer's working texture.

#ifndef HANDPEG_COMMON_HLSLI
#define HANDPEG_COMMON_HLSLI

cbuffer PassConstants : register(b0)
{
    // Effect packet (GpuEffectPacket: slot + eight lanes; 36 bytes, c0..c2.x).
    uint  Slot;
    float P0;
    float P1;
    float P2;
    float P3;
    float P4;
    float P5;
    float P6;
    float P7;
    float3 Pad0;

    // Second consecutive packet of the same slot (e.g. Color Balance #1), c3..c5.x.
    uint  Slot1;
    float Q0;
    float Q1;
    float Q2;
    float Q3;
    float Q4;
    float Q5;
    float Q6;
    float Q7;
    float3 Pad1;

    // c6: input texel size (1/width, 1/height) and target size in pixels.
    float2 SrcTexel;
    float2 DstSize;

    // c7: sampled input sub-rect in uv (u0, v0, u1, v1).
    float4 UvRect;

    // c8..c11: quad placement (VSQuad): corner (0..1, 0..1, 0, 1) times this, row vector.
    row_major float4x4 Transform;

    // c12: straight RGBA colour (solid layers, shadows, pattern tints).
    float4 Color;

    // c13: x PxScale1080 (texels per px of a 1080-wide frame), y pass direction (0 horizontal, 1 vertical),
    //      z host-set radius in texels for separable passes, w PixelScale (texels per output pixel).
    float4 Params;

    // c14..c15: per-shader extras (documented at each use).
    float4 Params2;
    float4 Params3;

    // c16..c18: YUV to RGB: rgb = float3(dot(Row0.xyz, yuv), dot(Row1.xyz, yuv), dot(Row2.xyz, yuv)) + (Row0.w, Row1.w, Row2.w).
    float4 YuvRow0;
    float4 YuvRow1;
    float4 YuvRow2;

    // c19: x layer opacity, y transition alpha, z 1 when the layer is the composite's input, w time (s).
    float4 Params4;
};

// t0 pass input (premultiplied RGBA); t1 auxiliary 2D (mask, 256x1 curve, canvas beneath a blended layer); t2 3D LUT;
// t3/t4 YUV luma and chroma planes.
Texture2D<float4> Input : register(t0);
Texture2D<float4> Aux : register(t1);
Texture3D<float4> Lut : register(t2);
Texture2D<float>  LumaPlane : register(t3);
Texture2D<float2> ChromaPlane : register(t4);

SamplerState LinearClamp : register(s0);
SamplerState PointClamp : register(s1);
// Transparent black outside the texture.
SamplerState LinearBorder : register(s2);
// Wrapping sampler for the baked 3D noise.
SamplerState LinearWrap : register(s3);

struct VsOut
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
    // Quad-local coordinates 0..1 (layer-local position for wipes and edges).
    float2 Local : TEXCOORD1;
};

static const float3 Luma709 = float3(0.2126, 0.7152, 0.0722);

float Luma(float3 rgb) { return dot(rgb, Luma709); }

float4 Unpremul(float4 c) { return c.a > 1e-5 ? float4(c.rgb / c.a, c.a) : float4(0, 0, 0, 0); }

float4 Premul(float3 rgb, float a) { return float4(rgb * a, a); }

float3 RgbToHsv(float3 c)
{
    float4 k = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = c.g < c.b ? float4(c.bg, k.wz) : float4(c.gb, k.xy);
    float4 q = c.r < p.x ? float4(p.xyw, c.r) : float4(c.r, p.yzx);
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 HsvToRgb(float3 c)
{
    float3 p = abs(frac(c.xxx + float3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
    return c.z * lerp(float3(1, 1, 1), saturate(p - 1.0), c.y);
}

// Bilinear input sample at uv.
float4 Sample(float2 uv) { return Input.SampleLevel(LinearClamp, uv, 0); }

// Point sample offset by whole texels.
float4 SampleAt(float2 uv, float2 texels) { return Input.SampleLevel(PointClamp, uv + texels * SrcTexel, 0); }

#endif
