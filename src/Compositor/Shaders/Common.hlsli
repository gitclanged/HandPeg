// HandPeg compositor: what every shader shares (Pass 5.0-Core). Mirrored in C# by Compositor/PassConstants.cs: change
// both together. Compiled at build time (HandPegApp.csproj, CompileShaders) for shader model 5.0.
//
// Conventions
// - Every layer texture is RGBA16F with PREMULTIPLIED alpha, in the source's own gamma-encoded RGB (BT.709 / sRGB
//   values, as FFmpeg's filters see them): no linearisation, so the looks match the FFmpeg graph.
// - A colour effect unpremultiplies (Unpremul), works on straight RGB, and premultiplies again (Premul).
// - Pixel distances: a lane "in px of a 1080-wide frame" is multiplied by PxScale1080 (Params.x); a lane "in output
//   pixels" by PixelScale (Params.w). Both already give texels of the layer's working texture.

#ifndef HANDPEG_COMMON_HLSLI
#define HANDPEG_COMMON_HLSLI

cbuffer PassConstants : register(b0)
{
    // The effect's packet (GpuEffectPacket: the slot and eight lanes; 36 bytes, c0..c2.x).
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

    // A second packet in a row with the same slot (Color Balance's #1), at c3..c5.x.
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

    // c6: the input texture's texel size (1/width, 1/height) and the target's size in pixels.
    float2 SrcTexel;
    float2 DstSize;

    // c7: the part of the input sampled, in uv (u0, v0, u1, v1).
    float4 UvRect;

    // c8..c11: where the quad lands (VSQuad): corner (0..1, 0..1, 0, 1) times this, row vector.
    row_major float4x4 Transform;

    // c12: a colour (straight RGBA): solid layers, shadows, pattern tints.
    float4 Color;

    // c13: x PxScale1080 (texels per px of a 1080-wide frame), y the pass's direction (0 across, 1 down),
    //      z a radius in texels set by the host for separable passes, w PixelScale (texels per output pixel).
    float4 Params;

    // c14..c15: extras, by shader (documented where used).
    float4 Params2;
    float4 Params3;

    // c16..c18: YUV to RGB: rgb = float3(dot(Row0.xyz, yuv), dot(Row1.xyz, yuv), dot(Row2.xyz, yuv)) + (Row0.w, Row1.w, Row2.w).
    float4 YuvRow0;
    float4 YuvRow1;
    float4 YuvRow2;

    // c19: x the layer's opacity, y the transition alpha, z 1 when the layer is the composite's input, w the time (s).
    float4 Params4;
};

// t0 the pass's input (premultiplied RGBA). t1 an auxiliary 2D texture: the mask, the curve (256 x 1), the canvas under
// a blended layer. t2 the 3D LUT. t3, t4 a YUV source's luma and chroma planes.
Texture2D<float4> Input : register(t0);
Texture2D<float4> Aux : register(t1);
Texture3D<float4> Lut : register(t2);
Texture2D<float>  LumaPlane : register(t3);
Texture2D<float2> ChromaPlane : register(t4);

SamplerState LinearClamp : register(s0);
SamplerState PointClamp : register(s1);
// Outside the texture: transparent black.
SamplerState LinearBorder : register(s2);

struct VsOut
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
    // The quad's own coordinates, 0..1 across and down (the layer-local position used by wipes and edges).
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

// The input at uv (the pass's own pixel, bilinear for in-between).
float4 Sample(float2 uv) { return Input.SampleLevel(LinearClamp, uv, 0); }

// The input one texel away (offset in texels).
float4 SampleAt(float2 uv, float2 texels) { return Input.SampleLevel(PointClamp, uv + texels * SrcTexel, 0); }

#endif
