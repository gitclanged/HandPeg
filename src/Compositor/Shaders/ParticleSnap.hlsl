// HandPeg compositor: the Thanos Snap (slot 69, TransitionStyle.ParticleSnap): a picture breaking into
// embers on a wind. The picture cracks into Voronoi cells that let go one after another, in a sweep from the side the wind blows to,
// broken up by noise, each glowing along its cracks just before it goes; what has let go flies as grains, turning to embers and
// burning out. Nothing here depends on the clock but how far through the fade it is, so one moment always draws the same frame.
// Run backwards it is a picture gathering itself from embers.
// Compiled at build time (HandPegApp.csproj).

#include "Common.hlsli"

// P3 how far through the fade the clock is (0 to 1), P4 1 for a fade in.
// Params2: where the picture lies in what is drawn (it has room round it for the embers): u0, v0, width, height.
// Params3: the picture's size in texels; Params.x: texels per px of a 1080-wide frame.

static const float GrainLife = 0.34;  // Of the whole fade, how much a grain's flight takes.
static const int GrainSteps = 10;     // How many ages of grain are looked for at a pixel.
static const float3 Ember = float3(1.0, 0.46, 0.12);

float SnapHash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float2 SnapHash2(float2 p)
{
    float n = SnapHash(p);
    return float2(n, SnapHash(p + n + 17.17));
}

float SnapNoise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    f = f * f * (3 - 2 * f);
    return lerp(lerp(SnapHash(i), SnapHash(i + float2(1, 0)), f.x), lerp(SnapHash(i + float2(0, 1)), SnapHash(i + float2(1, 1)), f.x), f.y);
}

// The wind: up and to the right.
float2 SnapWind() { return float2(0.86, -0.5); }

// When a place lets go, 0 (first) to 1 (last): the side the wind blows to goes first; every grain has some chance of its own.
float SnapRelease(float2 uv, float2 id)
{
    float sweep = dot(uv - 0.5, SnapWind()) + 0.5;
    float broken = (1 - sweep) + (SnapNoise(uv * 5) - 0.5) * 0.35;
    return saturate(lerp(broken, SnapHash(id), 0.42));
}

float4 PSParticleSnap(VsOut i) : SV_Target
{
    float through = saturate(P3);
    float gone = P4 > 0.5 ? 1 - through : through;
    float2 uv = (i.Uv - Params2.xy) / Params2.zw;
    float2 size = Params3.xy;
    float grain = max(5 * Params.x, 2.5);
    float2 grid = size / grain;
    float4 result = 0;

    // What is still in place, cracked into cells.
    if (all(uv >= 0) && all(uv <= 1))
    {
        float2 p = uv * grid, cell = floor(p), f = p - cell;
        float first = 8, second = 8;
        float2 id = cell, middle = cell;
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                float2 near = float2(x, y), offset = SnapHash2(cell + near), r = near + offset - f;
                float d = dot(r, r);
                if (d < first)
                {
                    second = first;
                    first = d;
                    id = cell + near;
                    middle = cell + near + offset;
                }
                else if (d < second)
                {
                    second = d;
                }
            }
        }

        float4 source = Input.SampleLevel(LinearClamp, uv, 0);
        float lets = SnapRelease(middle / grid, id) * (1 - GrainLife) + 0.001;
        float there = gone <= lets ? 1 : 0;
        float cracks = 1 - smoothstep(0, 0.16, sqrt(second) - sqrt(first));
        float heat = (1 - smoothstep(0, 0.09, lets - gone)) * cracks * there * step(1e-4, gone);
        result = source * there;
        result.rgb += Ember * heat * source.a;
    }

    // What has let go: for each age of grain, where one of that age passing here came from.
    float reach = max(size.x, size.y) * 0.26;
    float2 swirl = float2(SnapNoise(uv * 7 + 3.1), SnapNoise(uv * 7 + 9.7)) - 0.5;
    [loop]
    for (int k = 0; k < GrainSteps; k++)
    {
        float age = (k + 0.5) / GrainSteps;
        float left = gone - age * GrainLife;
        if (left < 0)
            continue;

        float2 drift = (SnapWind() * age * age + swirl * age * 0.35) * reach / size;
        float2 from = uv - drift;
        if (any(from < -0.02) || any(from > 1.02))
            continue;

        float2 seed = from * grid, cell = floor(seed), offset = SnapHash2(cell + 41.3), home = (cell + offset) / grid;
        float lets = SnapRelease(home, cell) * (1 - GrainLife) + 0.001;
        if (abs(lets - left) > GrainLife * 0.5 / GrainSteps * 1.4)
            continue;

        float radius = 0.34 * (1 - age * 0.72);
        float distance2 = length((seed - (cell + offset)) * float2(1, 1));
        float dot_ = 1 - smoothstep(radius * 0.55, radius, distance2);
        if (dot_ <= 0)
            continue;

        float4 source = Input.SampleLevel(LinearClamp, saturate(home), 0);
        if (source.a <= 0.01)
            continue;

        float burn = smoothstep(0.08, 0.62, age);
        float alpha = dot_ * pow(saturate(1 - age), 1.4) * source.a * (1 - smoothstep(0.9, 1, gone));
        float3 colour = lerp(source.rgb / max(source.a, 1e-3), Ember * 1.25, burn);
        result.rgb = colour * alpha + result.rgb * (1 - alpha);
        result.a = alpha + result.a * (1 - alpha);
    }

    return result;
}
