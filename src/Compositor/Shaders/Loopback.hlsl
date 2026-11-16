// Export Quality Preview compare pass (ExportLoopbackPreview).
//
// Compares the encoder's decoded output with the drawn frame, both NV12 (PSRgbToNv12, so an untouched frame diffs to zero):
// t0 source Y (R8), t1 source UV (R8G8), t3/t4 decoded Y/UV (two views of one NV12 texture, or two textures). Full-output pass
// (VSFull) placed by the letterbox; black outside the picture.
//
// Params:  x mode (0 encoded, 1 split wipe, 2 difference), y wipe position (0..1 of picture width), z difference gain,
//          w half divider width in output px; w < 0 point-samples the planes (loupe view: encoded or difference, never the wipe).
// Params2: picture rectangle on the output in px (left, top, width, height).
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

    // Difference x gain: luma error as grey, Cb/Cr errors added to blue/red (0 is black).
    if (Params.x > 1.5)
    {
        float3 d = abs(source - encoded) * Params.z;
        return float4(saturate(float3(d.x + d.z, d.x, d.x + d.y)), 1);
    }

    // Split wipe: source left of the divider, encoded right, light divider line.
    if (Params.x > 0.5)
    {
        float divider = Params2.x + Params.y * Params2.z;
        if (abs(i.Position.x - divider) < abs(Params.w))
            return float4(0.95, 0.95, 0.95, 1);
        return float4(LoopbackRgb(i.Position.x < divider ? source : encoded), 1);
    }

    return float4(LoopbackRgb(encoded), 1);
}
