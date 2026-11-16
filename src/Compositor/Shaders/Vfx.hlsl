// Procedural VFX: slot 26 Explosion, 27 Sparks & Debris, 28 Anime Lines, 29 Energy Shield and Saber. Each renders into its own
// transparent target; PSVfxComposite lays it over the input with Light Spill (a heavily blurred copy brightens what is beneath, so on an
// adjustment clip a fireball lights the underlying video). Deterministic in progress (P0) and sequence time (Params4.w).
// Points are % of the picture; sizes % of height or px of a 1080-wide frame (Params.x). Conventions: Common.hlsli.

#include "Common.hlsli"

// Light spill: the effect picture heavily blurred (SceneRenderer.Fx.cs Lit): t5.
Texture2D<float4> Spill : register(t5);

// Effect picture (Aux) over the input, with the spill (strength Params2.x) brightening the input.
float4 PSVfxComposite(VsOut i) : SV_Target
{
    float4 under = SampleAt(i.Uv, float2(0, 0));
    float4 fx = Aux.SampleLevel(LinearClamp, i.Uv, 0);
    float3 light = Spill.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    float3 lit = under.rgb * (1 + light * Params2.x * 2.5);
    return float4(fx.rgb + lit * (1 - fx.a), fx.a + under.a * (1 - fx.a));
}

float Hash(float n)
{
    n = frac(n * 0.1031);
    n *= n + 33.33;
    n *= n + n;
    return frac(n);
}

// Aspect-corrected coordinates (height = 1) relative to a point given in % of the picture.
float2 Aspected(float2 uv, float2 point_)
{
    return (uv - point_ / 100) * float2(DstSize.x / max(DstSize.y, 1), 1);
}

// Rotates a vector by an effect's Rotation in degrees, clockwise on screen (y down); 0 leaves it unchanged.
float2 Turn(float2 v, float degrees)
{
    float s, c;
    sincos(radians(degrees), s, c);
    return float2(v.x * c - v.y * s, v.x * s + v.y * c);
}

// ----- Explosion (slot 26) -----
//
// Fireball raymarched through a tiled baked 3D noise (t2): P0 progress 0..1, P1/P2 position, P3 size (% of height), P4..P6 fire colour,
// P7 smoke; Q1 seed, Q2 Rotation. Bursts fast, billows, cools from the outside in and leaves smoke. Per-pixel jittered ray starts turn
// banding into fine grain that the half-resolution target's bilinear upscale smooths away.
static const int ExplosionSteps = 26;

float3 Fire(float heat, float3 tint)
{
    const float3 ember = float3(0.32, 0.02, 0.0), white = float3(1.7, 1.4, 0.9);
    return heat < 0.5 ? lerp(ember, tint, heat * 2) : lerp(tint, white, (heat - 0.5) * 2);
}

float4 PSExplosion(VsOut i) : SV_Target
{
    float age = saturate(P0);
    float radius = max(P3 / 100 * 0.5 * (1 - exp(-7 * age)) * (1 + 0.35 * age), 1e-3);
    float2 at = Turn(Aspected(i.Uv, float2(P1, P2)), -Q2) / radius;
    if (dot(at, at) > 2.4)
        return float4(0, 0, 0, 0);

    float3 from = float3(at, -1.5);
    float3 way = normalize(float3(at * 0.22, 1));
    float stride = 3.0 / ExplosionSteps;
    float start = frac(52.9829189 * frac(dot(i.Position.xy, float2(0.06711056, 0.00583715))));
    float seed = Q1 * 0.137;
    float4 total = float4(0, 0, 0, 0);
    float3 tint = float3(P4, P5, P6);
    float gone = 1 - smoothstep(0.7, 1, age);
    [loop] for (int k = 0; k < ExplosionSteps; k++)
    {
        float3 q = from + way * (k + start) * stride;
        float r = length(q);
        if (r > 1.35)
            continue;

        // Billow outward from the centre as it grows.
        float4 n = Lut.SampleLevel(LinearWrap, q * 0.3 + seed - q / max(r, 0.05) * age * 0.32, 0);
        float billow = n.r * 0.65 + n.g * 0.35;
        float density = saturate((1 - r + (billow - 0.5) * 1.15) * 4) * gone;
        if (density < 0.01)
            continue;

        float heat = saturate((1 - r * (0.75 + age * 0.9)) * 1.9 + (n.b - 0.5) * 0.9 - age * 1.25 + 0.35);
        float3 smoke = lerp(0.015, 0.14, n.a) * (1 + heat * 2);
        float3 colour = lerp(smoke, Fire(heat, tint), smoothstep(0.12, 0.42, heat));
        float taken = 1 - exp(-density * stride * (1.2 + P7 * 2.4 * age + heat * 2.2));
        total.rgb += (1 - total.a) * colour * taken;
        total.a += (1 - total.a) * taken;
        if (total.a > 0.99)
            break;
    }

    return total;
}

// ----- Sparks & Debris (slot 27) -----
//
// Bufferless particles derived from their index, six vertices each, in 3D (nearer = larger). P0 progress, P1/P2 origin, P3 count,
// P4 speed, P5 gravity, P6 spread (degrees), P7 size; Q0..Q2 colour, Q3 debris share, Q5 seed, Q6 direction (degrees), Q7 Rotation.
// Params2.x: 0 = sparks (additive light streaks), 1 = debris (tumbling dark shards, alpha-over).

static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };

// Particle offset after a time (x, y in picture heights from the origin; z toward the viewer): damped launch plus gravity.
float3 Flown(float3 direction, float speed, float seconds)
{
    return direction * speed * (1 - exp(-2.8 * seconds)) / 2.8 * 1.6 + float3(0, P5 * 1.1 * seconds * seconds, 0);
}

VsOut VSSparks(uint id : SV_VertexID)
{
    VsOut o;
    o.Position = float4(0, 0, 0, 0);
    o.Uv = float2(0, 0);
    o.Local = float2(0, 0);

    float s = (id / 6) * 1.37 + Q5 * 7.13 + 1;
    float2 c = Corners[id % 6];
    float born = Hash(s) * 0.22;
    float life = lerp(0.35, 0.78, Hash(s + 11.1));
    float age = (P0 - born) / life;
    bool debris = Hash(s + 23.7) < Q3;
    if (age <= 0 || age >= 1 || debris != (Params2.x > 0.5))
        return o;

    float angle = radians(Q6 + (Hash(s + 31.3) - 0.5) * P6);
    float rise = (Hash(s + 41.9) - 0.5) * 1.4;
    float3 direction = float3(cos(angle) * cos(rise), sin(angle) * cos(rise), sin(rise));
    float speed = lerp(0.25, 1.1, Hash(s + 53.1) * Hash(s + 57.7)) * P4;
    if (debris)
        speed *= 0.55;

    float seconds = P0 - born;
    float3 here = Flown(direction, speed, seconds);
    float3 before = Flown(direction, speed, max(seconds - 0.02, 0));
    float near = 1 / max(1 - here.z * 0.6, 0.3);
    float2 size = float2(DstSize.y / max(DstSize.x, 1), 1);
    float2 start = float2(P1, P2) / 100;
    float2 at = (start + Turn(here.xy, Q7) * near * size) * DstSize;
    float2 from = (start + Turn(before.xy, Q7) * near * size) * DstSize;

    float2 pixel;
    if (debris)
    {
        float turn = Hash(s + 61.3) * 6.2832 + seconds * lerp(-14, 14, Hash(s + 67.9));
        float across = lerp(2.5, 8, Hash(s + 71.1)) * P7 * Params.x * near;
        float k = cos(turn), n = sin(turn);
        pixel = at + float2(c.x * k - c.y * n, c.x * n + c.y * k) * across * float2(1, 0.65);
    }
    else
    {
        float2 along = at - from;
        float length_ = max(length(along), 1e-3);
        float2 axis = along / length_;
        float width = max(1.3 * P7 * Params.x * near, 0.8);
        pixel = (at + from) * 0.5 + axis * c.x * (length_ * 0.5 + width) + float2(-axis.y, axis.x) * c.y * width;
    }

    o.Position = float4(pixel / DstSize * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = c;
    o.Local = float2(age, Hash(s + 79.3));
    return o;
}

// Light streak: white-hot, cooling through the colour to dull red.
float4 PSSparks(VsOut i) : SV_Target
{
    float across = 1 - abs(i.Uv.y);
    float core = across * across * (1 - i.Uv.x * i.Uv.x);
    float age = i.Local.x;
    float3 hot = lerp(float3(1.7, 1.5, 1.2), float3(Q0, Q1, Q2) * 1.5, smoothstep(0, 0.4, age));
    float3 colour = lerp(hot, float3(0.7, 0.12, 0.02), smoothstep(0.55, 1, age));
    float fade = (1 - age) * (1 - age) * (0.75 + 0.25 * i.Local.y);
    return float4(colour * core * fade, core * fade * 0.4);
}

// Shard: dark, lit along one edge, fading out as it lands.
float4 PSDebris(VsOut i) : SV_Target
{
    float age = i.Local.x;
    float a = saturate((1 - max(abs(i.Uv.x), abs(i.Uv.y))) * 3) * (1 - smoothstep(0.75, 1, age));
    float3 colour = lerp(float3(0.03, 0.028, 0.026), float3(Q0, Q1, Q2) * 0.5, saturate(i.Uv.x * 0.5 + 0.1) * (1 - age));
    return Premul(colour, a);
}

// ----- Anime Lines (slot 28) -----
//
// P0 style (0 radial, 1 horizontal, 2 vertical), P1/P2 centre, P3 count, P4 thickness, P5 clear centre (% of height), P6 redraws per
// second, P7 opacity (%); Q0..Q2 colour, Q4 seed, Q5 Rotation about the centre. Redrawn at a fixed rate like hand-drawn animation, never tweened.
float4 PSAnimeLines(VsOut i) : SV_Target
{
    float drawing = floor(Params4.w * max(P6, 1));
    float seed = Q4 * 3.17 + drawing * 7.31 + 1;
    float count = max(P3, 1);
    float shown = 0;
    // Rotate the lines about their centre (Q5) by sampling the pre-rotation point.
    float2 turned = Turn(Aspected(i.Uv, float2(P1, P2)), -Q5);
    float2 uv = turned / float2(DstSize.x / max(DstSize.y, 1), 1) + float2(P1, P2) / 100;
    if (P0 < 0.5)
    {
        float2 p = turned;
        float r = length(p);
        float around = (atan2(p.y, p.x) / 6.28318531 + 0.5) * count;
        float line_ = floor(around);
        float inner = P5 / 200 * (1 + Hash(line_ * 0.73 + seed) * 1.4);
        float there = step(0.38, Hash(line_ * 1.91 + seed));
        float wide = P4 * 0.16 * Hash(line_ * 2.37 + seed) * smoothstep(inner, inner + 0.3, r);
        float pixel = count / (6.28318531 * max(r, 1e-3) * DstSize.y);

        // Near the centre a pixel spans many lines: lines thinner than a pixel fade with their width so the clear centre does not fill in.
        shown = there * saturate(wide / pixel) * (1 - smoothstep(wide - pixel, wide + pixel, abs(frac(around) - 0.5)));
    }
    else
    {
        bool across = P0 < 1.5;
        float along = across ? uv.x : uv.y;
        float rows = (across ? uv.y : uv.x) * count;
        float line_ = floor(rows);
        float centre = Hash(line_ * 0.73 + seed);
        float reach = lerp(0.08, 0.45, Hash(line_ * 1.13 + seed));
        float there = step(0.45, Hash(line_ * 1.91 + seed));
        float wide = P4 * 0.2 * Hash(line_ * 2.37 + seed) * saturate(1 - abs(along - centre) / reach);
        float pixel = count / (across ? DstSize.y : DstSize.x);
        shown = there * (1 - smoothstep(wide - pixel, wide + pixel, abs(frac(rows) - 0.5)));
    }

    return Premul(float3(Q0, Q1, Q2), shown * saturate(P7 / 100));
}

// ----- Energy Shield and Saber (slot 29; variant in the second packet's last lane) -----

// Distance to the nearest hexagon-grid edge: 0 on an edge, ~0.5 at a cell centre.
float HexEdge(float2 p)
{
    const float2 side = float2(1, 1.7320508);
    float2 a = fmod(abs(p), side) - side * 0.5;
    float2 b = fmod(abs(p) + side * 0.5, side) - side * 0.5;
    float2 cell = dot(a, a) < dot(b, b) ? a : b;
    cell = abs(cell);
    return 0.5 - max(dot(cell, normalize(side)), cell.x);
}

// Shield: hexagon dome with a bright rim. P0 rise, P1/P2 centre, P3 radius (% of height), P4..P6 colour, P7 hexagon strength;
// Q1 pulse rate, Q2 seed, Q3 Rotation.
float4 PSEnergyShield(VsOut i) : SV_Target
{
    float rise = saturate(P0);
    float grown = rise * rise * (3 - 2 * rise);
    float radius = max(P3 / 100 * grown, 1e-3);
    float2 p = Turn(Aspected(i.Uv, float2(P1, P2)), -Q3) / radius;
    float d = dot(p, p);
    float3 tint = float3(P4, P5, P6);
    if (d >= 1)
    {
        float halo = exp(-(sqrt(d) - 1) * 9) * 0.5 * grown;
        return float4(tint * halo, halo * 0.3);
    }

    float z = sqrt(1 - d);
    float fresnel = 1 - z;
    fresnel = fresnel * fresnel * fresnel;
    float2 sphere = p / (1 + z);
    float pulse = 0.5 + 0.5 * sin(sqrt(d) * 9 - Params4.w * max(Q1, 0.01) * 6.2832 + Q2);
    float edge = 1 - smoothstep(0, 0.09, HexEdge(sphere * 8 + Q2 * 0.37));
    float web = edge * saturate(P7) * (0.25 + 0.55 * pulse + fresnel);
    float strength = saturate(fresnel * 1.1 + web + 0.04) * grown;
    return float4(lerp(tint, float3(1, 1, 1), fresnel * 0.5) * strength, strength * 0.45);
}

// Saber: a blade of light. P0 extension, P1/P2 hilt, P3/P4 tip (% of picture), P5..P7 colour; Q1 core width (px of a 1080-wide frame),
// Q2 glow, Q3 flicker, Q4 Rotation about the hilt-tip midpoint.
float4 PSSaber(VsOut i) : SV_Target
{
    float2 hilt = float2(P1, P2) / 100 * DstSize;
    float2 tip = float2(P3, P4) / 100 * DstSize;
    float2 middle = (hilt + tip) * 0.5;
    float2 turned = middle + Turn(hilt - middle, Q4);
    tip = middle + Turn(tip - middle, Q4);
    hilt = turned;
    float out_ = saturate(P0);
    tip = lerp(hilt, tip, out_ * out_ * (3 - 2 * out_));
    float2 pixel = i.Uv * DstSize;
    float2 blade = tip - hilt;
    float along = saturate(dot(pixel - hilt, blade) / max(dot(blade, blade), 1e-3));
    float d = length(pixel - hilt - blade * along);

    float core = max(Q1 * Params.x, 1);
    float flicker = 1 - saturate(Q3) * 0.3 * Hash(floor(Params4.w * 48) * 0.71 + 3.1);
    float solid = 1 - smoothstep(core * 0.5, core, d);
    float glow = (exp(-d / (core * 1.6)) * 0.95 + exp(-d / (core * 7)) * 0.3) * max(Q2, 0) * flicker;
    float3 colour = float3(P5, P6, P7);
    return float4(colour * glow * (1 - solid) + float3(1, 1, 1) * solid, solid + glow * 0.35 * (1 - solid));
}
