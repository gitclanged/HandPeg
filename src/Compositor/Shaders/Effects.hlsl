// HandPeg compositor: the effects of zones 1-3, Look and Key & Matte (Pass 5.0), one pixel shader per GPU slot
// (GpuShaderSlot; lanes as ARCHITECTURE_CONTRACTS.md section 1 packs them). Each follows the FFmpeg filter the graph
// builder writes for the same card, so the live picture matches the export. Mirrored on the CPU, formula for formula and
// tap for tap, by tools/GoldenTests/CompositorReference.cs: change both together.
//
// Every entry reads Input (t0, premultiplied) at i.Uv, a pixel centre of the working texture (VSFull), and returns
// premultiplied RGBA. A colour effect unpremultiplies, works on straight RGB and premultiplies again, keeping alpha.
// Neighbourhood taps are point samples (SampleAt, clamped to the edge), so the CPU reference can follow them exactly;
// radii are held to MaxRadius texels.

#include "Common.hlsli"

static const int MaxRadius = 64;

float Max3(float3 v) { return max(v.r, max(v.g, v.b)); }
float Min3(float3 v) { return min(v.r, min(v.g, v.b)); }

// The pass's own pixel.
float4 Here(VsOut i) { return SampleAt(i.Uv, float2(0, 0)); }

// The straight colour one tap away, held to 0..1 (FFmpeg's filters see 8-bit values).
float3 StraightAt(float2 uv, float2 texels) { return saturate(Unpremul(SampleAt(uv, texels)).rgb); }

// A separable pass's step, one texel across (Params.y 0) or down (1).
float2 PassStep() { return Params.y > 0.5 ? float2(0, 1) : float2(1, 0); }

// A separable pass's radius in texels (Params.z, set by the host), rounded and held to 0..MaxRadius.
int PassRadius() { return clamp((int)(Params.z + 0.5), 0, MaxRadius); }

// ----- Look -----

// Flip (slot 2): P0 left to right, P1 upside down (hflip, vflip). A pixel centre mirrors onto a pixel centre.
float4 PSFlip(VsOut i) : SV_Target
{
    float2 uv = float2(P0 > 0.5 ? 1 - i.Uv.x : i.Uv.x, P1 > 0.5 ? 1 - i.Uv.y : i.Uv.y);
    return SampleAt(uv, float2(0, 0));
}

// LUT 3D (slot 3): the .cube file's table in Lut (t2, N on a side, N = Params2.x, red fastest), looked up trilinearly
// between its entries (lut3d's own default is tetrahedral; the two differ by a hair inside a cell).
float4 PSLut3D(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float n = Params2.x;
    if (n < 2)
        return Premul(c.rgb, c.a);

    float3 at = saturate(c.rgb) * ((n - 1) / n) + 0.5 / n;
    return Premul(saturate(Lut.SampleLevel(LinearClamp, at, 0).rgb), c.a);
}

// Denoise (slot 4): P0 strength 0..20 (hqdn3d's spatial luma strength; 4 is its default). A 5 x 5 bilateral filter
// whose likeness curve is hqdn3d's own: a difference in luma of `strength` 8-bit steps weighs a quarter, (1 - d)^g with
// g = ln 0.25 / ln(1 - strength / 255); near pixels count more (a Gaussian of 2 texels). Taps are weighed by their
// alpha too, so a see-through neighbour lends no colour; alpha is kept. (hqdn3d also smooths between frames: the GPU has
// no frame history here, so only its spatial half is followed.)
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

// Color Grade (slot 5): eq's contrast (P0), brightness (P1), saturation (P2) and gamma (P3). eq works on luma (its
// gamma weight 1) and scales chroma by the saturation: here the luma is replaced and the colour's distance from it scaled.
float4 PSColorGrade(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float y = Luma(c.rgb);
    float v = P0 * (y - 0.5) + 0.5 + P1;
    float graded = v <= 0 ? 0 : min(pow(abs(v), 1 / max(P3, 0.01)), 1);
    return Premul(saturate(graded + P2 * (c.rgb - y)), c.a);
}

// Hue (slot 18): hue's turn by P0 degrees and saturation P1, in BT.709 YCbCr: (Cb, Cr) turned and scaled, luma kept.
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

// Color Balance's push on a channel at a lightness (colorbalance's get_component, before its clip): the shadows', the
// midtones' and the highlights' values, each weighed by where the lightness falls among the tones, at 0.7 of their strength.
float BalancePush(float l, float s, float m, float h)
{
    const float a = 4, b = 0.333, scale = 0.7;
    return (s * saturate((b - l) * a + 0.5) + m * saturate((l - b) * a + 0.5) * saturate((1 - l - b) * a + 0.5) + h * saturate((l + b - 1) * a + 0.5)) * scale;
}

// Color Balance (slot 6, two packets): P0..P7 = rs gs bs rm gm bm rh gh, Q0 = bh, Q1 = preserve lightness. The tones are
// weighed by the HSL lightness, (max + min) / 2 (5.0-AudioAnd3D, the user's Q1: FFmpeg's filter takes max + min, twice
// it, and the builder mends that around it). Preserve Lightness keeps the new colour's hue and chroma at the original's
// lightness, by one offset on all three channels before they are held to 0..1: what preservel's HSL round trip gives, with
// no colour turned grey for a channel at 0 or 1.
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

// A colour temperature's multiplier (colortemperature's kelvin2rgb).
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

// Vignette (slot 8): P0 the lens angle (vignette, forward, centred). A pixel's distance from the middle, over the
// half-diagonal, darkens it by cos(angle * d)^4; beyond the half-diagonal it is black. P1 the Roundness (%): vignette's
// aspect, (width / height)^(1 - P1 / 100), which (square pixels) stretches the distance down by it (EffectRegistry.VignetteAspect).
// P2 the Feather (%): the darkening over the outer part of the way out only, d narrowed to (d - (1 - f)) / f (the builder's geq).
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

// A colour's chroma as chromakey measures it: full-range BT.601 U and V (its key colour's conversion), in 0..1 steps.
float2 KeyChroma(float3 rgb)
{
    return float2(dot(rgb, float3(-0.16874, -0.33126, 0.5)), dot(rgb, float3(0.5, -0.41869, -0.08131)));
}

// Chroma Key (slot 10): P0..P2 the key colour, P3 similarity, P4 blend (chromakey). Its distance from the key is the
// mean over the 3 x 3 neighbourhood of each pixel's chroma distance, over sqrt(2) (so 0..1); the result's transparency
// multiplies what the layer had. (P5 despill is a second pass, PSDespill, with the same packet.)
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

// Despill (slot 11, run after PSChromaKey when P5 >= 0.005, with the Chroma Key's packet): despill with mix = P5 and
// expand 0, the builder's filter. The spill is the screen's channel above a mix of the other two (blue for a key bluer than
// it is green, else green), and it is taken off that channel (5.0-AudioAnd3D, the user's Q2: the builder sets despill's
// scales to green 0, blue -1 for a blue screen).
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

// Matte Choke (slot 12), one direction of a separable pass (the host runs across, then down): P0 > 0 erodes (the least
// alpha within Params.z texels), P0 < 0 dilates (the most), as the builder's passes of erosion or dilation do (a square).
// The colour is kept; where a dilation reaches into nothing, it takes the colour of the nearest tap that gave its alpha.
float4 PSMatteChoke(VsOut i) : SV_Target
{
    float4 c = Here(i);
    if (abs(P0) < 0.5)
        return c;

    int r = max(PassRadius(), 1);
    float2 step = PassStep();
    bool erode = P0 > 0;
    float best = c.a;
    float4 from = c;
    [loop] for (int n = 1; n <= MaxRadius; n++)
    {
        if (n > r)
            break;
        float4 before = SampleAt(i.Uv, -step * n);
        float4 after = SampleAt(i.Uv, step * n);
        if (erode)
        {
            best = min(best, min(before.a, after.a));
        }
        else
        {
            if (before.a > best)
            {
                best = before.a;
                from = before;
            }
            if (after.a > best)
            {
                best = after.a;
                from = after;
            }
        }
    }

    return Premul(Unpremul(c.a > 1e-5 ? c : from).rgb, best);
}

// Matte Smooth (slot 13), one direction of a separable pass: alpha averaged over Params.z texels either side with Hann
// weights, sin^2(pi k / (2r + 2)) for k = 1 .. 2r + 1 (the builder's tmix weights for 2r + 1 frames, laid out in space:
// the GPU has no frame history here). The colour is kept; where there was none, the neighbours' (by weight and alpha).
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

// The eight steps of a grown mask's rings: a square, as the builder's 3 x 3 dilation and erosion passes spread.
static const float2 MaskRing[8] =
{
    float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1), float2(1, 1), float2(-1, 1), float2(1, -1), float2(-1, -1),
};

// The mask's value at uv (Aux: straight colour, bilinear, clamped to its edge): its brightness times its alpha, inverted.
float MaskAt(float2 uv, bool invert)
{
    float4 m = Aux.SampleLevel(LinearClamp, uv, 0);
    float v = Luma(m.rgb) * m.a;
    return invert ? 1 - v : v;
}

// Mask (slot 14): Aux the mask picture, stretched over the layer. P0 grow in output pixels (> 0 spreads what shows,
// < 0 shrinks it), P1 invert, P2 / P3 offset in % of the layer's width / height (positive moves the mask right / down;
// what it uncovers is hidden). The builder's order: inverted, grown, then moved. Growing by R texels (R = |P0| times
// PixelScale, rounded, at least 1) takes the most (or least) over up to 8 rings, evenly spaced out to R, of the eight
// square steps: the builder's square exactly along rows, columns and diagonals, and for R up to 8 at every ring.
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

// Rounded & Feathered Edges (slot 15): P0 corner radius in % of the layer's shorter side, P1 soft edges, P2 their width
// in output pixels (P2 times PixelScale). The builder's shape (BuildLayerMask), on this texture's pixels: how far a
// pixel lies inside a rectangle rounded by max(corner, feather), over the feather's width (one pixel of anti-aliasing
// when not feathered). The host sends P1 = 0 when the layer has a mask: the mask's outline is softened instead.
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

// Curves (slot 19): Aux a 256 x 1 table, the preset then the master curve baked by the host into R, G and B.
float4 PSToneCurve(VsOut i) : SV_Target
{
    float4 c = Unpremul(Here(i));
    float3 u = saturate(c.rgb) * (255.0 / 256.0) + 0.5 / 256.0;
    float r = Aux.SampleLevel(LinearClamp, float2(u.r, 0.5), 0).r;
    float g = Aux.SampleLevel(LinearClamp, float2(u.g, 0.5), 0).g;
    float b = Aux.SampleLevel(LinearClamp, float2(u.b, 0.5), 0).b;
    return Premul(saturate(float3(r, g, b)), c.a);
}

// Box blur (slot 20), one direction (Params.y) of 2r + 1 equal taps, r = Params.z texels, on premultiplied values. The
// host works out r and runs it across, down, across, down (boxblur's two passes, or the card's Passes); shadows, the
// blurred background and blur dissolves use it too. A radius with a fraction f (the blurred background's continuous radius,
// 5.0-Release) takes one more tap at each end weighing f: from one whole radius to the next, the box grows smoothly.
float4 PSBoxBlur(VsOut i) : SV_Target
{
    float radius = clamp(Params.z, 0, MaxRadius);
    int r = (int)radius;
    float f = r < MaxRadius ? radius - r : 0;
    if (r < 1 && f < 0.001)
        return Here(i);

    float2 step = PassStep();
    float4 sum = float4(0, 0, 0, 0);
    [loop] for (int n = 0; n <= 2 * MaxRadius; n++)
    {
        if (n > 2 * r)
            break;
        sum += SampleAt(i.Uv, step * (n - r));
    }

    if (f >= 0.001)
        sum += (SampleAt(i.Uv, step * -(r + 1)) + SampleAt(i.Uv, step * (r + 1))) * f;
    return sum / (2 * r + 1 + 2 * f);
}

// Sharpen (slot 21): cas (contrast adaptive sharpening) with strength P0, on each straight channel: the cross's and the
// square's minimum and maximum set how far the four neighbours are pushed away, less where the contrast is already high.
float4 PSSharpen(VsOut i) : SV_Target
{
    float4 c = Here(i);
    if (P0 < 0.005)
        return c;

    float3 a = StraightAt(i.Uv, float2(-1, -1));
    float3 b = StraightAt(i.Uv, float2(0, -1));
    float3 cc = StraightAt(i.Uv, float2(1, -1));
    float3 d = StraightAt(i.Uv, float2(-1, 0));
    float3 e = StraightAt(i.Uv, float2(0, 0));
    float3 f = StraightAt(i.Uv, float2(1, 0));
    float3 g = StraightAt(i.Uv, float2(-1, 1));
    float3 h = StraightAt(i.Uv, float2(0, 1));
    float3 k = StraightAt(i.Uv, float2(1, 1));

    float3 mn = min(min(min(d, e), f), min(b, h));
    float3 mn2 = min(min(min(mn, a), cc), min(g, k));
    float3 mx = max(max(max(d, e), f), max(b, h));
    float3 mx2 = max(max(max(mx, a), cc), max(g, k));
    mn += mn2;
    mx += mx2;

    float3 amp = sqrt(saturate(min(mn, 2 - mx) / max(mx, 1e-6)));
    float3 weight = amp / -lerp(16, 4.01, saturate(P0));
    float3 o = ((b + d + f + h) * weight + e) / (1 + 4 * weight);
    return Premul(saturate(o), c.a);
}
