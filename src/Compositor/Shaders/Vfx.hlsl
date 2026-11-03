// HandPeg compositor: procedural 3D effects (Pass 5.1-GpuFx): slot 26 the Explosion, 27 Sparks & Debris, 28 Anime Lines, 29 the Energy Shield and the Saber.
// Each draws into a transparent picture of its own, and PSVfxComposite lays that over what the card works on, with the Light Spill: the picture itself, very
// blurred, lights what is under it (a card on an adjustment clip works on everything under it, so a fireball lights the video it goes off on).
// Nothing here depends on the clock but where the card is through its run (P0) and Params4.w, the moment of the sequence: one moment always draws one frame.
// Where a point is given it is in % of the picture; sizes in % of its height or px of a 1080-wide frame (Params.x). Conventions: Common.hlsli.

#include "Common.hlsli"

// The light: the picture blurred (SceneRenderer.Fx.cs Lit): t5.
Texture2D<float4> Spill : register(t5);

// The effect's picture (Aux) over the input, the spill (Params2.x) multiplying what is there.
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

// The picture's own square units (a height of 1), from a point in % of it.
float2 Aspected(float2 uv, float2 point_)
{
    return (uv - point_ / 100) * float2(DstSize.x / max(DstSize.y, 1), 1);
}

// ----- The Explosion (slot 26) -----
//
// A ball of fire raymarched through a baked 3D noise (t2, tiled): P0 how far through (0..1), P1, P2 where, P3 how large (% of the height), P4..P6 the fire's colour,
// P7 the smoke; Q1 the seed. It bursts out fast, rolls, cools from the outside in and leaves smoke. The steps are started a little off from pixel to pixel,
// which turns what would be bands into a fine grain the half-size picture and its bilinear enlargement smooth away.
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
    float2 at = Aspected(i.Uv, float2(P1, P2)) / radius;
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

        // Rolling out from the middle as it grows.
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
// Particles with no buffer at all: every one is worked out from its index, six vertices each, in 3D (toward the viewer is larger). P0 how far through, P1, P2 where,
// P3 how many, P4 speed, P5 gravity, P6 the spread (degrees), P7 size; Q0..Q2 the colour, Q3 the share that is debris, Q5 seed, Q6 the direction (degrees).
// Params2.x: 0 the sparks (streaks of light, added), 1 the debris (tumbling dark shards, over).

static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };

// Where a particle is (x, y in the picture's height, from where it started; z toward the viewer) after so long.
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
    float2 at = (start + here.xy * near * size) * DstSize;
    float2 from = (start + before.xy * near * size) * DstSize;

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

// A streak of light: white-hot, cooling through the colour to a dull red.
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

// A shard: dark, lit a little along one edge, gone as it lands.
float4 PSDebris(VsOut i) : SV_Target
{
    float age = i.Local.x;
    float a = saturate((1 - max(abs(i.Uv.x), abs(i.Uv.y))) * 3) * (1 - smoothstep(0.75, 1, age));
    float3 colour = lerp(float3(0.03, 0.028, 0.026), float3(Q0, Q1, Q2) * 0.5, saturate(i.Uv.x * 0.5 + 0.1) * (1 - age));
    return Premul(colour, a);
}

// ----- Anime Lines (slot 28) -----
//
// P0 the style (0 radial, 1 across, 2 down), P1, P2 the middle, P3 how many, P4 thickness, P5 the clear middle (% of the height), P6 new drawings a second,
// P7 the opacity (%); Q0..Q2 the colour, Q4 the seed. Drawn anew so many times a second, on twos as a hand would, never smoothly animated.
float4 PSAnimeLines(VsOut i) : SV_Target
{
    float drawing = floor(Params4.w * max(P6, 1));
    float seed = Q4 * 3.17 + drawing * 7.31 + 1;
    float count = max(P3, 1);
    float shown = 0;
    if (P0 < 0.5)
    {
        float2 p = Aspected(i.Uv, float2(P1, P2));
        float r = length(p);
        float around = (atan2(p.y, p.x) / 6.28318531 + 0.5) * count;
        float line_ = floor(around);
        float inner = P5 / 200 * (1 + Hash(line_ * 0.73 + seed) * 1.4);
        float there = step(0.38, Hash(line_ * 1.91 + seed));
        float wide = P4 * 0.16 * Hash(line_ * 2.37 + seed) * smoothstep(inner, inner + 0.3, r);
        float pixel = count / (6.28318531 * max(r, 1e-3) * DstSize.y);

        // (Near the middle a pixel spans many lines: a line narrower than a pixel fades with its width, or the clear middle would fill with half-covered pixels.)
        shown = there * saturate(wide / pixel) * (1 - smoothstep(wide - pixel, wide + pixel, abs(frac(around) - 0.5)));
    }
    else
    {
        bool across = P0 < 1.5;
        float along = across ? i.Uv.x : i.Uv.y;
        float rows = (across ? i.Uv.y : i.Uv.x) * count;
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

// ----- The Energy Shield and the Saber (slot 29; the variant in the second packet's last lane) -----

// How near a point is to the edges of a hexagon grid: 0 on an edge, about 0.5 at a cell's middle.
float HexEdge(float2 p)
{
    const float2 side = float2(1, 1.7320508);
    float2 a = fmod(abs(p), side) - side * 0.5;
    float2 b = fmod(abs(p) + side * 0.5, side) - side * 0.5;
    float2 cell = dot(a, a) < dot(b, b) ? a : b;
    cell = abs(cell);
    return 0.5 - max(dot(cell, normalize(side)), cell.x);
}

// The shield: a dome of hexagons with a bright rim. P0 how far it has risen, P1, P2 its middle, P3 its radius (% of the height), P4..P6 its colour, P7 the hexagons;
// Q1 how fast its pulse goes, Q2 the seed.
float4 PSEnergyShield(VsOut i) : SV_Target
{
    float rise = saturate(P0);
    float grown = rise * rise * (3 - 2 * rise);
    float radius = max(P3 / 100 * grown, 1e-3);
    float2 p = Aspected(i.Uv, float2(P1, P2)) / radius;
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

// The saber: a blade of light. P0 how far it has come out, P1, P2 its hilt, P3, P4 its tip (% of the picture), P5..P7 its colour; Q1 the core's width (px of a
// 1080-wide frame), Q2 the glow, Q3 how much it flickers.
float4 PSSaber(VsOut i) : SV_Target
{
    float2 hilt = float2(P1, P2) / 100 * DstSize;
    float2 tip = float2(P3, P4) / 100 * DstSize;
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
