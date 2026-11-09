// HandPeg compositor: the Export Quality Preview's compare pass (Pass 5.4B, ExportLoopbackPreview).
//
// The frame as the export's encoder leaves it, against the frame as it was drawn, both as NV12 (the source's own 4:2:0 conversion,
// PSRgbToNv12, so an untouched frame compares to exactly nothing): t0 the source's Y (R8), t1 its UV (R8G8), t3 the decoded Y and t4
// its UV (one NV12 texture's two views, or two plain textures). Drawn over the whole output (VSFull) and placed by the letterbox:
// outside the picture the player is black.
//
// Params:  x what is shown (0 the encoded picture, 1 a split wipe, 2 the difference), y the wipe's place (0..1 of the picture's width),
//          z how much the difference is magnified, w half the divider's width in output pixels; negative (5.4C, the loupe's view over
//          the compare: the encoded picture or the difference, never the wipe) the planes are point sampled, so each encoded pixel,
//          and each 2 x 2 of its chroma, shows as the square it is.
// Params2: the picture's rectangle on the output, in pixels (left, top, width, height).
// YuvRow0-2: BT.709 limited range to RGB (SceneRenderer.SetYuv).

#include "Common.hlsli"

float3 LoopbackRgb(float3 yuv)
{
    return saturate(float3(dot(YuvRow0.xyz, yuv) + YuvRow0.w, dot(YuvRow1.xyz, yuv) + YuvRow1.w, dot(YuvRow2.xyz, yuv) + YuvRow2.w));
}

float4 PSLoopbackCompare(VsOut i) : SV_Target
{
    float2 uv = (i.Position.xy - Params2.xy) / max(Params2.zw, float2(1, 1));
    if (any(uv < 0) || any(uv > 1))
        return float4(0, 0, 0, 1);

    float3 source, encoded;
    if (Params.w < 0)
    {
        source = float3(Input.SampleLevel(PointClamp, uv, 0).r, Aux.SampleLevel(PointClamp, uv, 0).rg);
        encoded = float3(LumaPlane.SampleLevel(PointClamp, uv, 0), ChromaPlane.SampleLevel(PointClamp, uv, 0));
    }
    else
    {
        source = float3(Input.SampleLevel(LinearClamp, uv, 0).r, Aux.SampleLevel(LinearClamp, uv, 0).rg);
        encoded = float3(LumaPlane.SampleLevel(LinearClamp, uv, 0), ChromaPlane.SampleLevel(LinearClamp, uv, 0));
    }

    // The difference x the gain: luma in grey, the blue and red chroma's errors added to their own colours (0 is black).
    if (Params.x > 1.5)
    {
        float3 d = abs(source - encoded) * Params.z;
        return float4(saturate(float3(d.x + d.z, d.x, d.x + d.y)), 1);
    }

    // The split wipe: the source left of the line, the encoded picture right of it, and a light divider between.
    if (Params.x > 0.5)
    {
        float divider = Params2.x + Params.y * Params2.z;
        if (abs(i.Position.x - divider) < abs(Params.w))
            return float4(0.95, 0.95, 0.95, 1);
        return float4(LoopbackRgb(i.Position.x < divider ? source : encoded), 1);
    }

    return float4(LoopbackRgb(encoded), 1);
}
