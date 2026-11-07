// HandPeg compositor: the social cut transitions (Pass 5.2.1, slots 70-73): Whip Pan, Zoom Warp, RGB Glitch and Spin. Each is one
// pass over the layer's working picture (its own size), after its effects; what the picture does on top (a dissolve, a spin of its
// box) is SceneRenderer's. Mirrored on the CPU by tools/GoldenTests/CompositorReference.Social.cs: change both together.
// Nothing here depends on the clock but how far through the fade it is, so one moment always draws the same frame.
// Compiled at build time (HandPegApp.csproj).

#include "Common.hlsli"

// Every one: P0 how much of the clip is there along its curve (TransitionStyles.Easing), P3 how far through the fade the clock is
// (0 to 1), P4 1 for a fade in. Outside the picture is transparent (LinearBorder): what is under it shows there.

static const float Pi = 3.14159265;

static const int WhipTaps = 32;
static const float WhipSmear = 0.35;    // The smear at the fastest, as a share of the picture's width.

static const int WarpTaps = 24;
static const float WarpReach = 2.0;     // How far it is zoomed in where it is not there at all: three times its size.
static const float WarpStreak = 0.3;    // The radial streak where it is not there at all, as a share of the zoom.

static const int GlitchSteps = 30;      // A new glitch so many times over the fade.
static const int GlitchBands = 18;      // Rows of the picture, each torn on its own.

static const int SpinTaps = 24;
static const float SpinSweep = 0.35;    // The spin's blur at the fastest, in radians.

float4 Border(float2 uv) { return Input.SampleLevel(LinearBorder, uv, 0); }

// Whip Pan (70): the picture whips in from the right (and away to the left), smeared along the way it goes, most at the fastest.
float4 PSWhipPan(VsOut i) : SV_Target
{
    float through = saturate(P3);
    float shift = (P4 > 0.5 ? 1 : -1) * (1 - saturate(P0));
    float smear = WhipSmear * sin(Pi * through);
    float4 sum = 0;
    for (int k = 0; k < WhipTaps; k++)
        sum += Border(float2(i.Uv.x - shift + ((k + 0.5) / WhipTaps - 0.5) * smear, i.Uv.y));
    return sum / WhipTaps;
}

// Zoom Warp (71): the picture punches in from three times its size (and away into it) about its middle, streaked outward.
float4 PSZoomWarp(VsOut i) : SV_Target
{
    float away = 1 - saturate(P0);
    float zoom = 1 + WarpReach * away;
    float streak = WarpStreak * away;
    float2 d = i.Uv - 0.5;
    float4 sum = 0;
    for (int k = 0; k < WarpTaps; k++)
        sum += Border(0.5 + d / (zoom * (1 + ((k + 0.5) / WarpTaps - 0.5) * streak)));
    return sum / WarpTaps;
}

// A whole number's hash (lowbias32), and a number in 0..1 from two (24 bits, exact as a float on the GPU and the CPU).
uint GlitchHash(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

float GlitchRandom(uint a, uint b) { return (GlitchHash(a * 0x9e3779b9u ^ GlitchHash(b)) >> 8) / 16777216.0; }

// Which glitch it is at a moment of the fade.
uint GlitchStep(float through) { return (uint)min(floor(saturate(through) * GlitchSteps), GlitchSteps - 1); }

// RGB Glitch (72): torn rows slid across and the red and blue pulled apart, as much as the clip is not there yet (or no longer).
// Whether the moment's frame blinks out is SceneRenderer's (GlitchBlink).
float4 PSRgbGlitch(VsOut i) : SV_Target
{
    float glitch = 1 - saturate(P0);
    uint step = GlitchStep(P3);
    uint band = (uint)min(floor(i.Uv.y * GlitchBands), GlitchBands - 1);
    float torn = GlitchRandom(band, step) > 1 - 0.6 * glitch ? (GlitchRandom(band, step + 101u) - 0.5) * 0.25 * glitch : 0;
    float split = 0.03 * glitch * (0.5 + 0.5 * GlitchRandom(step, 7u));
    float2 at = float2(i.Uv.x + torn, i.Uv.y);
    float4 r = Border(at + float2(split, 0));
    float4 g = Border(at);
    float4 b = Border(at - float2(split, 0));
    return float4(r.r, g.g, b.b, max(max(r.a, g.a), b.a));
}

// Spin (73): the picture blurred along its turn about its anchor (Params2.xy), most at the fastest; SceneRenderer turns its box.
float4 PSSpin(VsOut i) : SV_Target
{
    float sweep = SpinSweep * sin(Pi * saturate(P3));
    float2 anchor = Params2.xy;
    float2 p = (i.Uv - anchor) * DstSize;
    float4 sum = 0;
    for (int k = 0; k < SpinTaps; k++)
    {
        float a = ((k + 0.5) / SpinTaps - 0.5) * sweep;
        float c = cos(a), s = sin(a);
        sum += Border(anchor + float2(p.x * c - p.y * s, p.x * s + p.y * c) / DstSize);
    }
    return sum / SpinTaps;
}
