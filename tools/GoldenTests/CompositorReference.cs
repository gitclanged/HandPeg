using System.Numerics;

namespace HandPegGoldenTests;

/// <summary>
/// The compositor's effect shaders (src/Compositor/Shaders/Effects.hlsl, Pass 5.0) run on the CPU: one method per entry
/// point, with the same formulas, the same taps in the same order and the same clamping, in float, so that what the GPU
/// draws can be checked against it. They should agree to well within 3/255: the GPU's own exp, pow, sin and division,
/// and its 8-bit bilinear weights, differ from System.MathF by a hair. Change both together.
/// <para>
/// Each method takes the pass's input (premultiplied; an RGBA16F texture keeps it to half precision, so give both sides
/// <see cref="RefImage.ToHalf"/>'s copy), the effect's lanes (P0..P7, then Q0..Q7 for a second packet; lanes left out
/// read 0) and the pass's other constants and textures (<see cref="RefPass"/>), and returns the premultiplied result, the
/// same size as the input (the shader's DstSize). Pixel (x, y) is the shader's pixel centre, uv ((x + 0.5) / width,
/// (y + 0.5) / height), and a tap n texels away is the neighbour n pixels away, clamped to the edge (PointClamp).
/// </para>
/// <para>
/// Separable passes (<see cref="MatteChoke"/>, <see cref="MatteSmooth"/>, <see cref="BoxBlur"/>) go one direction a
/// call, as on the GPU: across (Params.Y 0) then down (1), the box blur twice over. The GPU keeps what lies between two
/// passes in RGBA16F, so round it with ToHalf too.
/// </para>
/// </summary>
internal static class CompositorReference
{
    /// <summary>A picture of float RGBA, row-major, four floats a pixel: premultiplied, unless said otherwise.</summary>
    public sealed class RefImage
    {
        public int Width, Height;
        public float[] Rgba;

        public RefImage(int width, int height) : this(width, height, new float[width * height * 4])
        {
        }

        public RefImage(int width, int height, float[] rgba)
        {
            if (width < 1 || height < 1 || rgba.Length != width * height * 4)
                throw new ArgumentException($"A {width} x {height} picture needs {width * height * 4} floats, not {rgba.Length}.");
            Width = width;
            Height = height;
            Rgba = rgba;
        }

        /// <summary>The pixel at (x, y), clamped to the edge (PointClamp).</summary>
        public Vector4 Get(int x, int y)
        {
            var i = (Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)) * 4;
            return new Vector4(Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
        }

        public void Set(int x, int y, Vector4 value)
        {
            var i = (y * Width + x) * 4;
            (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]) = (value.X, value.Y, value.Z, value.W);
        }

        /// <summary>The picture filtered at uv as LinearClamp filters it: texel centres at (i + 0.5) / size, the edge held.</summary>
        public Vector4 Bilinear(float u, float v)
        {
            var (x0, x1, fx) = LinearTaps(u, Width);
            var (y0, y1, fy) = LinearTaps(v, Height);
            return Lerp(Lerp(Get(x0, y0), Get(x1, y0), fx), Lerp(Get(x0, y1), Get(x1, y1), fx), fy);
        }

        /// <summary>A copy with every value as an RGBA16F texture keeps it.</summary>
        public RefImage ToHalf()
        {
            var values = new float[Rgba.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = (float)(Half)Rgba[i];
            return new RefImage(Width, Height, values);
        }

        /// <summary>The largest difference in any channel of any pixel from another picture the same size.</summary>
        public float MaxDifference(RefImage other)
        {
            if (other.Width != Width || other.Height != Height)
                throw new ArgumentException($"{Width} x {Height} against {other.Width} x {other.Height}.");
            var most = 0f;
            for (var i = 0; i < Rgba.Length; i++)
                most = MathF.Max(most, MathF.Abs(Rgba[i] - other.Rgba[i]));
            return most;
        }
    }

    /// <summary>The rest of a pass's constants (Common.hlsli's cbuffer) and textures.</summary>
    public sealed class RefPass
    {
        /// <summary>c13: X PxScale1080, Y the direction of a separable pass (0 across, 1 down), Z its radius in texels, W PixelScale (texels per output pixel).</summary>
        public Vector4 Params = new(1, 0, 0, 1);

        /// <summary>c14: X the 3D LUT's size N (PSLut3D).</summary>
        public Vector4 Params2;

        /// <summary>t1: the mask (straight RGBA 0..1, any size, stretched over the layer) or the curve (256 x 1: R, G and B each a channel's curve).</summary>
        public RefImage? Aux;

        /// <summary>
        /// t2: the 3D LUT, N^3 entries of RGBA (4 floats each), red fastest, then green, then blue, as a .cube file lists
        /// them; on the GPU, x is red, y green and z blue.
        /// </summary>
        public float[]? Lut;

        /// <summary>The same pass in another direction, with another radius (a separable pass's next step).</summary>
        public RefPass With(float direction, float radius) =>
            new() { Params = Params with { Y = direction, Z = radius }, Params2 = Params2, Aux = Aux, Lut = Lut };
    }

    // ----- Common.hlsli -----

    private const int MaxRadius = 64;
    private static readonly Vector3 Luma709 = new(0.2126f, 0.7152f, 0.0722f);

    private static float Luma(Vector3 rgb) => Vector3.Dot(rgb, Luma709);

    private static Vector4 Unpremul(Vector4 c) => c.W > 1e-5f ? new Vector4(c.X / c.W, c.Y / c.W, c.Z / c.W, c.W) : Vector4.Zero;

    private static Vector4 Premul(Vector3 rgb, float a) => new(rgb * a, a);

    private static Vector3 Rgb(Vector4 c) => new(c.X, c.Y, c.Z);

    private static float Sat(float v) => Math.Clamp(v, 0f, 1f);

    private static Vector3 Sat(Vector3 v) => Vector3.Clamp(v, Vector3.Zero, Vector3.One);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    private static Vector4 Lerp(Vector4 a, Vector4 b, float t) => a + (b - a) * t;

    private static float Max3(Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));

    private static float Min3(Vector3 v) => MathF.Min(v.X, MathF.Min(v.Y, v.Z));

    /// <summary>One axis of a linear filter at u (0..1 over size texels): the texels either side, clamped, and the second's weight.</summary>
    private static (int First, int Second, float Weight) LinearTaps(float u, int size)
    {
        var t = u * size - 0.5f;
        var floor = MathF.Floor(t);
        var i = (int)floor;
        return (Math.Clamp(i, 0, size - 1), Math.Clamp(i + 1, 0, size - 1), t - floor);
    }

    /// <summary>The lanes, padded to 16 with zeros.</summary>
    private static float[] Lanes(ReadOnlySpan<float> p)
    {
        var lanes = new float[16];
        p[..Math.Min(p.Length, lanes.Length)].CopyTo(lanes);
        return lanes;
    }

    /// <summary>Every pixel of the result, from its position.</summary>
    private static RefImage Map(RefImage input, Func<int, int, Vector4> pixel)
    {
        var output = new RefImage(input.Width, input.Height);
        for (var y = 0; y < input.Height; y++)
        {
            for (var x = 0; x < input.Width; x++)
                output.Set(x, y, pixel(x, y));
        }

        return output;
    }

    /// <summary>Effects.hlsl's StraightAt: a tap's straight colour, held to 0..1.</summary>
    private static Vector3 StraightAt(RefImage input, int x, int y) => Sat(Rgb(Unpremul(input.Get(x, y))));

    /// <summary>A separable pass's step (Params.y) and radius (Params.z, rounded, held to 0..64).</summary>
    private static (int Dx, int Dy, int Radius) Separable(RefPass pass) =>
        (pass.Params.Y > 0.5f ? 0 : 1, pass.Params.Y > 0.5f ? 1 : 0, Math.Clamp((int)(pass.Params.Z + 0.5f), 0, MaxRadius));

    // ----- Look -----

    /// <summary>PSFlip (slot 2): P0 left to right, P1 upside down.</summary>
    public static RefImage Flip(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        return Map(input, (x, y) => input.Get(l[0] > 0.5f ? input.Width - 1 - x : x, l[1] > 0.5f ? input.Height - 1 - y : y));
    }

    /// <summary>PSLut3D (slot 3): <see cref="RefPass.Lut"/>, N = Params2.X, trilinear.</summary>
    public static RefImage Lut3D(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var n = pass.Params2.X;
        var size = (int)n;
        var lut = n < 2 ? null : pass.Lut is { } table && table.Length >= size * size * size * 4 ? table
            : throw new ArgumentException($"A {size}^3 LUT needs {size * size * size * 4} floats.");
        return Map(input, (x, y) =>
        {
            var c = Unpremul(input.Get(x, y));
            if (lut is null)
                return Premul(Rgb(c), c.W);

            var at = Sat(Rgb(c)) * ((n - 1) / n) + new Vector3(0.5f / n);
            return Premul(Sat(Rgb(Trilinear(lut, size, at))), c.W);
        });
    }

    private static Vector4 Trilinear(float[] lut, int n, Vector3 at)
    {
        var (r0, r1, fr) = LinearTaps(at.X, n);
        var (g0, g1, fg) = LinearTaps(at.Y, n);
        var (b0, b1, fb) = LinearTaps(at.Z, n);

        Vector4 Entry(int r, int g, int b)
        {
            var i = ((b * n + g) * n + r) * 4;
            return new Vector4(lut[i], lut[i + 1], lut[i + 2], lut[i + 3]);
        }

        var near = Lerp(Lerp(Entry(r0, g0, b0), Entry(r1, g0, b0), fr), Lerp(Entry(r0, g1, b0), Entry(r1, g1, b0), fr), fg);
        var far = Lerp(Lerp(Entry(r0, g0, b1), Entry(r1, g0, b1), fr), Lerp(Entry(r0, g1, b1), Entry(r1, g1, b1), fr), fg);
        return Lerp(near, far, fb);
    }

    /// <summary>PSDenoise (slot 4): P0 strength; a 5 x 5 bilateral with hqdn3d's likeness curve, weighed by alpha.</summary>
    public static RefImage Denoise(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        return Map(input, (x, y) =>
        {
            var centre = input.Get(x, y);
            var strength = MathF.Min(l[0], 20);
            if (strength < 0.05f || centre.W <= 1e-5f)
                return centre;

            var likeness = MathF.Log(0.25f) / MathF.Log(1 - strength / 255);
            var y0 = Luma(Sat(Rgb(centre) / centre.W));
            var sum = Vector4.Zero;
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var t = input.Get(x + dx, y + dy);
                    var d = MathF.Abs(Luma(Sat(Rgb(Unpremul(t)))) - y0);
                    var w = MathF.Exp(-(dx * dx + dy * dy) / 8f) * MathF.Pow(MathF.Max(1 - d, 0), likeness);
                    sum += t * w;
                }
            }

            return Premul(Rgb(sum) / sum.W, centre.W);
        });
    }

    /// <summary>PSColorGrade (slot 5): eq's P0 contrast, P1 brightness, P2 saturation, P3 gamma.</summary>
    public static RefImage ColorGrade(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        return Map(input, (x, y) =>
        {
            var c = Unpremul(input.Get(x, y));
            var rgb = Rgb(c);
            var luma = Luma(rgb);
            var v = l[0] * (luma - 0.5f) + 0.5f + l[1];
            var graded = v <= 0 ? 0 : MathF.Min(MathF.Pow(MathF.Abs(v), 1 / MathF.Max(l[3], 0.01f)), 1);
            return Premul(Sat(new Vector3(graded) + l[2] * (rgb - new Vector3(luma))), c.W);
        });
    }

    /// <summary>PSHueSaturation (slot 18): hue's P0 degrees and P1 saturation, in BT.709 YCbCr.</summary>
    public static RefImage HueSaturation(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        var angle = l[0] * (MathF.PI / 180);
        var (s, k) = (MathF.Sin(angle), MathF.Cos(angle));
        return Map(input, (x, y) =>
        {
            var c = Unpremul(input.Get(x, y));
            var luma = Luma(Rgb(c));
            var cb = (c.Z - luma) / 1.8556f;
            var cr = (c.X - luma) / 1.5748f;
            var cb2 = (cb * k - cr * s) * l[1];
            var cr2 = (cb * s + cr * k) * l[1];
            var r = luma + 1.5748f * cr2;
            var b = luma + 1.8556f * cb2;
            var g = (luma - 0.2126f * r - 0.0722f * b) / 0.7152f;
            return Premul(Sat(new Vector3(r, g, b)), c.W);
        });
    }

    private static float BalancePush(float l, float s, float m, float h)
    {
        const float a = 4, b = 0.333f, scale = 0.7f;
        return (s * Sat((b - l) * a + 0.5f) + m * Sat((l - b) * a + 0.5f) * Sat((1 - l - b) * a + 0.5f) + h * Sat((l + b - 1) * a + 0.5f)) * scale;
    }

    /// <summary>
    /// PSColorBalance (slot 6): lanes 0..7 rs gs bs rm gm bm rh gh, lane 8 (Q0) bh, lane 9 (Q1) preserve lightness. The tones
    /// weighed by the HSL lightness (max + min) / 2, and preserving, one offset on the three channels back to the original's
    /// lightness before they are held to 0..1 (5.0-AudioAnd3D, the user's Q1).
    /// </summary>
    public static RefImage ColorBalance(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        return Map(input, (x, y) =>
        {
            var c = Unpremul(input.Get(x, y));
            var v = Sat(Rgb(c));
            var light = (Max3(v) + Min3(v)) * 0.5f;
            var o = v + new Vector3(BalancePush(light, l[0], l[3], l[6]), BalancePush(light, l[1], l[4], l[7]), BalancePush(light, l[2], l[5], l[8]));
            if (l[9] > 0.5f)
                o += new Vector3(light - (Max3(o) + Min3(o)) * 0.5f);
            return Premul(Sat(o), c.W);
        });
    }

    private static Vector3 KelvinMultiplier(float kelvin)
    {
        var k = kelvin / 100;
        float r, g, b;
        if (k <= 66)
        {
            r = 1;
            g = Sat(0.39008157876901960784f * MathF.Log(k) - 0.63184144378862745098f);
        }
        else
        {
            var t = MathF.Max(k - 60, 0);
            r = Sat(1.29293618606274509804f * MathF.Pow(t, -0.1332047592f));
            g = Sat(1.12989086089529411765f * MathF.Pow(t, -0.0755148492f));
        }

        b = k >= 66 ? 1 : k <= 19 ? 0 : Sat(0.54320678911019607843f * MathF.Log(MathF.Max(k - 10, 1e-6f)) - 1.19625408914f);
        return new Vector3(r, g, b);
    }

    /// <summary>PSColorTemperature (slot 7): colortemperature's P0 kelvin, P1 mix, P2 preserve lightness.</summary>
    public static RefImage ColorTemperature(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        var multiplier = KelvinMultiplier(l[0]);
        return Map(input, (x, y) =>
        {
            var c = Unpremul(input.Get(x, y));
            var v = Rgb(c);
            var n = Lerp(v, v * multiplier, l[1]);
            var l0 = Max3(v) + Min3(v) + 1e-6f;
            var l1 = Max3(n) + Min3(n) + 1e-6f;
            n = Lerp(n, n * (l0 / l1), l[2]);
            return Premul(Sat(n), c.W);
        });
    }

    /// <summary>
    /// PSVignette (slot 8): P0 the lens angle; cos(angle * d)^4 by the distance over the half-diagonal, stretched by the
    /// aspect P1's roundness gives ((width / height)^(1 - P1 / 100)), narrowed to the outer P2 % of the way out (feather).
    /// </summary>
    public static RefImage Vignette(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        var middle = new Vector2(input.Width * 0.5f, input.Height * 0.5f);
        var aspect = MathF.Pow((float)input.Width / Math.Max(input.Height, 1), 1 - Sat(l[1] / 100));
        var scale = new Vector2(1, aspect);
        var feather = l[2] > 0 ? Math.Clamp(l[2] / 100, 0.05f, 1) : 1;
        return Map(input, (x, y) =>
        {
            var c = input.Get(x, y);
            var d = ((new Vector2(x, y) - middle) * scale).Length() / middle.Length();
            var k = MathF.Cos(l[0] * Sat((d - (1 - feather)) / feather));
            var f = d > 1 ? 0 : k * k * (k * k);
            return new Vector4(Rgb(c) * f, c.W);
        });
    }

    // ----- Key & Matte -----

    private static Vector2 KeyChroma(Vector3 rgb) =>
        new(Vector3.Dot(rgb, new Vector3(-0.16874f, -0.33126f, 0.5f)), Vector3.Dot(rgb, new Vector3(0.5f, -0.41869f, -0.08131f)));

    /// <summary>
    /// PSChromaKey (slot 10): P0..P2 the key colour, P3 similarity, P4 blend. With P4 at most 0.0001 the key is hard (in or
    /// out), and a pixel whose distance sits on the similarity can come out either way on the GPU: test with a blend.
    /// </summary>
    public static RefImage ChromaKey(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        var key = KeyChroma(Sat(new Vector3(l[0], l[1], l[2])));
        return Map(input, (x, y) =>
        {
            var diff = 0f;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var d = KeyChroma(StraightAt(input, x + dx, y + dy)) - key;
                    diff += MathF.Sqrt(Vector2.Dot(d, d) * 0.5f);
                }
            }

            diff /= 9;
            var keep = l[4] > 0.0001f ? Sat((diff - l[3]) / l[4]) : diff > l[3] ? 1 : 0;
            return input.Get(x, y) * keep;
        });
    }

    /// <summary>PSDespill (slot 11, the Chroma Key's lanes): despill with mix P5, expand 0; the spill comes off the screen's own channel (Q2).</summary>
    public static RefImage Despill(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        var mix = Sat(l[5]);
        var blue = l[2] > l[1];
        return Map(input, (x, y) =>
        {
            var c = Unpremul(input.Get(x, y));
            var v = Sat(Rgb(c));
            if (blue)
                v.Z = MathF.Max(v.Z - MathF.Max(v.Z - (v.X * mix + v.Y * (1 - mix)), 0), 0);
            else
                v.Y = MathF.Max(v.Y - MathF.Max(v.Y - (v.X * mix + v.Z * (1 - mix)), 0), 0);
            return Premul(v, c.W);
        });
    }

    /// <summary>PSMatteChoke (slot 12), one direction: P0 &gt; 0 erodes, &lt; 0 dilates, over Params.Z texels (at least 1).</summary>
    public static RefImage MatteChoke(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        var (dx, dy, radius) = Separable(pass);
        var r = Math.Max(radius, 1);
        var erode = l[0] > 0;
        return Map(input, (x, y) =>
        {
            var c = input.Get(x, y);
            if (MathF.Abs(l[0]) < 0.5f)
                return c;

            var best = c.W;
            var from = c;
            for (var n = 1; n <= r; n++)
            {
                var before = input.Get(x - dx * n, y - dy * n);
                var after = input.Get(x + dx * n, y + dy * n);
                if (erode)
                {
                    best = MathF.Min(best, MathF.Min(before.W, after.W));
                    continue;
                }

                if (before.W > best)
                    (best, from) = (before.W, before);
                if (after.W > best)
                    (best, from) = (after.W, after);
            }

            return Premul(Rgb(Unpremul(c.W > 1e-5f ? c : from)), best);
        });
    }

    /// <summary>PSMatteSmooth (slot 13), one direction: alpha under Hann weights over Params.Z texels either side.</summary>
    public static RefImage MatteSmooth(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var (dx, dy, r) = Separable(pass);
        return Map(input, (x, y) =>
        {
            var c = input.Get(x, y);
            if (r < 1)
                return c;

            var total = 0f;
            var sum = Vector4.Zero;
            for (var n = 0; n <= 2 * r; n++)
            {
                var s = MathF.Sin(3.14159265f * (n + 1) / (2 * r + 2));
                var w = s * s;
                sum += input.Get(x + dx * (n - r), y + dy * (n - r)) * w;
                total += w;
            }

            var a = sum.W / total;
            var straight = c.W > 1e-5f ? Rgb(c) / c.W : sum.W > 1e-5f ? Rgb(sum) / sum.W : Vector3.Zero;
            return Premul(straight, a);
        });
    }

    private static readonly Vector2[] MaskRing =
        [new(1, 0), new(-1, 0), new(0, 1), new(0, -1), new(1, 1), new(-1, 1), new(1, -1), new(-1, -1)];

    private static float MaskAt(RefImage mask, Vector2 uv, bool invert)
    {
        var m = mask.Bilinear(uv.X, uv.Y);
        var v = Luma(Rgb(m)) * m.W;
        return invert ? 1 - v : v;
    }

    /// <summary>
    /// PSLayerMask (slot 14): <see cref="RefPass.Aux"/> the mask (straight). P0 grow in output pixels (times Params.W),
    /// P1 invert, P2 / P3 offset in % of the width / height.
    /// </summary>
    public static RefImage LayerMask(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        var mask = pass.Aux ?? throw new ArgumentException("The mask pass needs its mask (Aux).");
        var invert = l[1] > 0.5f;
        var offset = new Vector2(Math.Clamp(l[2], -50, 50), Math.Clamp(l[3], -50, 50)) / 100;
        var texel = new Vector2(1f / input.Width, 1f / input.Height);
        var grow = Math.Clamp(l[0], -40, 40);
        return Map(input, (x, y) =>
        {
            var at = new Vector2((x + 0.5f) / input.Width, (y + 0.5f) / input.Height) - offset;
            if (at.X < 0 || at.Y < 0 || at.X > 1 || at.Y > 1)
                return Vector4.Zero;

            var m = MaskAt(mask, at, invert);
            if (grow != 0)
            {
                var reach = MathF.Max(MathF.Round(MathF.Abs(grow) * pass.Params.W), 1);
                var rings = MathF.Min(reach, 8);
                for (var j = 1; j <= 8 && j <= rings; j++)
                {
                    var span = reach * j / rings * texel;
                    foreach (var step in MaskRing)
                    {
                        var v = MaskAt(mask, at + step * span, invert);
                        m = grow > 0 ? MathF.Max(m, v) : MathF.Min(m, v);
                    }
                }
            }

            return input.Get(x, y) * m;
        });
    }

    /// <summary>
    /// PSCornerFeather (slot 15): P0 corner radius in % of the shorter side, P1 soft edges, P2 their width in output
    /// pixels (times Params.W): the builder's rounded rectangle on this picture's pixels.
    /// </summary>
    public static RefImage CornerFeather(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        var size = new Vector2(input.Width, input.Height);
        var shorter = MathF.Min(size.X, size.Y);
        var limit = MathF.Max(MathF.Floor(shorter / 2) - 1, 1);
        var corner = MathF.Min(MathF.Round(Math.Clamp(l[0], 0, 50) / 100 * shorter), limit);
        var feather = l[1] > 0.5f ? Math.Clamp(MathF.Round(l[2] * pass.Params.W), 1, limit) : 0;
        var radius = MathF.Max(corner, feather);
        var ramp = MathF.Max(feather, 1);
        return Map(input, (x, y) =>
        {
            var c = input.Get(x, y);
            if (corner == 0 && feather == 0)
                return c;

            var at = new Vector2(x, y);
            var beyond = Vector2.Max(new Vector2(radius) - at, Vector2.Zero) + Vector2.Max(at - (size - new Vector2(1 + radius)), Vector2.Zero);
            return c * Sat((radius + 0.5f - beyond.Length()) / ramp);
        });
    }

    /// <summary>PSToneCurve (slot 19): <see cref="RefPass.Aux"/> the 256 x 1 curve, R, G and B each a channel's.</summary>
    public static RefImage ToneCurve(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var curve = pass.Aux ?? throw new ArgumentException("The curve pass needs its curve (Aux, 256 x 1).");
        return Map(input, (x, y) =>
        {
            var c = Unpremul(input.Get(x, y));
            var u = Sat(Rgb(c)) * (255f / 256f) + new Vector3(0.5f / 256f);
            var r = curve.Bilinear(u.X, 0.5f).X;
            var g = curve.Bilinear(u.Y, 0.5f).Y;
            var b = curve.Bilinear(u.Z, 0.5f).Z;
            return Premul(Sat(new Vector3(r, g, b)), c.W);
        });
    }

    /// <summary>
    /// PSBoxBlur (slot 20), one direction: 2r + 1 equal taps, r = Params.Z texels, on premultiplied values; a radius with a
    /// fraction f takes one more tap at each end weighing f (5.0-Release: the blurred background's continuous radius).
    /// </summary>
    public static RefImage BoxBlur(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var (dx, dy, _) = Separable(pass);
        var radius = Math.Clamp(pass.Params.Z, 0, MaxRadius);
        var r = (int)radius;
        var f = r < MaxRadius ? radius - r : 0;
        return Map(input, (x, y) =>
        {
            if (r < 1 && f < 0.001f)
                return input.Get(x, y);

            var sum = Vector4.Zero;
            for (var n = 0; n <= 2 * r; n++)
                sum += input.Get(x + dx * (n - r), y + dy * (n - r));
            if (f >= 0.001f)
                sum += (input.Get(x - dx * (r + 1), y - dy * (r + 1)) + input.Get(x + dx * (r + 1), y + dy * (r + 1))) * f;
            return sum / (2 * r + 1 + 2 * f);
        });
    }

    /// <summary>Core.hlsl's PSDim: the straight colour with <paramref name="amount"/> added to every channel, held to 0..1.</summary>
    public static RefImage Dim(RefImage input, float amount) => Map(input, (x, y) =>
    {
        var c = Unpremul(input.Get(x, y));
        return Premul(Sat(Rgb(c) + new Vector3(amount)), c.W);
    });

    /// <summary>PSSharpen (slot 21): cas with strength P0, on each straight channel.</summary>
    public static RefImage Sharpen(RefImage input, ReadOnlySpan<float> p, RefPass pass)
    {
        var l = Lanes(p);
        var strength = -Lerp(16f, 4.01f, Sat(l[0]));
        return Map(input, (x, y) =>
        {
            var c = input.Get(x, y);
            if (l[0] < 0.005f)
                return c;

            var a = StraightAt(input, x - 1, y - 1);
            var b = StraightAt(input, x, y - 1);
            var cc = StraightAt(input, x + 1, y - 1);
            var d = StraightAt(input, x - 1, y);
            var e = StraightAt(input, x, y);
            var f = StraightAt(input, x + 1, y);
            var g = StraightAt(input, x - 1, y + 1);
            var h = StraightAt(input, x, y + 1);
            var k = StraightAt(input, x + 1, y + 1);

            var mn = Vector3.Min(Vector3.Min(Vector3.Min(d, e), f), Vector3.Min(b, h));
            var mn2 = Vector3.Min(Vector3.Min(Vector3.Min(mn, a), cc), Vector3.Min(g, k));
            var mx = Vector3.Max(Vector3.Max(Vector3.Max(d, e), f), Vector3.Max(b, h));
            var mx2 = Vector3.Max(Vector3.Max(Vector3.Max(mx, a), cc), Vector3.Max(g, k));
            mn += mn2;
            mx += mx2;

            var amp = Vector3.SquareRoot(Sat(Vector3.Min(mn, new Vector3(2) - mx) / Vector3.Max(mx, new Vector3(1e-6f))));
            var weight = amp / strength;
            var o = ((b + d + f + h) * weight + e) / (Vector3.One + 4 * weight);
            return Premul(Sat(o), c.W);
        });
    }
}
