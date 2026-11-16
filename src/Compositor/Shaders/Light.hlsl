// Light and lens effects: slot 23 Film Halation & Glow, slot 24 Neon Rim Light, slot 25 Impact Shockwave and Anamorphic Lens, plus their
// shared dual-Kawase pyramid. CPU mirror: tools/GoldenTests/CompositorReference.Fx.cs (change both together). Frosted Glass (slot 24's
// other variant) samples the canvas: PSFrostedGlass in Core.hlsl.
// Conventions: Common.hlsli. Light is added in linear space (FilmGamma) because picture values are gamma-encoded.

#include "Common.hlsli"

static const float FilmGamma = 2.2;

float3 ToLinear(float3 c) { return pow(abs(c), FilmGamma); }
float3 ToFilm(float3 c) { return pow(abs(c), 1 / FilmGamma); }

// ----- Dual-Kawase pyramid -----
//
// Wide, smooth blur from a few small passes: halve (PSKawaseDown), then double back (PSKawaseUp); Params.z = tap offset in input texels.
// Bilinear taps on premultiplied (or linear) values.

float4 PSKawaseDown(VsOut i) : SV_Target
{
    float2 t = SrcTexel * Params.z;
    float4 sum = Sample(i.Uv) * 4;
    sum += Sample(i.Uv - t) + Sample(i.Uv + t);
    sum += Sample(i.Uv + float2(t.x, -t.y)) + Sample(i.Uv - float2(t.x, -t.y));
    return sum / 8;
}

float4 PSKawaseUp(VsOut i) : SV_Target
{
    float2 t = SrcTexel * Params.z;
    float4 sum = Sample(i.Uv + float2(-2 * t.x, 0)) + Sample(i.Uv + float2(2 * t.x, 0));
    sum += Sample(i.Uv + float2(0, -2 * t.y)) + Sample(i.Uv + float2(0, 2 * t.y));
    sum += (Sample(i.Uv + float2(-t.x, t.y)) + Sample(i.Uv + float2(t.x, t.y)) + Sample(i.Uv + float2(t.x, -t.y)) + Sample(i.Uv + float2(-t.x, -t.y))) * 2;
    return sum / 12;
}

// Bloom source at half resolution: linear-light pixels above the threshold (Params2.x, gamma-encoded) with a soft knee. Feeds the glow
// and anamorphic streak pyramids.
float4 PSGlowBright(VsOut i) : SV_Target
{
    float4 c = Unpremul(Sample(i.Uv));
    float3 lin = ToLinear(saturate(c.rgb));
    float peak = max(lin.r, max(lin.g, lin.b));
    float limit = pow(abs(Params2.x), FilmGamma);
    float knee = limit * 0.5 + 1e-4;
    float soft = clamp(peak - limit + knee, 0, 2 * knee);
    soft = soft * soft / (4 * knee);
    float kept = max(soft, peak - limit) / max(peak, 1e-4);
    return float4(lin * kept * c.a, c.a);
}

// Adds linear light to a premultiplied pixel; alpha grows by the light's luma so light beyond the shape shows too.
float4 AddLight(float4 picture, float3 light)
{
    float4 c = Unpremul(picture);
    float3 total = ToLinear(saturate(c.rgb)) * c.a + light;
    float a = saturate(c.a + Luma(light));
    return Premul(ToFilm(saturate(total / max(a, 1e-4))), a);
}

// Film Halation & Glow (slot 23): P0 amount, P1 threshold, P2 spread (host), P3 halation, P4..P6 halation colour, P7 mix (%); Aux = blurred
// highlights. Halation is light reflected off the film base that re-exposes only the red-sensitive layer: the halo keeps the highlight's
// brightness but takes the film's tint.
float4 PSGlowMix(VsOut i) : SV_Target
{
    float4 c = SampleAt(i.Uv, float2(0, 0));
    float3 glow = Aux.SampleLevel(LinearClamp, i.Uv, 0).rgb * P0;
    glow = lerp(glow, Luma(glow) * float3(P4, P5, P6) * 1.5, saturate(P3));
    return lerp(c, AddLight(c, glow), saturate(P7 / 100));
}

// ----- Neon Rim Light (slot 24, variant 0) -----
//
// Jump-flood distance field of the layer's shape (any mask, key or rounded box) in ~log2(reach) passes. Each texel stores the texel offset
// to the nearest inside texel (.xy) and outside texel (.zw); relative offsets keep RGBA16F exact to a fraction of a texel at this reach.
// Input is mapped by UvRect with padding around the shape (border sampler beyond).
static const float FarAway = 20000;

float4 PSJfaSeed(VsOut i) : SV_Target
{
    float a = Input.SampleLevel(LinearBorder, lerp(UvRect.xy, UvRect.zw, i.Uv), 0).a;
    return a >= 0.5 ? float4(0, 0, FarAway, FarAway) : float4(FarAway, FarAway, 0, 0);
}

// One JFA pass with step Params.z texels. Out-of-bounds neighbours are rejected by select, not branch: on tiled mobile GPUs (Adreno)
// a divergent branch per tap cost more than the taps themselves.
float4 PSJfaStep(VsOut i) : SV_Target
{
    float4 best = SampleAt(i.Uv, float2(0, 0));
    float toInside = dot(best.xy, best.xy), toOutside = dot(best.zw, best.zw);
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0)
                continue;

            float2 way = float2(x, y) * Params.z;
            float2 at = i.Uv + way * SrcTexel;
            bool within = all(at >= 0) && all(at <= 1);
            float4 there = Input.SampleLevel(PointClamp, at, 0);
            float2 inside = there.xy + way, outside = there.zw + way;
            float d = within ? dot(inside, inside) : 1e30;
            bool nearer = d < toInside;
            toInside = nearer ? d : toInside;
            best.xy = nearer ? inside : best.xy;

            d = within ? dot(outside, outside) : 1e30;
            nearer = d < toOutside;
            toOutside = nearer ? d : toOutside;
            best.zw = nearer ? outside : best.zw;
        }
    }

    return best;
}

// Rim on the grown picture: Input = layer (via UvRect), Aux = field, Params2.x = field-to-output texel scale, Params2.y = retained picture
// share (Frosted Glass), P0..P2 colour, P3 line width and P4 glow reach in output px (Params.w), P5 strength, P6 inward falloff.
// Light is additive; only the line itself is opaque.
float4 PSRimLight(VsOut i) : SV_Target
{
    float4 c = Input.SampleLevel(LinearBorder, lerp(UvRect.xy, UvRect.zw, i.Uv), 0) * Params2.y;
    float4 field = Aux.SampleLevel(LinearClamp, i.Uv, 0) * Params2.x;
    float outside = length(field.xy), inside = length(field.zw);
    float signedAway = outside > inside ? outside : -inside;

    float half_ = max(P3 * Params.w, 0.5) * 0.5;
    float reach = max(P4 * Params.w, 0.5);
    float line_ = 1 - smoothstep(half_ - 0.75, half_ + 0.75, abs(signedAway));
    float glow = signedAway >= 0 ? exp(-3 * signedAway / reach) : saturate(P6) * exp(-6 * -signedAway / reach);
    float light = (line_ + glow * (1 - line_) * 0.8) * P5;
    return float4(c.rgb + float3(P0, P1, P2) * light, saturate(c.a + saturate(line_ * P5)));
}

// ----- Impact Shockwave (slot 25, variant 0) -----
//
// Expanding refraction ring: P0 progress 0..1, P1/P2 centre (% of picture), P3 ripple in px of a 1080-wide frame, P4 max radius (% of
// height), P5 ring width (% of current radius), P6 camera shake (px, as P3). Per-channel bend adds lens fringing. Deterministic in
// progress and the shake noise.

float ShakeNoise(float n)
{
    n = frac(n * 0.1031);
    n *= n + 33.33;
    n *= n + n;
    return frac(n) * 2 - 1;
}

// Ring radius as a fraction of max: cubic ease-out, like a decelerating pressure front.
float RingOut(float through) { float left = 1 - through; return 1 - left * left * left; }

float4 PSShockwave(VsOut i) : SV_Target
{
    float through = P0;
    if (through <= 0 || through >= 1)
        return SampleAt(i.Uv, float2(0, 0));

    float fading = (1 - through) * (1 - through);
    float aspect = DstSize.x / max(DstSize.y, 1);
    float2 scale = float2(aspect, 1);
    float2 p = (i.Uv - float2(P1, P2) / 100) * scale;
    float d = length(p);
    float radius = max(RingOut(through) * P4 / 100, 1e-3);
    float width = max(radius * P5 / 100, 1e-3);
    float away = (d - radius) / width;
    float ring = exp(-away * away);

    // Push outward ahead of the ring, pull inward behind it.
    float2 toward = d > 1e-5 ? p / d : float2(0, 0);
    float pixels = P3 * Params.x / max(DstSize.y, 1);
    float2 bend = toward * sin(clamp(away, -1.5, 1.5) * 2.0944) * ring * pixels * fading / scale;

    // Camera shake, with the frame zoomed in slightly so no edge shows.
    float2 uv = i.Uv;
    if (P6 > 0)
    {
        float tick = floor(Params4.w * 60);
        float2 jolt = float2(ShakeNoise(tick + 11.3), ShakeNoise(tick * 1.7 + 47.1)) * P6 * Params.x * fading / DstSize;
        float2 margin = P6 * Params.x / DstSize;
        uv = 0.5 + (uv - 0.5) * (1 - 2 * margin) + jolt;
    }

    float4 c;
    c.r = Sample(uv - bend * 1.06).r;
    c.ga = Sample(uv - bend).ga;
    c.b = Sample(uv - bend * 0.94).b;

    // Brighten the ring, as compressed air catches the light.
    c.rgb += ring * fading * 0.18 * c.a * saturate(P3 / 60);
    return c;
}

// ----- Anamorphic Lens (slot 25, variant 1) -----

// One streak pass (quarter height, half width): seven taps along the row Params.z texels apart; the host widens it each pass.
// The row is rotated by Params.y radians (Rotation) with constant on-screen length (Params.w = width / height).
float4 PSStreak(VsOut i) : SV_Target
{
    float s, c;
    sincos(Params.y, s, c);
    float2 step = float2(c, s * Params.w) * SrcTexel.x * Params.z;
    float4 sum = Sample(i.Uv) * 4;
    sum += (Sample(i.Uv - step) + Sample(i.Uv + step)) * 3;
    sum += (Sample(i.Uv - step * 2) + Sample(i.Uv + step * 2)) * 2;
    sum += Sample(i.Uv - step * 3) + Sample(i.Uv + step * 3);
    return sum / 16;
}

// Anamorphic Lens: P0 streak strength, P1 threshold, P2 length (host), P3..P5 streak colour, P6 barrel and colour fringe; Aux = streaks.
float4 PSAnamorphic(VsOut i) : SV_Target
{
    float2 d = i.Uv - 0.5;
    float round_ = dot(d, d);
    float barrel = saturate(P6) * 0.16;
    float bent = (1 + barrel * round_) / (1 + barrel * 0.5);
    float fringe = saturate(P6) * 0.012 * round_;
    float4 c;
    c.r = Sample(0.5 + d * bent * (1 + fringe)).r;
    c.ga = Sample(0.5 + d * bent).ga;
    c.b = Sample(0.5 + d * bent * (1 - fringe)).b;

    float3 streak = Luma(Aux.SampleLevel(LinearClamp, i.Uv, 0).rgb) * float3(P3, P4, P5) * P0;
    return AddLight(c, streak);
}
