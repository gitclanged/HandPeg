// Social cut transitions (slots 70-73): Whip Pan, Zoom Warp, RGB Glitch, Spin. Each is one pass over the layer's working picture
// (its own size) after its effects; the dissolve and box spin happen in SceneRenderer. CPU mirror:
// tools/GoldenTests/CompositorReference.Social.cs (change both together). Output depends only on fade progress (deterministic).
// Compiled at build time (HandPegApp.csproj).

#include "Common.hlsli"

// Packets: P0 eased visible share (TransitionStyles.Easing), P3 fade progress 0..1, P4 1 for a fade in.
// Outside the picture is transparent (LinearBorder) so the layer beneath shows.

static const float Pi = 3.14159265;

static const int WhipTaps = 32;
static const float WhipSmear = 0.35;    // Peak smear, fraction of picture width.

static const int WarpTaps = 24;
static const float WarpReach = 2.0;     // Extra zoom at zero visibility (1 + 2 = 3x).
static const float WarpStreak = 0.3;    // Radial streak at zero visibility, fraction of the zoom.

static const int GlitchSteps = 30;      // Distinct glitch patterns over the fade.
static const int GlitchBands = 18;      // Horizontal bands, each torn independently.

static const int SpinTaps = 24;
static const float SpinSweep = 0.35;    // Peak rotational blur, radians.

float4 Border(float2 uv) { return Input.SampleLevel(LinearBorder, uv, 0); }

// Whip Pan (70): slides in from the right (out to the left), smeared along the motion, most at peak speed.
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

// Zoom Warp (71): punches in from 3x (out into it) about the centre, streaked radially.
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

// lowbias32 integer hash, and a 0..1 value from two inputs (24 bits: exact as float on GPU and CPU).
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

// Glitch pattern index for a fade progress.
uint GlitchStep(float through) { return (uint)min(floor(saturate(through) * GlitchSteps), GlitchSteps - 1); }

// RGB Glitch (72): torn rows shifted sideways and red/blue split apart, scaled by the hidden share.
// Blink-out is decided on the CPU (SceneRenderer.GlitchBlinks).
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

// Spin (73): rotational blur about the anchor (Params2.xy), strongest at peak speed; SceneRenderer rotates the box.
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
