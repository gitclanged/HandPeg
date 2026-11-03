// The Media pane's hover tilt (5.1-GpuFx, TileTiltEffect.cs): one tile, the one under the pointer, seen in true perspective. WPF has no 3D for a 2D element, and a Viewport3D per
// tile would cost the memory the pane is built not to spend; this is a homography in a pixel shader, on one element at a time. ps_2_0, the profile WPF can always run.
// c0: x, y the tilt toward the pointer; z the sheen's strength; w how much smaller it is drawn, to leave room for its near edge. c1: xy where the sheen is.

sampler2D Input : register(s0);
float4 Tilt : register(c0);
float4 Sheen : register(c1);

float4 main(float2 uv : TEXCOORD0) : COLOR
{
    float2 p = uv * 2 - 1;
    float2 q = p / ((1 - Tilt.x * p.x - Tilt.y * p.y) * Tilt.w);
    float2 edge = saturate((1 - abs(q)) * 80);
    float4 c = tex2D(Input, q * 0.5 + 0.5) * (edge.x * edge.y);
    float2 d = uv - Sheen.xy;
    c.rgb += c.a * Tilt.z * saturate(1 - dot(d, d) * 5);
    return c;
}
