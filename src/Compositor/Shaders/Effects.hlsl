// Zone 1-3 effects (Look, Key & Matte): one pixel shader per GPU slot (GpuShaderSlot lane packing). Each matches the FFmpeg filter the
// graph builder writes for the same card, so the preview matches the export. CPU mirror, formula for formula and tap for tap:
// tools/GoldenTests/CompositorReference.cs (change both together).
//
// Entries read Input (t0, premultiplied) at i.Uv, a pixel centre of the working texture (VSFull), and return premultiplied RGBA. Colour
// effects unpremultiply, work on straight RGB and premultiply, keeping alpha. Neighbourhood taps are point samples (SampleAt, edge-clamped)
// so the CPU reference can follow them exactly; radii are capped at MaxRadius texels.

#include "Common.hlsli"

static const int MaxRadius = 64;

float Max3(float3 v) { return max(v.r, max(v.g, v.b)); }
float Min3(float3 v) { return min(v.r, min(v.g, v.b)); }

// The pass's own pixel.
float4 Here(VsOut i) { return SampleAt(i.Uv, float2(0, 0)); }

// Straight colour one tap away, clamped to 0..1 (FFmpeg filters see 8-bit values).
float3 StraightAt(float2 uv, float2 texels) { return saturate(Unpremul(SampleAt(uv, texels)).rgb); }

// Separable pass step: one texel horizontally (Params.y 0) or vertically (1).
float2 PassStep() { return Params.y > 0.5 ? float2(0, 1) : float2(1, 0); }

// Separable pass radius in texels (host-set Params.z), rounded and clamped to 0..MaxRadius.
int PassRadius() { return clamp((int)(Params.z + 0.5), 0, MaxRadius); }

// ----- Look -----

// Flip (slot 2): P0 horizontal, P1 vertical (hflip, vflip). Pixel centres mirror onto pixel centres.
float4 PSFlip(VsOut i) : SV_Target
{
    float2 uv = float2(P0 > 0.5 ? 1 - i.Uv.x : i.Uv.x, P1 > 0.5 ? 1 - i.Uv.y : i.Uv.y);
    return SampleAt(uv, float2(0, 0));
}

// LUT 3D (slot 3): the .cube table in Lut (t2, N per side, N = Params2.x, red fastest), sampled trilinearly (lut3d defaults to
// tetrahedral; the two differ negligibly within a cell).
float4 PSLut3D(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float n = Params2.x;
    if (n < 2)
        return Premul(c.rgb, c.a);

    float3 at = saturate(c.rgb) * ((n - 1) / n) + 0.5 / n;
    return Premul(saturate(Lut.SampleLevel(LinearClamp, at, 0).rgb), c.a);
}

// Denoise (slot 4): P0 strength 0..20 (hqdn3d spatial luma strength; default 4). 5x5 bilateral filter with hqdn3d's similarity curve: a
// luma difference of `strength` 8-bit steps weighs 0.25, (1 - d)^g with g = ln 0.25 / ln(1 - strength / 255); spatial Gaussian of 2 texels.
// Taps are also alpha-weighted so transparent neighbours lend no colour; alpha is kept. Spatial half of hqdn3d only (no frame history).
float4 PSDenoise(VsOut i) : SV_Target
{
    float4 centre = Here(i);
    float strength = min(P0, 20);
    if (strength < 0.05 || centre.a <= 1e-5)
        return centre;

    float likeness = log(0.25) / log(1 - strength / 255);
    float y0 = Luma(saturate(centre.rgb / centre.a));
    float4 sum = float4(0, 0, 0, 0);
    [unroll] for (int dy = -2; dy <= 2; dy++)
    {
        [unroll] for (int dx = -2; dx <= 2; dx++)
        {
            float4 t = SampleAt(i.Uv, float2(dx, dy));
            float d = abs(Luma(saturate(Unpremul(t).rgb)) - y0);
            float w = exp(-(dx * dx + dy * dy) / 8.0) * pow(max(1 - d, 0), likeness);
            sum += t * w;
        }
    }

    return Premul(sum.rgb / sum.a, centre.a);
}

// Color Grade (slot 5): eq contrast (P0), brightness (P1), saturation (P2), gamma (P3). eq works on luma (gamma weight 1) and scales
// chroma by saturation: luma is replaced and the colour's offset from it scaled. Channel balance follows eq too: P3 = gamma x gamma_g,
// P4/P5 = Cb/Cr plane gammas sqrt(gamma_b / gamma_g) and sqrt(gamma_r / gamma_g), each on a 0..1 plane with 0.5 = grey.
float4 PSColorGrade(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float y = Luma(c.rgb);
    float v = P0 * (y - 0.5) + 0.5 + P1;
    float graded = v <= 0 ? 0 : min(pow(abs(v), 1 / max(P3, 0.01)), 1);
    if (P4 <= 0 && P5 <= 0)
        return Premul(saturate(graded + P2 * (c.rgb - y)), c.a);

    float cb = P2 * (c.b - y) / 1.8556 + 0.5;
    float cr = P2 * (c.r - y) / 1.5748 + 0.5;
    cb = cb <= 0 ? 0 : min(pow(abs(cb), 1 / max(P4, 0.01)), 1);
    cr = cr <= 0 ? 0 : min(pow(abs(cr), 1 / max(P5, 0.01)), 1);
    float r = graded + 1.5748 * (cr - 0.5);
    float b = graded + 1.8556 * (cb - 0.5);
    return Premul(saturate(float3(r, (graded - 0.2126 * r - 0.0722 * b) / 0.7152, b)), c.a);
}

// Hue (slot 18): hue rotation by P0 degrees and saturation P1 in BT.709 YCbCr: (Cb, Cr) rotated and scaled, luma kept.
float4 PSHueSaturation(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float y = Luma(c.rgb);
    float cb = (c.b - y) / 1.8556;
    float cr = (c.r - y) / 1.5748;
    float s, k;
    sincos(radians(P0), s, k);
    float cb2 = (cb * k - cr * s) * P1;
    float cr2 = (cb * s + cr * k) * P1;
    float r = y + 1.5748 * cr2;
    float b = y + 1.8556 * cb2;
    float g = (y - 0.2126 * r - 0.0722 * b) / 0.7152;
    return Premul(saturate(float3(r, g, b)), c.a);
}

// Color Balance push for one channel at a lightness (colorbalance get_component, before clipping): shadow, midtone and highlight values
// weighted by where the lightness falls, at 0.7 strength.
float BalancePush(float l, float s, float m, float h)
{
    const float a = 4, b = 0.333, scale = 0.7;
    return (s * saturate((b - l) * a + 0.5) + m * saturate((l - b) * a + 0.5) * saturate((1 - l - b) * a + 0.5) + h * saturate((l + b - 1) * a + 0.5)) * scale;
}

// Color Balance (slot 6, two packets): P0..P7 = rs gs bs rm gm bm rh gh, Q0 = bh, Q1 = preserve lightness. Tones are weighted by HSL
// lightness (max + min) / 2 (FFmpeg uses max + min, twice that; the builder compensates). Preserve Lightness restores the original lightness
// with one offset on all channels before clamping: preservel's HSL round trip result, without greying channels at 0 or 1.
float4 PSColorBalance(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float3 v = saturate(c.rgb);
    float l = (Max3(v) + Min3(v)) * 0.5;
    float3 o = v + float3(BalancePush(l, P0, P3, P6), BalancePush(l, P1, P4, P7), BalancePush(l, P2, P5, Q0));
    if (Q1 > 0.5)
        o += l - (Max3(o) + Min3(o)) * 0.5;
    return Premul(saturate(o), c.a);
}

// Colour temperature multiplier (colortemperature kelvin2rgb).
float3 KelvinMultiplier(float kelvin)
{
    float k = kelvin / 100;
    float3 m;
    if (k <= 66)
    {
        m.r = 1;
        m.g = saturate(0.39008157876901960784 * log(k) - 0.63184144378862745098);
    }
    else
    {
        float t = max(k - 60, 0);
        m.r = saturate(1.29293618606274509804 * pow(t, -0.1332047592));
        m.g = saturate(1.12989086089529411765 * pow(t, -0.0755148492));
    }

    m.b = k >= 66 ? 1 : k <= 19 ? 0 : saturate(0.54320678911019607843 * log(max(k - 10, 1e-6)) - 1.19625408914);
    return m;
}

// Color Temperature (slot 7): P0 kelvin, P1 mix, P2 preserve lightness 0..1 (colortemperature).
float4 PSColorTemperature(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float3 v = c.rgb;
    float3 n = lerp(v, v * KelvinMultiplier(P0), P1);
    float l0 = Max3(v) + Min3(v) + 1e-6;
    float l1 = Max3(n) + Min3(n) + 1e-6;
    n = lerp(n, n * (l0 / l1), P2);
    return Premul(saturate(n), c.a);
}

// Vignette (slot 8): P0 lens angle (vignette, forward, centred). Distance from the centre over the half-diagonal darkens by
// cos(angle * d)^4; black beyond. P1 Roundness (%): aspect (width / height)^(1 - P1 / 100), stretching distance vertically
// (EffectRegistry.VignetteAspect). P2 Feather (%): darkening confined to the outer part, d remapped to (d - (1 - f)) / f (the builder's geq).
float4 PSVignette(VsOut i) : SV_Target
{
    float4 c = Here(i);
    float2 middle = DstSize * 0.5;
    float aspect = pow(abs(DstSize.x / max(DstSize.y, 1)), 1 - saturate(P1 / 100));
    float2 scale = float2(1, aspect);
    float d = length((i.Uv * DstSize - 0.5 - middle) * scale) / length(middle);
    float feather = P2 > 0 ? clamp(P2 / 100, 0.05, 1) : 1;
    float k = cos(P0 * saturate((d - (1 - feather)) / feather));
    float f = d > 1 ? 0 : (k * k) * (k * k);
    return float4(c.rgb * f, c.a);
}

// ----- Key & Matte -----

// Chroma as chromakey measures it: full-range BT.601 U and V (as the key colour is converted), in 0..1 units.
float2 KeyChroma(float3 rgb)
{
    return float2(dot(rgb, float3(-0.16874, -0.33126, 0.5)), dot(rgb, float3(0.5, -0.41869, -0.08131)));
}

// Chroma Key (slot 10): P0..P2 key colour, P3 similarity, P4 blend (chromakey). Distance = mean 3x3 chroma distance over sqrt(2)
// (0..1); the resulting alpha multiplies the layer's. (P5 despill runs as a second pass, PSDespill, with the same packet.)
float4 PSChromaKey(VsOut i) : SV_Target
{
    float2 key = KeyChroma(saturate(float3(P0, P1, P2)));
    float diff = 0;
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float2 d = KeyChroma(StraightAt(i.Uv, float2(x, y))) - key;
            diff += sqrt(dot(d, d) * 0.5);
        }
    }

    diff /= 9;
    float keep = P4 > 0.0001 ? saturate((diff - P3) / P4) : (diff > P3 ? 1 : 0);
    return Here(i) * keep;
}

// Despill (slot 11, after PSChromaKey when P5 >= 0.005, same packet): despill with mix = P5, expand 0. Spill is the screen channel above a
// mix of the other two (blue when the key is bluer than green, else green) and is removed from that channel (the builder sets despill's
// scales to green 0, blue -1 for blue screens).
float4 PSDespill(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float3 v = saturate(c.rgb);
    float mix = saturate(P5);
    if (P2 > P1)
        v.b = max(v.b - max(v.b - (v.r * mix + v.g * (1 - mix)), 0), 0);
    else
        v.g = max(v.g - max(v.g - (v.r * mix + v.b * (1 - mix)), 0), 0);
    return Premul(v, c.a);
}

// Matte Choke (slot 12), one direction of a separable pass (host runs horizontal, then vertical): P0 > 0 erodes (min alpha within
// Params.z texels), P0 < 0 dilates (max), matching the builder's square erosion/dilation. Colour is kept; where dilation reaches empty
// pixels it takes the colour of the tap that supplied the alpha.
float4 PSMatteChoke(VsOut i) : SV_Target
{
    float4 c = Here(i);
    if (abs(P0) < 0.5)
        return c;

    // Loop to the uniform radius, with erosion and dilation in separate loops: a single max-radius loop holding both behind a branch
    // cost Adreno twice its taps.
    int r = clamp(PassRadius(), 1, MaxRadius);
    float2 step = PassStep();
    float best = c.a;
    float4 from = c;
    if (P0 > 0)
    {
        [loop] for (int n = 1; n <= r; n++)
            best = min(best, min(SampleAt(i.Uv, -step * n).a, SampleAt(i.Uv, step * n).a));
        return Premul(Unpremul(c).rgb, best);
    }

    [loop] for (int n = 1; n <= r; n++)
    {
        float4 before = SampleAt(i.Uv, -step * n);
        float4 after = SampleAt(i.Uv, step * n);
        bool nearer = before.a > best;
        best = nearer ? before.a : best;
        from = nearer ? before : from;
        nearer = after.a > best;
        best = nearer ? after.a : best;
        from = nearer ? after : from;
    }

    return Premul(Unpremul(c.a > 1e-5 ? c : from).rgb, best);
}

// Matte Smooth (slot 13), one direction of a separable pass: alpha averaged over Params.z texels each side with Hann weights
// sin^2(pi k / (2r + 2)), k = 1..2r + 1 (the builder's tmix weights for 2r + 1 frames, applied spatially: no frame history here). Colour
// kept; where absent, borrowed from the neighbours (weighted by alpha).
float4 PSMatteSmooth(VsOut i) : SV_Target
{
    float4 c = Here(i);
    int r = PassRadius();
    if (r < 1)
        return c;

    float2 step = PassStep();
    float total = 0;
    float4 sum = float4(0, 0, 0, 0);
    [loop] for (int n = 0; n <= 2 * MaxRadius; n++)
    {
        if (n > 2 * r)
            break;
        float s = sin(3.14159265 * (n + 1) / (2 * r + 2));
        float w = s * s;
        sum += SampleAt(i.Uv, step * (n - r)) * w;
        total += w;
    }

    float a = sum.a / total;
    float3 straight = c.a > 1e-5 ? c.rgb / c.a : sum.a > 1e-5 ? sum.rgb / sum.a : float3(0, 0, 0);
    return Premul(straight, a);
}

// Eight steps of a grown mask's rings: a square, as the builder's 3x3 dilation/erosion passes spread.
static const float2 MaskRing[8] =
{
    float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1), float2(1, 1), float2(-1, 1), float2(1, -1), float2(-1, -1),
};

// Mask value at uv (Aux: straight colour, bilinear, edge-clamped): luma x alpha, optionally inverted.
float MaskAt(float2 uv, bool invert)
{
    float4 m = Aux.SampleLevel(LinearClamp, uv, 0);
    float v = Luma(m.rgb) * m.a;
    return invert ? 1 - v : v;
}

// Mask (slot 14): Aux = mask picture stretched over the layer. P0 grow in output px (> 0 expands the visible area, < 0 shrinks), P1 invert,
// P2/P3 offset in % of layer width/height (positive = right/down; uncovered areas are hidden). Builder order: invert, grow, offset. Growth by
// R texels (R = |P0| x PixelScale, rounded, >= 1) takes the max (or min) over up to 8 evenly spaced rings out to R of the eight square
// steps: exact along rows, columns and diagonals, and at every ring for R <= 8.
float4 PSLayerMask(VsOut i) : SV_Target
{
    bool invert = P1 > 0.5;
    float2 at = i.Uv - clamp(float2(P2, P3), -50, 50) / 100;
    if (any(at < 0) || any(at > 1))
        return float4(0, 0, 0, 0);

    float m = MaskAt(at, invert);
    float grow = clamp(P0, -40, 40);
    if (grow != 0)
    {
        float reach = max(round(abs(grow) * Params.w), 1);
        float rings = min(reach, 8);
        [loop] for (int j = 1; j <= 8; j++)
        {
            if (j > rings)
                break;
            float2 span = reach * j / rings * SrcTexel;
            [unroll] for (int t = 0; t < 8; t++)
            {
                float v = MaskAt(at + MaskRing[t] * span, invert);
                m = grow > 0 ? max(m, v) : min(m, v);
            }
        }
    }

    return Here(i) * m;
}

// Rounded & Feathered Edges (slot 15): P0 corner radius in % of the shorter side, P1 soft edges, P2 their width in output px (x PixelScale).
// The builder's shape (BuildLayerMask) in this texture's pixels: depth inside a rectangle rounded by max(corner, feather), over the feather
// width (1 px anti-aliasing when not feathered). The host sends P1 = 0 when the layer has a mask (its outline is softened instead).
float4 PSCornerFeather(VsOut i) : SV_Target
{
    float4 c = Here(i);
    float shorter = min(DstSize.x, DstSize.y);
    float limit = max(floor(shorter / 2) - 1, 1);
    float corner = min(round(clamp(P0, 0, 50) / 100 * shorter), limit);
    float feather = P1 > 0.5 ? clamp(round(P2 * Params.w), 1, limit) : 0;
    if (corner == 0 && feather == 0)
        return c;

    float radius = max(corner, feather);
    float ramp = max(feather, 1);
    float2 p = i.Uv * DstSize - 0.5;
    float2 beyond = max(radius - p, 0) + max(p - (DstSize - 1 - radius), 0);
    return c * saturate((radius + 0.5 - length(beyond)) / ramp);
}

// Curves (slot 19): Aux = 256x1 table, preset then master curve baked into R, G and B by the host.
float4 PSToneCurve(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float3 u = saturate(c.rgb) * (255.0 / 256.0) + 0.5 / 256.0;
    float r = Aux.SampleLevel(LinearClamp, float2(u.r, 0.5), 0).r;
    float g = Aux.SampleLevel(LinearClamp, float2(u.g, 0.5), 0).g;
    float b = Aux.SampleLevel(LinearClamp, float2(u.b, 0.5), 0).b;
    return Premul(saturate(float3(r, g, b)), c.a);
}

// ----- Color Grade (slot 30: Color Studio, two packets) -----
// Mirrors ColorGradeMath.Apply step by step in float with the same constants; neutral steps are skipped. P0 temperature, P1 tint (each
// -100..100), P2 exposure (stops), P3..P5 lift r g b, P6 gamma r, P7 contrast; Q0/Q1 gamma g b, Q2..Q4 gain r g b, Q5 saturation, Q6
// vibrance. Q7 encodes LUT mix and table ownership; the host decodes it into Params2.x = LUT size N (0 none), Params2.y = mix, Params2.z = 1
// when Aux holds the card's curves (GpuColorTables layout: R, G, B = channel curve then master). The host skips neutral passes.

float3 StudioToLinear(float3 c) { return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4); }
float3 StudioToSrgb(float3 c) { return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1 / 2.4) - 0.055; }

// White-balance gains in linear light (ColorGradeMath.WhiteBalance): red vs blue, green vs both, luma preserved.
float3 StudioWhiteBalance(float temperature, float tint)
{
    float t = clamp(temperature / 100, -1, 1);
    float n = clamp(tint / 100, -1, 1);
    float3 w = float3((1 + 0.3 * t) * (1 + 0.125 * n), 1 - 0.25 * n, (1 - 0.3 * t) * (1 + 0.125 * n));
    return w / Luma(w);
}

// Lift (half range at black), gain (2^x) and gamma (exponent 2^-x) per channel (ColorGradeMath.Wheels).
float3 StudioWheels(float3 c, float3 lift, float3 gamma, float3 gain)
{
    c = c + 0.5 * lift * (1 - c);
    c *= exp2(gain);
    return pow(max(c, 0), exp2(-gamma));
}

float4 PSColorStudio(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float3 v = c.rgb;
    if (P2 != 0 || P0 != 0 || P1 != 0)
        v = StudioToSrgb(max(StudioToLinear(max(v, 0)) * StudioWhiteBalance(P0, P1) * exp2(P2), 0));

    float3 lift = float3(P3, P4, P5), gamma = float3(P6, Q0, Q1), gain = float3(Q2, Q3, Q4);
    if (any(lift != 0) || any(gamma != 0) || any(gain != 0))
        v = StudioWheels(v, lift, gamma, gain);
    if (P7 != 1)
        v = (v - 0.5) * P7 + 0.5;
    if (Q5 != 1 || Q6 != 0)
    {
        float y = Luma(v);
        float spread = saturate(Max3(v) - Min3(v));
        v = y + (v - y) * (Q5 * (1 + Q6 * (1 - spread)));
    }

    v = saturate(v);
    if (Params2.z > 0.5)
    {
        float3 u = v * (255.0 / 256.0) + 0.5 / 256.0;
        v = saturate(float3(Aux.SampleLevel(LinearClamp, float2(u.r, 0.5), 0).r, Aux.SampleLevel(LinearClamp, float2(u.g, 0.5), 0).g,
            Aux.SampleLevel(LinearClamp, float2(u.b, 0.5), 0).b));
    }

    float n = Params2.x;
    if (n >= 2 && Params2.y > 0)
    {
        float3 at = v * ((n - 1) / n) + 0.5 / n;
        v = lerp(v, saturate(Lut.SampleLevel(LinearClamp, at, 0).rgb), saturate(Params2.y));
    }

    return Premul(v, c.a);
}

// Box blur (slot 20), one direction (Params.y) of 2r + 1 equal taps, r = Params.z texels, on premultiplied values. The host computes r and
// runs horizontal, vertical, horizontal, vertical (boxblur's two passes, or the card's Passes); shadows, blur fill and blur dissolves reuse
// it. A fractional radius f (blur fill's continuous radius) adds an end tap each side weighted f, so the box grows smoothly between radii.
float4 PSBoxBlur(VsOut i) : SV_Target
{
    float radius = clamp(Params.z, 0, MaxRadius);
    int r = (int)radius;
    float f = r < MaxRadius ? radius - r : 0;
    if (r < 1 && f < 0.001)
        return Here(i);

    // Taps in pairs: a bilinear tap halfway between two texels is exactly their mean (half a texel is a whole step of the sub-texel grid,
    // and clamping repeats the edge texel as point taps do), so -r..r-1 take r fetches plus one for r: half the fetches.
    float2 step = PassStep();
    float4 sum = SampleAt(i.Uv, step * r);
    [loop] for (int n = 0; n < r; n++)
        sum += Input.SampleLevel(LinearClamp, i.Uv + step * ((2 * n - r) + 0.5) * SrcTexel, 0) * 2;

    if (f >= 0.001)
        sum += (SampleAt(i.Uv, step * -(r + 1)) + SampleAt(i.Uv, step * (r + 1))) * f;
    return sum / (2 * r + 1 + 2 * f);
}

// Sharpen (slot 21, unsharp mask): Aux = picture box-blurred by the host (EffectRegistry.SharpenBox, horizontal + vertical, as FFmpeg
// unsharp averages its square), premultiplied like the input. The BT.709 luma of the straight difference is added back Strength x 2 times
// (P0, EffectRegistry.SharpenAmount) to all channels equally, so only luma changes (unsharp's la). Threshold (P2, in 8-bit luma levels: 219
// from black to white) ignores smaller differences, ramping in over 2 levels around it (no hard step).
float4 PSSharpen(VsOut i) : SV_Target
{
    float4 c = Here(i);
    if (P0 < 0.005)
        return c;

    float4 s = Unpremul(c);
    float4 b = Unpremul(Aux.SampleLevel(PointClamp, i.Uv, 0));
    float d = dot(s.rgb - b.rgb, Luma709);
    float knee = P2 >= 0.5 ? saturate((abs(d) * 219 - P2 + 1) / 2) : 1;
    return Premul(saturate(s.rgb + d * 2 * saturate(P0) * knee), s.a);
}
