using System.Numerics;

namespace HandPegGoldenTests;

/// <summary>
/// The social cuts (src/Compositor/Shaders/SocialCuts.hlsl, Pass 5.2.1, slots 70-73) on the CPU: the same taps, in the same order,
/// sampled as LinearBorder samples (outside the picture transparent black). Lanes as the shader reads them: P0 how much of the clip
/// is there, P3 how far through the fade, P4 1 for a fade in.
/// </summary>
internal static partial class CompositorReference
{
    private const float Pi = 3.14159265f;

    /// <summary>The picture filtered at uv as LinearBorder filters it: texel centres at (i + 0.5) / size, transparent black outside.</summary>
    public static Vector4 BilinearBorder(RefImage image, float u, float v)
    {
        var (x0, fx) = BorderTaps(u, image.Width);
        var (y0, fy) = BorderTaps(v, image.Height);
        Vector4 At(int x, int y) => x < 0 || y < 0 || x >= image.Width || y >= image.Height ? Vector4.Zero : image.Get(x, y);
        return Lerp(Lerp(At(x0, y0), At(x0 + 1, y0), fx), Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
    }

    private static (int First, float Weight) BorderTaps(float u, int size)
    {
        var t = u * size - 0.5f;
        var floor = MathF.Floor(t);
        return ((int)floor, t - floor);
    }

    private static Vector2 Uv(RefImage input, int x, int y) => new((x + 0.5f) / input.Width, (y + 0.5f) / input.Height);

    /// <summary>PSWhipPan (slot 70): in from the right (away to the left), smeared across by up to 0.35 of its width at the fastest.</summary>
    public static RefImage WhipPan(RefImage input, ReadOnlySpan<float> lanes)
    {
        var p = Lanes(lanes);
        var shift = (p[4] > 0.5f ? 1 : -1) * (1 - Sat(p[0]));
        var smear = 0.35f * MathF.Sin(Pi * Sat(p[3]));
        const int taps = 32;
        return Map(input, (x, y) =>
        {
            var uv = Uv(input, x, y);
            var sum = Vector4.Zero;
            for (var k = 0; k < taps; k++)
                sum += BilinearBorder(input, uv.X - shift + ((k + 0.5f) / taps - 0.5f) * smear, uv.Y);
            return sum / taps;
        });
    }

    /// <summary>PSZoomWarp (slot 71): zoomed about its middle by 1 + 2 x what is not there, streaked by 0.3 of that.</summary>
    public static RefImage ZoomWarp(RefImage input, ReadOnlySpan<float> lanes)
    {
        var p = Lanes(lanes);
        var away = 1 - Sat(p[0]);
        var (zoom, streak) = (1 + 2 * away, 0.3f * away);
        const int taps = 24;
        return Map(input, (x, y) =>
        {
            var d = Uv(input, x, y) - new Vector2(0.5f);
            var sum = Vector4.Zero;
            for (var k = 0; k < taps; k++)
            {
                var at = new Vector2(0.5f) + d / (zoom * (1 + ((k + 0.5f) / taps - 0.5f) * streak));
                sum += BilinearBorder(input, at.X, at.Y);
            }

            return sum / taps;
        });
    }

    // SocialCuts.hlsl's GlitchHash, GlitchRandom and GlitchStep.
    private static uint GlitchHash(uint x)
    {
        x ^= x >> 16;
        x = unchecked(x * 0x7feb352du);
        x ^= x >> 15;
        x = unchecked(x * 0x846ca68bu);
        x ^= x >> 16;
        return x;
    }

    public static float GlitchRandom(uint a, uint b) => (GlitchHash(unchecked(a * 0x9e3779b9u) ^ GlitchHash(b)) >> 8) / 16777216f;

    public static uint GlitchStep(float through) => (uint)Math.Min(MathF.Floor(Sat(through) * 30), 29);

    /// <summary>PSRgbGlitch (slot 72): 18 rows, each torn across by chance, red and blue pulled apart, as much as the clip is not there.</summary>
    public static RefImage RgbGlitch(RefImage input, ReadOnlySpan<float> lanes)
    {
        var p = Lanes(lanes);
        var glitch = 1 - Sat(p[0]);
        var step = GlitchStep(p[3]);
        var split = 0.03f * glitch * (0.5f + 0.5f * GlitchRandom(step, 7));
        return Map(input, (x, y) =>
        {
            var uv = Uv(input, x, y);
            var band = (uint)Math.Min(MathF.Floor(uv.Y * 18), 17);
            var torn = GlitchRandom(band, step) > 1 - 0.6f * glitch ? (GlitchRandom(band, step + 101) - 0.5f) * 0.25f * glitch : 0;
            var at = uv.X + torn;
            var (r, g, b) = (BilinearBorder(input, at + split, uv.Y), BilinearBorder(input, at, uv.Y), BilinearBorder(input, at - split, uv.Y));
            return new Vector4(r.X, g.Y, b.Z, MathF.Max(MathF.Max(r.W, g.W), b.W));
        });
    }

    /// <summary>PSSpin (slot 73): blurred along its turn about the anchor, by up to 0.35 radians at the fastest.</summary>
    public static RefImage Spin(RefImage input, ReadOnlySpan<float> lanes, Vector2 anchor)
    {
        var p = Lanes(lanes);
        var sweep = 0.35f * MathF.Sin(Pi * Sat(p[3]));
        var size = new Vector2(input.Width, input.Height);
        const int taps = 24;
        return Map(input, (x, y) =>
        {
            var at = (Uv(input, x, y) - anchor) * size;
            var sum = Vector4.Zero;
            for (var k = 0; k < taps; k++)
            {
                var a = ((k + 0.5f) / taps - 0.5f) * sweep;
                var (c, s) = (MathF.Cos(a), MathF.Sin(a));
                var q = anchor + new Vector2(at.X * c - at.Y * s, at.X * s + at.Y * c) / size;
                sum += BilinearBorder(input, q.X, q.Y);
            }

            return sum / taps;
        });
    }

    /// <summary>A picture turned half a turn about its middle (a box turned 180 degrees over the whole frame lays it on so).</summary>
    public static RefImage HalfTurned(RefImage input) => Map(input, (x, y) => input.Get(input.Width - 1 - x, input.Height - 1 - y));
}
