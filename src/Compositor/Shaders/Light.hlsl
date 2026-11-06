// HandPeg compositor: light and lenses (Pass 5.1-GpuFx): slot 23 Film Halation & Glow, slot 24 the Neon Rim Light, slot 25 the Impact Shockwave and the
// Anamorphic Lens, and the dual-Kawase pyramid they share. Mirrored on the processor by tools/GoldenTests/CompositorReference.Fx.cs: change both together.
// (Frosted Glass, the other half of slot 24, reads the canvas where the layer lands: PSFrostedGlass in Core.hlsl.)
// Conventions: Common.hlsli. Light is added in linear light (FilmGamma), as a lens and a film stock add it: the picture's values are gamma-encoded.

#include "Common.hlsli"

static const float FilmGamma = 2.2;

float3 ToLinear(float3 c) { return pow(abs(c), FilmGamma); }
float3 ToFilm(float3 c) { return pow(abs(c), 1 / FilmGamma); }

// ----- The dual-Kawase pyramid -----
//
// A wide, smooth blur for the price of a few small passes: each pass halves the picture (PSKawaseDown), then doubles it back (PSKawaseUp), Params.z the
// taps' offset in texels of the pass's input. Bilinear taps on premultiplied (or linear) values.

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

// What is bright enough to bloom, in linear light, its threshold (Params2.x, gamma-encoded) with a soft knee: drawn at half the picture's size, it
// starts the pyramid of the glow and of the lens's streaks.
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

// Light added to a picture at a pixel: its colour, in linear light, on a premultiplied picture; what it brightens beyond the picture's shape shows too.
float4 AddLight(float4 picture, float3 light)
{
    float4 c = Unpremul(picture);
    float3 total = ToLinear(saturate(c.rgb)) * c.a + light;
    float a = saturate(c.a + Luma(light));
    return Premul(ToFilm(saturate(total / max(a, 1e-4))), a);
}

// Film Halation & Glow (slot 23): P0 amount, P1 threshold, P2 spread (the host's), P3 halation, P4..P6 its colour, P7 mix (%). Aux the blurred highlights.
// Halation is light that went through the film and back off its backing: only the red-sensitive layer sees it again, so the halo keeps the highlight's
// brightness and takes the film's colour instead of its own.
float4 PSGlowMix(VsOut i) : SV_Target
{
    float4 c = SampleAt(i.Uv, float2(0, 0));
    float3 glow = Aux.SampleLevel(LinearClamp, i.Uv, 0).rgb * P0;
    glow = lerp(glow, Luma(glow) * float3(P4, P5, P6) * 1.5, saturate(P3));
    return lerp(c, AddLight(c, glow), saturate(P7 / 100));
}

// ----- The Neon Rim Light (slot 24, variant 0) -----
//
// A distance field of the layer's shape, by jump flooding: whatever its shape (a mask, a key, a rounded box), in about log2(reach) passes. Each texel keeps
// the way to the nearest texel inside the shape (.xy) and outside it (.zw) as offsets in texels: relative, so RGBA16F keeps them exact to a fraction
// of a texel at the reach used. The input is mapped by UvRect, which leaves room round the shape (beyond it: nothing, the border sampler).
static const float FarAway = 20000;

float4 PSJfaSeed(VsOut i) : SV_Target
{
    float a = Input.SampleLevel(LinearBorder, lerp(UvRect.xy, UvRect.zw, i.Uv), 0).a;
    return a >= 0.5 ? float4(0, 0, FarAway, FarAway) : float4(FarAway, FarAway, 0, 0);
}

// One pass: Params.z the step, in texels: a neighbour's nearest, seen from here, may be nearer than this texel's. A neighbour beyond the edge is passed
// over by a choice, not a branch: on a tiled mobile GPU (an Adreno) a divergent branch round each of the eight taps cost more than the taps themselves
// (5.2-Arm64AndNpu).
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

// The rim on the grown picture: Input the layer's picture (mapped by UvRect), Aux the field, Params2.x its texels to this picture's texels, Params2.y what
// share of the picture itself stays (Frosted Glass's), P0..P2 the colour, P3 the line's width and P4 the glow's reach in output pixels (Params.w),
// P5 the strength, P6 how far the light falls inward. The light is added: its colour counts on any picture under it, and only the line itself is solid.
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

// ----- The Impact Shockwave (slot 25, variant 0) -----
//
// A ring of refraction going out from a point: P0 how far through it is (0..1), P1 and P2 the point (% of the picture), P3 the ripple in px of a 1080-wide
// frame, P4 how far it gets (% of the picture's height, from the middle), P5 how wide the ring is (% of how far it has got), P6 the camera shake (px, as P3).
// Channels bend a little differently (a real lens's fringe). Nothing depends on the clock but where it is through its run and the shake's own noise.

float ShakeNoise(float n)
{
    n = frac(n * 0.1031);
    n *= n + 33.33;
    n *= n + n;
    return frac(n) * 2 - 1;
}

// How far the ring is out, a share of the whole way: fast, then slowing, as a pressure front does.
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

    // Pushed outward ahead of the ring, drawn in behind it.
    float2 toward = d > 1e-5 ? p / d : float2(0, 0);
    float pixels = P3 * Params.x / max(DstSize.y, 1);
    float2 bend = toward * sin(clamp(away, -1.5, 1.5) * 2.0944) * ring * pixels * fading / scale;

    // The whole frame shaken, and held in a little so no edge shows.
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

    // The compressed air catches the light.
    c.rgb += ring * fading * 0.18 * c.a * saturate(P3 / 60);
    return c;
}

// ----- The Anamorphic Lens (slot 25, variant 1) -----

// One pass of the streaks (a quarter of the picture's height, half its width): seven taps along the row, Params.z texels apart; the host goes wider each pass.
// The row turned Params.y radians (5.2: the lens's Rotation), as long on the screen whichever way it runs (Params.w the picture's width over its height).
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

// Anamorphic Lens: P0 the streaks' strength, P1 threshold, P2 length (the host's), P3..P5 their colour, P6 the barrel and colour fringe. Aux the streaks.
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
