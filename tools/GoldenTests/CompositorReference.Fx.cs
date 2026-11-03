using System.Numerics;

namespace HandPegGoldenTests;

// The game-engine effects of Pass 5.1-GpuFx on the processor: Light.hlsl, entry point for entry point, and the host's passes round them (SceneRenderer.Fx.cs). Slots 22 (its
// sub-frames) and 24 to 25 are compared with these; what is procedural (26 to 29) is held to what it must always do, in CompositorParityTests.Fx.cs, as the Thanos Snap is.
internal static partial class CompositorReference
{
    private const float FilmGamma = 2.2f;

    private static Vector3 ToLinear(Vector3 c) => new(MathF.Pow(MathF.Abs(c.X), FilmGamma), MathF.Pow(MathF.Abs(c.Y), FilmGamma), MathF.Pow(MathF.Abs(c.Z), FilmGamma));

    private static Vector3 ToFilm(Vector3 c) => new(MathF.Pow(MathF.Abs(c.X), 1 / FilmGamma), MathF.Pow(MathF.Abs(c.Y), 1 / FilmGamma), MathF.Pow(MathF.Abs(c.Z), 1 / FilmGamma));

    /// <summary>A pass drawn at a size of its own: every pixel from its uv.</summary>
    private static RefImage Drawn(int width, int height, Func<float, float, Vector4> pixel)
    {
        var output = new RefImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                output.Set(x, y, pixel((x + 0.5f) / width, (y + 0.5f) / height));
        }

        return output.ToHalf();
    }

    /// <summary>SceneRenderer.KawaseShape.</summary>
    public static (int Levels, float Offset) KawaseShape(float reach, int width, int height)
    {
        reach = MathF.Max(reach, 1);
        var most = Math.Clamp((int)MathF.Floor(MathF.Log2(MathF.Max(Math.Min(width, height), 8) / 4f)), 1, 6);
        var levels = Math.Clamp((int)MathF.Ceiling(MathF.Log2(reach)), 1, most);
        return (levels, Math.Clamp(reach / (1 << levels), 0.5f, 2));
    }

    private static int Half(int side) => Math.Max(side / 2, 2);

    /// <summary>SceneRenderer.Kawase: PSKawaseDown so many times, then PSKawaseUp back to half the source's size.</summary>
    public static RefImage Kawase(RefImage source, float reach)
    {
        var (levels, offset) = KawaseShape(reach, source.Width, source.Height);
        var chain = new List<RefImage>();
        var input = source;
        for (var level = 0; level < levels; level++)
        {
            var (from, tx, ty) = (input, 1f / input.Width * offset, 1f / input.Height * offset);
            input = Drawn(Half(from.Width), Half(from.Height), (u, v) => (from.Bilinear(u, v) * 4 + from.Bilinear(u - tx, v - ty) + from.Bilinear(u + tx, v + ty)
                + from.Bilinear(u + tx, v - ty) + from.Bilinear(u - tx, v + ty)) / 8);
            chain.Add(input);
        }

        for (var level = levels - 1; level >= 1; level--)
        {
            var (from, target) = (chain[level], chain[level - 1]);
            var (tx, ty) = (1f / from.Width * offset, 1f / from.Height * offset);
            chain[level - 1] = Drawn(target.Width, target.Height, (u, v) => (from.Bilinear(u - 2 * tx, v) + from.Bilinear(u + 2 * tx, v) + from.Bilinear(u, v - 2 * ty) + from.Bilinear(u, v + 2 * ty)
                + (from.Bilinear(u - tx, v + ty) + from.Bilinear(u + tx, v + ty) + from.Bilinear(u + tx, v - ty) + from.Bilinear(u - tx, v - ty)) * 2) / 12);
        }

        return chain[0];
    }

    /// <summary>PSGlowBright, drawn at the size given.</summary>
    public static RefImage Highlights(RefImage input, float threshold, int width, int height) => Drawn(width, height, (u, v) =>
    {
        var c = Unpremul(input.Bilinear(u, v));
        var lin = ToLinear(Sat(Rgb(c)));
        var peak = Max3(lin);
        var limit = MathF.Pow(MathF.Abs(threshold), FilmGamma);
        var knee = limit * 0.5f + 1e-4f;
        var soft = Math.Clamp(peak - limit + knee, 0, 2 * knee);
        soft = soft * soft / (4 * knee);
        var kept = MathF.Max(soft, peak - limit) / MathF.Max(peak, 1e-4f);
        return new Vector4(lin * kept * c.W, c.W);
    });

    private static Vector4 AddLight(Vector4 picture, Vector3 light)
    {
        var c = Unpremul(picture);
        var total = ToLinear(Sat(Rgb(c))) * c.W + light;
        var a = Sat(c.W + Luma(light));
        return Premul(ToFilm(Sat(total / MathF.Max(a, 1e-4f))), a);
    }

    /// <summary>SceneRenderer.Glow: the highlights at half the size, the pyramid, PSGlowMix.</summary>
    public static RefImage Glow(RefImage input, ReadOnlySpan<float> lanes, float pxScale1080)
    {
        var p = Lanes(lanes);
        var bright = Highlights(input, p[1], Half(input.Width), Half(input.Height));
        var blurred = Kawase(bright, MathF.Pow(2, Math.Clamp(p[2], 1, 8)) * pxScale1080 / 2);
        return Map(input, (x, y) =>
        {
            var c = input.Get(x, y);
            var glow = Rgb(blurred.Bilinear((x + 0.5f) / input.Width, (y + 0.5f) / input.Height)) * p[0];
            glow = Lerp(glow, Luma(glow) * new Vector3(p[4], p[5], p[6]) * 1.5f, Sat(p[3]));
            return Lerp(c, AddLight(c, glow), Sat(p[7] / 100));
        });
    }

    /// <summary>PSShockwave, with no shake (its noise is checked by what it does).</summary>
    public static RefImage Shockwave(RefImage input, ReadOnlySpan<float> lanes, float pxScale1080)
    {
        var p = Lanes(lanes);
        var (width, height) = ((float)input.Width, (float)input.Height);
        return Map(input, (x, y) =>
        {
            var (u, v) = ((x + 0.5f) / width, (y + 0.5f) / height);
            var through = p[0];
            var fading = (1 - through) * (1 - through);
            var aspect = width / height;
            var (px, py) = ((u - p[1] / 100) * aspect, v - p[2] / 100);
            var d = MathF.Sqrt(px * px + py * py);
            var left = 1 - through;
            var radius = MathF.Max((1 - left * left * left) * p[4] / 100, 1e-3f);
            var ring = MathF.Max(radius * p[5] / 100, 1e-3f);
            var away = (d - radius) / ring;
            var band = MathF.Exp(-away * away);
            var (tx, ty) = d > 1e-5f ? (px / d, py / d) : (0, 0);
            var bend = MathF.Sin(Math.Clamp(away, -1.5f, 1.5f) * 2.0944f) * band * (p[3] * pxScale1080 / height) * fading;
            var (bx, by) = (tx * bend / aspect, ty * bend);
            var c = new Vector4(input.Bilinear(u - bx * 1.06f, v - by * 1.06f).X, input.Bilinear(u - bx, v - by).Y, input.Bilinear(u - bx * 0.94f, v - by * 0.94f).Z, input.Bilinear(u - bx, v - by).W);
            var shine = band * fading * 0.18f * c.W * Sat(p[3] / 60);
            return c + new Vector4(shine, shine, shine, 0);
        });
    }

    /// <summary>SceneRenderer.Lens's anamorphic half: the highlights, the streaks' passes, PSAnamorphic.</summary>
    public static RefImage Anamorphic(RefImage input, ReadOnlySpan<float> lanes)
    {
        var p = Lanes(lanes);
        var streak = Highlights(input, p[1], Half(input.Width), Math.Max(input.Height / 4, 2));
        var stride = 0.5f + Sat(p[2]) * 1.5f;
        for (var pass = 0; pass < 3; pass++, stride *= 3)
        {
            var (from, step) = (streak, stride / streak.Width);
            streak = Drawn(from.Width, from.Height, (u, v) => (from.Bilinear(u, v) * 4 + (from.Bilinear(u - step, v) + from.Bilinear(u + step, v)) * 3
                + (from.Bilinear(u - 2 * step, v) + from.Bilinear(u + 2 * step, v)) * 2 + from.Bilinear(u - 3 * step, v) + from.Bilinear(u + 3 * step, v)) / 16);
        }

        return Map(input, (x, y) =>
        {
            var (u, v) = ((x + 0.5f) / input.Width, (y + 0.5f) / input.Height);
            var (dx, dy) = (u - 0.5f, v - 0.5f);
            var round = dx * dx + dy * dy;
            var barrel = Sat(p[6]) * 0.16f;
            var bent = (1 + barrel * round) / (1 + barrel * 0.5f);
            var fringe = Sat(p[6]) * 0.012f * round;
            Vector4 At(float s) => input.Bilinear(0.5f + dx * s, 0.5f + dy * s);
            var c = new Vector4(At(bent * (1 + fringe)).X, At(bent).Y, At(bent * (1 - fringe)).Z, At(bent).W);
            return AddLight(c, Luma(Rgb(streak.Bilinear(u, v))) * new Vector3(p[3], p[4], p[5]) * p[0]);
        });
    }

    /// <summary>
    /// PSRimLight on a picture with <paramref name="room"/> texels round it, the distances taken exactly (jump flooding finds the same, or a hair more: what the comparison proves).
    /// The nearest pixel inside and outside the shape (alpha from a half) is looked for over the room's reach.
    /// </summary>
    public static RefImage RimLight(RefImage input, ReadOnlySpan<float> lanes, int room, float pixelScale)
    {
        var p = Lanes(lanes);
        var (width, height) = (input.Width + 2 * room, input.Height + 2 * room);
        bool Shape(int x, int y) => x >= room && y >= room && x < room + input.Width && y < room + input.Height && input.Get(x - room, y - room).W >= 0.5f;
        var output = new RefImage(width, height);
        var colour = new Vector3(p[0], p[1], p[2]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var inside = Shape(x, y);
                var nearest = float.MaxValue;
                for (var dy = -room; dy <= room; dy++)
                {
                    for (var dx = -room; dx <= room; dx++)
                    {
                        var (tx, ty) = (x + dx, y + dy);
                        if (tx >= 0 && ty >= 0 && tx < width && ty < height && Shape(tx, ty) != inside)
                            nearest = MathF.Min(nearest, dx * dx + dy * dy);
                    }
                }

                var signedAway = inside ? -MathF.Sqrt(nearest) : MathF.Sqrt(nearest);
                var half = MathF.Max(p[3] * pixelScale, 0.5f) * 0.5f;
                var reach = MathF.Max(p[4] * pixelScale, 0.5f);
                var line = 1 - Smoothstep(half - 0.75f, half + 0.75f, MathF.Abs(signedAway));
                var glow = signedAway >= 0 ? MathF.Exp(-3 * signedAway / reach) : Sat(p[5 + 1]) * MathF.Exp(-6 * -signedAway / reach);
                var light = (line + glow * (1 - line) * 0.8f) * p[5];
                var c = x >= room && y >= room && x < room + input.Width && y < room + input.Height ? input.Get(x - room, y - room) : Vector4.Zero;
                output.Set(x, y, new Vector4(Rgb(c) + colour * light, Sat(c.W + Sat(line * p[5]))));
            }
        }

        return output;
    }

    private static float Smoothstep(float from, float to, float value)
    {
        var t = Sat((value - from) / (to - from));
        return t * t * (3 - 2 * t);
    }
}
