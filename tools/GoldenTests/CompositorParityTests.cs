using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using static HandPegGoldenTests.CompositorReference;

namespace HandPegGoldenTests;

/// <summary>
/// The v5.0 GPU compositor, end to end (Pass 5.0-Core, 5.0B): scenes are written into the control plane by a real
/// CompositorClient, rendered by a real worker process (this program started again with --compositor-worker, as HandPeg
/// starts HandPeg.exe), shared back as a DXGI texture and read here through CompositorFrameReader. Every effect slot is
/// checked against CompositorReference (the same math on the processor) within 3/255; the composite stage (opacity,
/// blend modes, transitions, transform, shadow, blurred background) against its own math or its shape; a real H.264
/// clip (made with HandPeg's FFmpeg when it is installed) for colour and frame-exact seeking; the recovery after a lost
/// device and after a crash; and every slot's GPU time at 1080p. Measured numbers go to the console, never into the
/// golden file (they differ per machine).
/// </summary>
public static class CompositorParityTests
{
    private const int W = 256, H = 144, Tolerance = 3;

    public static IEnumerable<GraphResult> All()
    {
        var text = new StringBuilder("# The GPU compositor against its CPU reference (5.0-Core): every slot, composite stage, decode, recovery, timing.\n");
        var problems = new List<string>();
        try
        {
            using var client = new CompositorClient(adapter: -1, keepStandby: true);
            using var reader = new CompositorFrameReader(client);
            var harness = new Harness(client, reader, text, problems);
            harness.Effects();
            harness.Composite();
            harness.Exploded();
            harness.ExplodedDetail();
            harness.Loupe();
            harness.Decode();
            harness.Timing();
            harness.Recovery();
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("compositor-parity", text.ToString(), problems, 0);
    }

    private sealed class Harness(CompositorClient client, CompositorFrameReader reader, StringBuilder text, List<string> problems)
    {
        private void Line(FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

        // ----- Scenes -----

        private void Begin(int width = W, int height = H, int flags = FrameSceneHeader.FlagRawOutput | FrameSceneHeader.FlagExactFrames)
        {
            client.ClearScene();
            ref var scene = ref client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.Flags) = (width, height, flags);
        }

        private ref GpuLayerDescriptor Layer(int index, GpuSourceKind kind, int pattern = 1, float x = 0, float y = 0, float width = W, float height = H)
        {
            ref var layer = ref client.Layer(index);
            layer.SourceKind = (int)kind;
            layer.PatternKind = pattern;
            (layer.X, layer.Y, layer.Width, layer.Height) = (x, y, width, height);
            (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
            (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
            (layer.CropRight, layer.CropBottom) = (1, 1);
            layer.Flags = GpuLayerDescriptor.FlagVisible;
            client.Scene.ActiveLayerCount = Math.Max(client.Scene.ActiveLayerCount, index + 1);
            return ref layer;
        }

        private static void Add(ref GpuLayerDescriptor layer, GpuEffectPacket packet) => layer.Effects[layer.EffectCount++] = packet;

        /// <summary>Publishes the scene and reads the presented frame back: premultiplied RGBA floats (raw output) or opaque.</summary>
        private RefImage? Render(string what)
        {
            var sequence = client.Publish();
            if (!client.WaitForFrame(sequence, 10_000))
            {
                problems.Add($"{what}: no frame within 10 s ({client.State}: {client.Message})");
                return null;
            }

            if (!reader.TryRead(out var bgra, out var width, out var height))
            {
                problems.Add($"{what}: the frame could not be read back");
                return null;
            }

            var image = new RefImage(width, height);
            for (var i = 0; i < width * height; i++)
                (image.Rgba[i * 4], image.Rgba[i * 4 + 1], image.Rgba[i * 4 + 2], image.Rgba[i * 4 + 3]) =
                    (bgra[i * 4 + 2] / 255f, bgra[i * 4 + 1] / 255f, bgra[i * 4] / 255f, bgra[i * 4 + 3] / 255f);
            return image;
        }

        /// <summary>Compares in 8-bit steps (the output is BGRA8); records the case either way.</summary>
        private void Expect(string name, RefImage? gpu, RefImage expected)
        {
            if (gpu is null)
                return;
            var worst = 0;
            var at = (X: 0, Y: 0);
            for (var y = 0; y < expected.Height; y++)
            {
                for (var x = 0; x < expected.Width; x++)
                {
                    var (g, e) = (gpu.Get(x, y), expected.Get(x, y));
                    var d = (int)MathF.Max(MathF.Max(Steps(g.X, e.X), Steps(g.Y, e.Y)), MathF.Max(Steps(g.Z, e.Z), Steps(g.W, e.W)));
                    if (d > worst)
                        (worst, at) = (d, (x, y));
                }
            }

            Line($"  {name}: within {Tolerance}/255 of the reference");
            Console.WriteLine($"        {name}: worst {worst}/255");
            if (worst > Tolerance)
                problems.Add($"{name}: {worst}/255 off at ({at.X}, {at.Y}): GPU {Show(gpu.Get(at.X, at.Y))}, reference {Show(expected.Get(at.X, at.Y))}");
        }

        private static float Steps(float gpu, float reference) => MathF.Abs(gpu * 255 - MathF.Round(Math.Clamp(reference, 0, 1) * 255));

        private static string Show(Vector4 c) => string.Create(CultureInfo.InvariantCulture, $"({c.X * 255:0},{c.Y * 255:0},{c.Z * 255:0},{c.W * 255:0})");

        // ----- The source patterns, as PSPattern draws them -----

        public static RefImage Pattern(int kind, int width = W, int height = H)
        {
            var image = new RefImage(width, height);
            Vector3[] bars = [new(1, 1, 1), new(1, 1, 0), new(0, 1, 1), new(0, 1, 0), new(1, 0, 1), new(1, 0, 0), new(0, 0, 1), new(0, 0, 0)];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (px, py) = (x + 0.5f, y + 0.5f);
                    var (u, v) = (px / width, py / height);
                    Vector3 rgb;
                    var a = 1f;
                    switch (kind)
                    {
                        case 0:
                            rgb = bars[Math.Min((int)(u * 8), 7)] * (v < 0.75f ? 0.75f : 1f);
                            break;
                        case 1:
                            rgb = new Vector3(u, v, 1 - u * 0.5f - v * 0.5f);
                            break;
                        case 2:
                            rgb = (((int)(px / 32) + (int)(py / 32)) & 1) != 0 ? new Vector3(0.9f, 0.85f, 0.8f) : new Vector3(0.15f, 0.2f, 0.25f);
                            break;
                        default:
                            var (dx, dy) = (u - 0.5f, v - 0.5f);
                            var r = MathF.Sqrt(dx * (float)width / height * dx * (float)width / height + dy * dy);
                            var angle = MathF.Atan2(dy, dx) / 6.28318530718f + 0.5f;
                            rgb = Hsv(angle, 0.8f, 0.6f + 0.4f * Frac(r * 8));
                            a = Math.Clamp((0.45f - r) * 20, 0, 1);
                            break;
                    }

                    image.Set(x, y, new Vector4(rgb * a, a));
                }
            }

            return image.ToHalf();
        }

        private static float Frac(float v) => v - MathF.Floor(v);

        private static Vector3 Hsv(float h, float s, float v)
        {
            var p = new Vector3(MathF.Abs(Frac(h + 1) * 6 - 3), MathF.Abs(Frac(h + 2f / 3) * 6 - 3), MathF.Abs(Frac(h + 1f / 3) * 6 - 3));
            var c = Vector3.Clamp(p - Vector3.One, Vector3.Zero, Vector3.One);
            return v * (Vector3.One + (c - Vector3.One) * s);
        }

        // ----- Zones 1-3 -----

        public void Effects()
        {
            Line($"effects (one full-frame pattern layer, {W} x {H}, each slot alone):");
            var pass = new RefPass { Params = new Vector4(W / 1080f, 0, 0, 1) };

            One("flip horizontally (2)", 1, new(GpuShaderSlot.Flip, 1, 0, 0, 0, 0, 0, 0, 0), input => Flip(input, [1, 0], pass));
            One("flip vertically (2)", 1, new(GpuShaderSlot.Flip, 0, 1, 0, 0, 0, 0, 0, 0), input => Flip(input, [0, 1], pass));
            One("denoise (4)", 3, new(GpuShaderSlot.Denoise, 6, 0, 0, 0, 0, 0, 0, 0), input => Denoise(input, [6], pass));
            One("color grade (5)", 1, new(GpuShaderSlot.ColorGrade, 1.3f, 0.05f, 1.4f, 0.9f, 0, 0, 0, 0), input => ColorGrade(input, [1.3f, 0.05f, 1.4f, 0.9f], pass));

            // Color Balance is two packets in a row.
            float[] balance = [0.2f, -0.1f, 0.05f, -0.15f, 0.1f, 0.2f, 0.1f, 0f];
            Case("color balance, preserving lightness (6)", 1, (ref GpuLayerDescriptor l) =>
            {
                Add(ref l, GpuEffectPacket.From(GpuShaderSlot.ColorBalance, balance));
                Add(ref l, new(GpuShaderSlot.ColorBalance, -0.2f, 1, 0, 0, 0, 0, 0, 0));
            }, input => ColorBalance(input, [.. balance, -0.2f, 1], pass));

            One("color temperature (7)", 1, new(GpuShaderSlot.ColorTemperature, 3200, 1, 0.5f, 0, 0, 0, 0, 0), input => ColorTemperature(input, [3200, 1, 0.5f], pass));
            One("vignette (8)", 1, new(GpuShaderSlot.Vignette, 0.9f, 100, 100, 0, 0, 0, 0, 0), input => Vignette(input, [0.9f, 100, 100], pass));
            One("vignette, frame-shaped and feathered (8)", 1, new(GpuShaderSlot.Vignette, 0.9f, 30, 60, 0, 0, 0, 0, 0), input => Vignette(input, [0.9f, 30, 60], pass));
            One("hue and saturation (18)", 1, new(GpuShaderSlot.HueSaturation, 60, 1.5f, 0, 0, 0, 0, 0, 0), input => HueSaturation(input, [60, 1.5f], pass));
            One("chroma key with despill (10, 11)", 0, new(GpuShaderSlot.ChromaKey, 0, 1, 0, 0.15f, 0.05f, 0.5f, 0, 0),
                input => Despill(ChromaKey(input, [0, 1, 0, 0.15f, 0.05f, 0.5f], pass).ToHalf(), [0, 1, 0, 0.15f, 0.05f, 0.5f], pass));

            // The key's Advanced Choke and Smooth (5.0-Release): Matte Choke's square, then Matte Smooth's Hann weights, on its transparency.
            float[] keyed = [0, 1, 0, 0.15f, 0.05f, 0, 2, 3];
            One("chroma key, choked and smoothed (10, 12, 13)", 0, GpuEffectPacket.From(GpuShaderSlot.ChromaKey, keyed),
                input => Separable(Separable(ChromaKey(input, keyed, pass).ToHalf(), 2, (i, p) => MatteChoke(i, [2], p), pass, 1), 3, (i, p) => MatteSmooth(i, [7], p), pass, 1));
            One("matte choke, eroding (12)", 3, new(GpuShaderSlot.MatteChoke, 3, 0, 0, 0, 0, 0, 0, 0), input => Separable(input, 3, (i, p) => MatteChoke(i, [3], p), pass, 1));
            One("matte choke, dilating (12)", 3, new(GpuShaderSlot.MatteChoke, -3, 0, 0, 0, 0, 0, 0, 0), input => Separable(input, 3, (i, p) => MatteChoke(i, [-3], p), pass, 1));
            One("matte smooth (13)", 3, new(GpuShaderSlot.MatteSmooth, 5, 0, 0, 0, 0, 0, 0, 0), input => Separable(input, 2, (i, p) => MatteSmooth(i, [5], p), pass, 1));
            One("rounded and feathered edges (15)", 1, new(GpuShaderSlot.CornerFeather, 20, 1, 8, 0, 0, 0, 0, 0), input => CornerFeather(input, [20, 1, 8], pass));

            // Blur: FFmpeg's box size for a 20 px radius on a 256-wide frame (the host's formula), twice each way.
            var sigma = MathF.Max(20 * W / 1080f / 2, 0.2f);
            var box = Math.Min(Math.Max((int)MathF.Round((MathF.Sqrt(6 * sigma * sigma + 1) - 1) / 2), 1), Math.Min(W, H) / 4 - 1);
            One("blur (20)", 2, new(GpuShaderSlot.BoxBlur, 20, 0, 0, 0, 0, 0, 0, 0), input => Separable(input, box, (i, p) => BoxBlur(i, [20], p), pass, 2));

            // Its Advanced values (5.0-Release): three passes, twice as wide across as down, dimmed by 10 %.
            var (across, down) = EffectRegistry.BlurSizes(20, 2, 3, W, W, H);
            One("blur, 3 passes, wider across, dimmed (20)", 2, new(GpuShaderSlot.BoxBlur, 20, 3, 2, 10, 0, 0, 0, 0),
                input => Dim(Separable(input, across, down, (i, p) => BoxBlur(i, [20], p), pass, 3), -0.1f * 256 / 219));
            One("sharpen (21)", 2, new(GpuShaderSlot.Sharpen, 0.7f, 0, 0, 0, 0, 0, 0, 0), input => Sharpen(input, [0.7f], pass));

            // Textures handed over: a LUT, a mask, a curve.
            const int n = 17;
            var lut = new float[n * n * n * 4];
            var lutBytes = new byte[lut.Length * 4];
            for (var b = 0; b < n; b++)
                for (var g = 0; g < n; g++)
                    for (var r = 0; r < n; r++)
                    {
                        var i = ((b * n + g) * n + r) * 4;
                        var (fr, fg, fb) = (r / (n - 1f), g / (n - 1f), b / (n - 1f));
                        (lut[i], lut[i + 1], lut[i + 2], lut[i + 3]) = (1 - fr * 0.8f, fg * 0.9f + 0.05f, fb * fb, 1);
                    }
            Buffer.BlockCopy(lut, 0, lutBytes, 0, lutBytes.Length);
            client.SetResource(0, GpuResourceKind.Lut3D, n, n, n, lutBytes);
            Case("LUT 3D (3)", 1, (ref GpuLayerDescriptor l) => { l.LutResourceId = 0; Add(ref l, new(GpuShaderSlot.Lut3D, 0, 0, 0, 0, 0, 0, 0, 0)); },
                input => Lut3D(input, [], new RefPass { Params = pass.Params, Params2 = new Vector4(n, 0, 0, 0), Lut = lut }));

            var (mw, mh) = (64, 36);
            var maskBytes = new byte[mw * mh * 4];
            var mask = new RefImage(mw, mh);
            for (var y = 0; y < mh; y++)
                for (var x = 0; x < mw; x++)
                {
                    var value = (byte)Math.Clamp((int)(x * 4 + 40 * MathF.Sin(y * 0.3f)), 0, 255);
                    var i = (y * mw + x) * 4;
                    (maskBytes[i], maskBytes[i + 1], maskBytes[i + 2], maskBytes[i + 3]) = (value, value, value, 255);
                    mask.Set(x, y, new Vector4(value / 255f, value / 255f, value / 255f, 1));
                }
            client.SetResource(1, GpuResourceKind.Mask2D, mw, mh, 1, maskBytes);
            Case("mask, offset (14)", 1, (ref GpuLayerDescriptor l) => { l.MaskResourceId = 1; Add(ref l, new(GpuShaderSlot.LayerMask, 0, 0, 10, -5, 0, 0, 0, 0)); },
                input => LayerMask(input, [0, 0, 10, -5], new RefPass { Params = pass.Params, Aux = mask }));
            Case("mask, grown and inverted (14)", 1, (ref GpuLayerDescriptor l) => { l.MaskResourceId = 1; Add(ref l, new(GpuShaderSlot.LayerMask, 2, 1, 0, 0, 0, 0, 0, 0)); },
                input => LayerMask(input, [2, 1, 0, 0], new RefPass { Params = pass.Params, Aux = mask }));

            var curve = new RefImage(256, 1);
            var curveFloats = new float[256 * 4];
            for (var x = 0; x < 256; x++)
            {
                var t = x / 255f;
                var s = t * t * (3 - 2 * t);
                (curveFloats[x * 4], curveFloats[x * 4 + 1], curveFloats[x * 4 + 2], curveFloats[x * 4 + 3]) = (s, MathF.Sqrt(t), t * t, t);
                curve.Set(x, 0, new Vector4(s, MathF.Sqrt(t), t * t, t));
            }
            var curveBytes = new byte[curveFloats.Length * 4];
            Buffer.BlockCopy(curveFloats, 0, curveBytes, 0, curveBytes.Length);
            client.SetResource(2, GpuResourceKind.Curve1D, 256, 1, 1, curveBytes);
            Case("tone curve (19)", 1, (ref GpuLayerDescriptor l) => { l.CurveResourceId = 2; Add(ref l, new(GpuShaderSlot.ToneCurve, 0, 0, 0, 0, 0, 0, 0, 0)); },
                input => ToneCurve(input, [], new RefPass { Params = pass.Params, Aux = curve }));
        }

        private delegate void Setup(ref GpuLayerDescriptor layer);

        private void One(string name, int pattern, GpuEffectPacket packet, Func<RefImage, RefImage> reference) =>
            Case(name, pattern, (ref GpuLayerDescriptor l) => Add(ref l, packet), reference);

        private void Case(string name, int pattern, Setup setup, Func<RefImage, RefImage> reference)
        {
            Begin();
            setup(ref Layer(0, GpuSourceKind.TestPattern, pattern));
            Expect(name, Render(name), reference(Pattern(pattern)).ToHalf());
        }

        /// <summary>A separable effect as the host runs it: across then down, so many times, kept in half floats between.</summary>
        private static RefImage Separable(RefImage input, int radius, Func<RefImage, RefPass, RefImage> step, RefPass pass, int iterations) =>
            Separable(input, radius, radius, step, pass, iterations);

        /// <summary>The same with a radius of its own down (a box blur's: a fraction weighs the outer taps).</summary>
        private static RefImage Separable(RefImage input, float radius, float down, Func<RefImage, RefPass, RefImage> step, RefPass pass, int iterations)
        {
            for (var i = 0; i < iterations; i++)
            {
                input = step(input, pass.With(0, radius)).ToHalf();
                input = step(input, pass.With(1, down)).ToHalf();
            }

            return input;
        }

        // ----- Zone 4 -----

        public void Composite()
        {
            Line($"composite stage:");
            var gradient = Pattern(1);
            var target = Pattern(3);

            Case("opacity 50 % (1)", 3, (ref GpuLayerDescriptor l) => Add(ref l, new(GpuShaderSlot.TransformComposite, 50, 0, 0, 0, 0, 0, 0, 0)),
                input => Scale(input, 0.5f));
            Case("dissolve at 40 % (64)", 3, (ref GpuLayerDescriptor l) => Add(ref l, new(GpuShaderSlot.TransitionDissolve, 0.4f, 0, 0, 0, 0.4f, 1, 0, 0)),
                input => Scale(input, 0.4f));
            Case("wipe to the right, half way (65)", 1, (ref GpuLayerDescriptor l) => Add(ref l, new(GpuShaderSlot.TransitionWipe, 0.5f, 1, 0, 0.5f, 1, 0, 0, 0)),
                input => Wiped(input, 0.5f));

            // A slide half way in from the left (P1 = 1, coming on: it comes from the side it travels away from): carried
            // Beyond = -(0 + 256) - 2 = -258 pixels times the half not yet shown, so 129 px left of its place.
            Case("slide in, half way (66)", 1, (ref GpuLayerDescriptor l) => Add(ref l, new(GpuShaderSlot.TransitionSlide, 0.5f, 1, 0, 0.5f, 1, 0, 0, 0)),
                input => Shifted(input, -129));

            // A zoom at half size about the middle, past its first fifth (no dissolve left).
            Begin();
            ref var zoomed = ref Layer(0, GpuSourceKind.SolidColor, 0);
            (zoomed.SolidR, zoomed.SolidG, zoomed.SolidB, zoomed.SolidA) = (0, 0, 1, 1);
            Add(ref zoomed, new(GpuShaderSlot.TransitionZoom, 0.5f, 0, 0, 0.5f, 1, 0, 0, 0));
            if (Render("zoom") is { } zoom)
            {
                var (inside, outside) = (zoom.Get(W / 2, H / 2), zoom.Get(W / 4 - 4, H / 4 - 4));
                Line($"  zoom at half size (67): about the box's middle");
                if (inside.Z < 0.98f || outside.W > 0.02f || zoom.Get(W / 4 + 2, H / 4 + 2).W < 0.98f)
                    problems.Add($"zoom: inside {Show(inside)}, outside {Show(outside)}");
            }

            string[] modes = ["normal", "multiply", "screen", "add", "overlay", "soft light", "hard light", "difference"];
            for (var mode = 1; mode < modes.Length; mode++)
            {
                Begin();
                Layer(0, GpuSourceKind.TestPattern, 1);
                ref var top = ref Layer(1, GpuSourceKind.TestPattern, 3);
                Add(ref top, new(GpuShaderSlot.BlendMode, mode, 0, 0, 0, 0, 0, 0, 0));
                Expect($"blend {modes[mode]} (17)", Render(modes[mode]), Blended(gradient, target, mode).ToHalf());
            }

            // The blurred background (5.0-Release): a 7.3 px radius is drawn at half the frame's size (BlurFillShrink), box-blurred
            // twice with the radius's fraction its own (3.65 texels there), dimmed, enlarged back to the frame (bilinear), and its
            // own vignette run there before it is laid on. (A test pattern is drawn to its target whole: the zoom's crop is the
            // media's, checked by gpu-scene-clip-effect.)
            Begin();
            ref var blurred = ref Layer(0, GpuSourceKind.TestPattern, 1);
            blurred.Flags |= GpuLayerDescriptor.FlagBlurFillOnly;
            (blurred.BlurFillRadius, blurred.BlurFillPasses, blurred.BlurFillDim) = (7.3f, 2, -0.1f);
            Add(ref blurred, new(GpuShaderSlot.Vignette, 0.9f, 100, 100, 0, 0, 0, 0, 0));
            var stage = new RefPass { Params = new Vector4(W / 1080f, 0, 0, 1) };
            const int shrink = 2; // SceneRenderer.BlurFillShrink: a half from 6 output pixels of radius, a quarter from 16.
            var small = Separable(Pattern(1, W / shrink / 2 * 2, H / shrink / 2 * 2), 7.3f / shrink, 7.3f / shrink, (i, p) => BoxBlur(i, [], p), stage, 2);
            var enlarged = Enlarged(Dim(small, -0.1f * 256 / 219).ToHalf(), W, H).ToHalf();
            Expect("blurred background, 7.3 px, dimmed, with its own vignette (20, 8)", Render("blurred background"), Vignette(enlarged, [0.9f, 100, 100], stage).ToHalf());

            // Shape checks: the math is the same quad, so where things land is what matters.
            Begin();
            ref var square = ref Layer(0, GpuSourceKind.SolidColor, 0, 100, 40, 40, 40);
            (square.SolidR, square.SolidG, square.SolidB, square.SolidA) = (1, 1, 1, 1);
            Add(ref square, new(GpuShaderSlot.DropShadow, 10, 135, 0, 1, 1, 0, 0, 0));
            if (Render("drop shadow") is { } shadowed)
            {
                // Lit from the upper left at 135 degrees: the shadow falls 7 px right and down.
                var inside = shadowed.Get(120, 60);
                var shadow = shadowed.Get(144, 84);
                var clear = shadowed.Get(98, 38);
                Line($"  drop shadow (16): white square, red shadow 7 px right and down, clear elsewhere");
                if (inside.X < 0.98f || inside.Y < 0.98f || shadow.X < 0.98f || shadow.Y > 0.02f || clear.W > 0.02f)
                    problems.Add($"drop shadow: inside {Show(inside)}, shadow {Show(shadow)}, clear {Show(clear)}");
            }

            Begin();
            ref var turned = ref Layer(0, GpuSourceKind.SolidColor, 0, 108, 52, 40, 40);
            (turned.SolidR, turned.SolidG, turned.SolidB, turned.SolidA, turned.Rotation) = (0, 1, 0, 1, 45);
            if (Render("rotation") is { } rotated)
            {
                // A 40 px square turned 45 degrees about its middle (128, 72): a diamond reaching 28 px each way.
                var (centre, tip, beyond, corner) = (rotated.Get(128, 72), rotated.Get(128, 72 - 26), rotated.Get(128, 72 - 31), rotated.Get(109, 53));
                Line($"  rotation 45 degrees: a diamond about the anchor");
                if (centre.Y < 0.98f || tip.Y < 0.98f || beyond.W > 0.02f || corner.W > 0.02f)
                    problems.Add($"rotation: centre {Show(centre)}, tip {Show(tip)}, beyond {Show(beyond)}, old corner {Show(corner)}");
            }

            Begin();
            ref var filled = ref Layer(0, GpuSourceKind.TestPattern, 0, 96, 0, 64, 144);
            (filled.BlurFillRadius, filled.BlurFillPasses, filled.BlurFillDim) = (20, 2, -0.1f);
            if (Render("blurred background") is { } background)
            {
                var (edge, middle) = (background.Get(4, 72), background.Get(128, 20));
                Line($"  blurred background: the frame filled behind a narrow layer");
                if (edge.W < 0.98f || middle.W < 0.98f)
                    problems.Add($"blurred background: edge {Show(edge)}, layer {Show(middle)}");
            }
        }

        // ----- The 3D exploded layer stack (5.0D) -----

        /// <summary>
        /// The 3D exploded view (FrameSceneHeader.ViewMode 1): three solid layers at three places in the stack, seen turned
        /// and from above. Every pixel away from an edge must show the layer that ExplodedCamera's own ray-plane picking finds
        /// in front there (HandPeg picks with the same maths), so the planes land where the camera says to within 2 px and
        /// in the right order. At rest (no turn, no spread, zoom 1) the view must be the flat letterboxed picture, and a flat
        /// frame drawn after the 3D ones must be the very frame drawn before them.
        /// </summary>
        /// <summary>
        /// The magnifier loupe (5.0-Verify, the user's bug 6): a red layer turned 30 degrees (its corners cut by its own box)
        /// named as the loupe's, on a letterboxed output as the player draws it. Within a few frames (the edge search is read
        /// back a frame or two later) an amber-framed square of the expected size is in a corner, holding both the layer's
        /// red and the checkerboard behind its transparent part; with no loupe named, none.
        /// </summary>
        public void Loupe()
        {
            const int cw = 320, ch = 180, ow = 640, oh = 360;
            Line($"magnifier loupe ({cw} x {ch} canvas on a {ow} x {oh} output, a red layer turned 30 degrees, 400 %):");

            RefImage? Draw(long loupe)
            {
                Begin(cw, ch, FrameSceneHeader.FlagLetterbox);
                ref var scene = ref client.Scene;
                (scene.OutputWidth, scene.OutputHeight, scene.ViewScale) = (ow, oh, 1);
                (scene.LoupeClipKey, scene.LoupeKind, scene.LoupeZoom) = (loupe, 1, 4);
                ref var layer = ref Layer(0, GpuSourceKind.SolidColor, 0, 100, 40, 120, 90);
                (layer.SolidR, layer.SolidG, layer.SolidB, layer.SolidA, layer.Rotation, layer.ClipKey) = (1, 0, 0, 1, 30, 42);
                return Render("the loupe");
            }

            RefImage? image = null;
            for (var k = 0; k < 4; k++)
                image = Draw(42);
            var none = Draw(0);
            if (image is null || none is null)
                return;

            static bool Amber(Vector4 c) => c.X > 0.9f && c.Y is > 0.6f and < 0.8f && c.Z < 0.3f;
            var (minX, minY, maxX, maxY, amber) = (int.MaxValue, int.MaxValue, -1, -1, 0);
            for (var y = 0; y < oh; y++)
            {
                for (var x = 0; x < ow; x++)
                {
                    if (!Amber(image.Get(x, y)))
                        continue;
                    amber++;
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                }
            }

            var side = (int)Math.Clamp(MathF.Round(oh * 0.36f), 120, 640);
            var (red, grey) = (0, 0);
            for (var y = minY + 3; y <= maxY - 3; y++)
            {
                for (var x = minX + 3; x <= maxX - 3; x++)
                {
                    var c = image.Get(x, y);
                    red += c.X > 0.9f && c.Y < 0.1f && c.Z < 0.1f ? 1 : 0;
                    grey += MathF.Abs(c.X - c.Y) < 0.02f && MathF.Abs(c.Y - c.Z) < 0.02f && c.X is > 0.15f and < 0.31f ? 1 : 0;
                }
            }

            var noneAmber = 0;
            for (var y = 0; y < oh; y++)
            {
                for (var x = 0; x < ow; x++)
                    noneAmber += Amber(none.Get(x, y)) ? 1 : 0;
            }

            Line($"  a framed square of the expected size in a corner, the layer's edge in it over the checkerboard; none without a loupe");
            Console.WriteLine($"        loupe: frame {minX},{minY} to {maxX},{maxY} ({amber} amber pixels, {side} px expected), {red} red and {grey} checkerboard pixels inside; {noneAmber} amber without it");
            if (amber == 0 || Math.Abs(maxX - minX + 1 - side) > 2 || Math.Abs(maxY - minY + 1 - side) > 2)
                problems.Add($"loupe: framed {minX},{minY} to {maxX},{maxY}, {amber} amber pixels (a {side} px square expected)");
            else if (red < 50 || grey < 50)
                problems.Add($"loupe: {red} red and {grey} checkerboard pixels inside (both expected: the layer's edge over the checkerboard)");
            if (noneAmber > 0)
                problems.Add($"loupe: {noneAmber} amber pixels with no loupe named");
        }

        public void Exploded()
        {
            const int cw = 320, ch = 180, ow = 400, oh = 300;
            const int raw = FrameSceneHeader.FlagRawOutput | FrameSceneHeader.FlagExactFrames;
            Line($"3D exploded layer stack ({cw} x {ch} canvas on a {ow} x {oh} output, three solid layers):");
            (float X, float Y, float W, float H, float Turn, Vector3 Rgb)[] boxes =
            [
                (20, 20, 120, 80, 0, new Vector3(1, 0, 0)),
                (110, 60, 120, 80, 0, new Vector3(0, 1, 0)),
                (200, 30, 90, 110, 20, new Vector3(0, 0, 1)),
            ];

            void Scene(int viewMode, float yaw, float pitch, float zoom, float spread, int flags)
            {
                Begin(cw, ch, flags);
                ref var scene = ref client.Scene;
                (scene.OutputWidth, scene.OutputHeight) = (ow, oh);
                (scene.ViewMode, scene.CameraYaw, scene.CameraPitch, scene.CameraZoom, scene.ExplodedZSpread) = (viewMode, yaw, pitch, zoom, spread);
                for (var k = 0; k < boxes.Length; k++)
                {
                    var b = boxes[k];
                    ref var layer = ref Layer(k, GpuSourceKind.SolidColor, 0, b.X, b.Y, b.W, b.H);
                    (layer.SolidR, layer.SolidG, layer.SolidB, layer.SolidA) = (b.Rgb.X, b.Rgb.Y, b.Rgb.Z, 1);
                    (layer.Rotation, layer.ZDepth) = (b.Turn, k);
                }
            }

            // Each box's corners on the output as a camera sees them: the band along their edges is left out of the checks.
            Vector2[][] Corners(HandPegApp.Compositor.ExplodedCamera camera) =>
            [
                .. Enumerable.Range(0, boxes.Length).Select(k => new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) }.Select(c =>
                {
                    var b = boxes[k];
                    var p = HandPegApp.Compositor.ExplodedCamera.BoxPoint(b.X, b.Y, b.W, b.H, b.Turn, 0.5f, 0.5f, c.Item1, c.Item2);
                    return camera.Project(p.X, p.Y, camera.DepthOf(k));
                }).ToArray()),
            ];

            // The layer in front at an output point, by the camera's picking maths (of planes as near, the later in the stack,
            // which is drawn over the earlier); -1 none.
            int Picked(HandPegApp.Compositor.ExplodedCamera camera, float x, float y)
            {
                var (found, nearest) = (-1, float.MaxValue);
                for (var k = 0; k < boxes.Length; k++)
                {
                    var b = boxes[k];
                    var z = camera.DepthOf(k);
                    if (camera.HitPlane(x, y, z, out var on) && HandPegApp.Compositor.ExplodedCamera.InBox(on, b.X, b.Y, b.W, b.H, b.Turn, 0.5f, 0.5f)
                        && camera.DistanceTo(z) <= nearest)
                        (found, nearest) = (k, camera.DistanceTo(z));
                }

                return found;
            }

            // Every other pixel off the edges against what the camera picks there.
            void Check(string name, RefImage? image, HandPegApp.Compositor.ExplodedCamera camera, FormattableString line)
            {
                if (image is null)
                    return;
                var corners = Corners(camera);
                var (looked, wrong, seen) = (0, 0, new int[boxes.Length]);
                string? first = null;
                for (var y = 0; y < oh; y += 2)
                {
                    for (var x = 0; x < ow; x += 2)
                    {
                        var (px, py) = (x + 0.5f, y + 0.5f);
                        if (NearEdge(new Vector2(px, py), corners, 2.5f))
                            continue;
                        var (expected, got) = (Picked(camera, px, py), Classify(image.Get(x, y)));
                        looked++;
                        if (got >= 0)
                            seen[got]++;
                        if (got != expected)
                        {
                            wrong++;
                            first ??= $"({x}, {y}): {ColorName(got)} where {ColorName(expected)} should be";
                        }
                    }
                }

                Line(line);
                Console.WriteLine($"        {name}: {looked} pixels checked, {wrong} wrong; red {seen[0]}, green {seen[1]}, blue {seen[2]}");
                if (wrong > 0)
                    problems.Add($"{name}: {wrong} of {looked} pixels are not the layer ExplodedCamera picks there, the first at {first}");
                if (seen.Any(s => s < 100))
                    problems.Add($"{name}: a plane is missing or too small (red {seen[0]}, green {seen[1]}, blue {seen[2]} pixels)");
            }

            // Turned and spread: the planes where the camera puts them, the nearer over the farther.
            const float yaw = 28, pitch = 14, zoom = 1.6f, spread = 0.2f * cw;
            Scene(1, yaw, pitch, zoom, spread, raw);
            Check("exploded", Render("exploded"), HandPegApp.Compositor.ExplodedCamera.Create(cw, ch, ow, oh, yaw, pitch, zoom, spread, boxes.Length),
                $"  turned {yaw} degrees, {pitch} up, {zoom} times the fitted distance, {spread} px apart: every pixel off the edges shows the plane picked there");

            // At rest the 3D view is the flat picture (the way in and out of 3D has no jump).
            Scene(0, 0, 0, 0, 0, raw | FrameSceneHeader.FlagLetterbox);
            var flat = Render("flat");
            var atRest = HandPegApp.Compositor.ExplodedCamera.Create(cw, ch, ow, oh, 0, 0, 1, 0, boxes.Length);
            Check("flat letterboxed", flat, atRest, $"  flat and letterboxed: each layer where the camera at rest puts it");
            Scene(1, 0, 0, 1, 0, raw);
            Check("exploded at rest", Render("exploded at rest"), atRest, $"  3D at rest (no turn, no spread, zoom 1): the flat picture");

            // The flat path is untouched by the 3D one: the same scene drawn flat again is the same frame, to the bit.
            Scene(0, 0, 0, 0, 0, raw | FrameSceneHeader.FlagLetterbox);
            if (Render("flat again") is { } again && flat is not null)
            {
                Line($"  flat again after 3D frames: the same frame, to the bit");
                if (!again.Rgba.AsSpan().SequenceEqual(flat.Rgba))
                    problems.Add("exploded: the flat frame drawn after the 3D ones differs from the one before them");
            }
        }

        /// <summary>
        /// The 3D view at full resolution and its outlines (5.0-Release, the user's directive 4). A 512 px still of stripes
        /// 8 px wide in a 64 px box, the camera close (0.3 of the fitted distance): every pixel away from a stripe's edge is
        /// the stripe's black or white (a plane drawn at the canvas's fitted size, 80 px for the 512, averaged them into grey
        /// and beats). The selected layer's outline round its box is the same, pixel for pixel, with a box blur (a multi-pass
        /// effect) of 4 px, of 12 px and with none. And while a layer above two others comes and goes, those two and the
        /// selected one's outline stay where they were, to the pixel (the stack as deep as its plan, ExplodedDepth: a caption
        /// between its cues once swung the whole stack).
        /// </summary>
        public void ExplodedDetail()
        {
            const int cw = 320, ch = 180, ow = 400, oh = 300, side = 512;
            const int raw = FrameSceneHeader.FlagRawOutput | FrameSceneHeader.FlagExactFrames;
            Line($"3D exploded view at full resolution, its outlines and its places ({cw} x {ch} canvas on a {ow} x {oh} output):");

            void Camera(float yaw, float pitch, float zoom, float spread)
            {
                Begin(cw, ch, raw);
                ref var scene = ref client.Scene;
                (scene.OutputWidth, scene.OutputHeight) = (ow, oh);
                (scene.ViewMode, scene.CameraYaw, scene.CameraPitch, scene.CameraZoom, scene.ExplodedZSpread) = (1, yaw, pitch, zoom, spread);
            }

            // Full resolution: the still's stripes against what the camera says is under each pixel.
            var stripes = new byte[side * side * 4];
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var value = (byte)(x / 8 % 2 == 0 ? 255 : 0);
                    var i = (y * side + x) * 4;
                    (stripes[i], stripes[i + 1], stripes[i + 2], stripes[i + 3]) = (value, value, value, 255);
                }
            }

            client.SetResource(5, GpuResourceKind.Still2D, side, side, 1, stripes);
            const float boxX = 128, boxY = 58, boxSide = 64, zoomIn = 0.3f;
            Camera(0, 0, zoomIn, 0);
            ref var still = ref Layer(0, GpuSourceKind.Still, 0, boxX, boxY, boxSide, boxSide);
            still.StillResourceId = 5;
            if (Render("3D close up") is { } close)
            {
                var camera = HandPegApp.Compositor.ExplodedCamera.Create(cw, ch, ow, oh, 0, 0, zoomIn, 0, 1);
                var (looked, wrong) = (0, 0);
                for (var y = oh / 2 - 60; y <= oh / 2 + 60; y += 3)
                {
                    for (var x = ow / 2 - 100; x <= ow / 2 + 100; x++)
                    {
                        if (!camera.HitPlane(x + 0.5f, y + 0.5f, 0, out var on))
                            continue;
                        var across = (on.X - boxX) * side / boxSide / 8;
                        var within = across - MathF.Floor(across);
                        if (across < 0 || across >= side / 8f || within is < 0.3f or > 0.7f)
                            continue;
                        var expected = (int)MathF.Floor(across) % 2 == 0 ? 1f : 0f;
                        var c = close.Get(x, y);
                        looked++;
                        wrong += MathF.Abs((c.X + c.Y + c.Z) / 3 - expected) > 0.2f ? 1 : 0;
                    }
                }

                Line($"  a 512 px still of 8 px stripes in a 64 px box, the camera close: every pixel off the stripes' edges black or white as its stripe");
                Console.WriteLine($"        close up: {looked} pixels checked, {wrong} not their stripe's black or white");
                if (looked < 1000 || wrong > looked / 100)
                    problems.Add($"3D full resolution: {wrong} of {looked} pixels close up are not their stripe's black or white (the plane is not at its source's resolution)");
            }

            // The selected layer's outline round its box: the ring just outside it, the same with a multi-pass blur.
            const float yaw = 24, pitch = 12, zoom = 1.4f, spread = 0.2f * cw;
            (float X, float Y, float W, float H) selected = (90, 40, 140, 100);
            RefImage? Outlined(float blur)
            {
                Camera(yaw, pitch, zoom, spread);
                ref var layer = ref Layer(0, GpuSourceKind.TestPattern, 2, selected.X, selected.Y, selected.W, selected.H);
                layer.Flags |= GpuLayerDescriptor.FlagSelected;
                if (blur > 0)
                    Add(ref layer, new GpuEffectPacket(GpuShaderSlot.BoxBlur, blur, 0, 0, 0, 0, 0, 0, 0));
                return Render($"3D outline, blur {blur}");
            }

            var plain = Outlined(0);
            var blurs = new[] { Outlined(4), Outlined(12) };
            if (plain is not null)
            {
                var camera = HandPegApp.Compositor.ExplodedCamera.Create(cw, ch, ow, oh, yaw, pitch, zoom, spread, 1);
                Vector2[][] corners =
                [
                    [.. new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) }.Select(c => camera.Project(selected.X + c.Item1 * selected.W, selected.Y + c.Item2 * selected.H, 0))],
                ];
                var (ring, amber, worst) = (0, 0, 0);
                for (var y = 0; y < oh; y++)
                {
                    for (var x = 0; x < ow; x++)
                    {
                        var point = new Vector2(x + 0.5f, y + 0.5f);
                        if (!NearEdge(point, corners, 5) || NearEdge(point, corners, 1.5f)
                            || camera.HitPlane(point.X, point.Y, 0, out var on) && HandPegApp.Compositor.ExplodedCamera.InBox(on, selected.X, selected.Y, selected.W, selected.H, 0, 0.5f, 0.5f))
                            continue;
                        ring++;
                        var c = plain.Get(x, y);
                        amber += c.X > 0.3f && c.X > c.Z * 2 ? 1 : 0;
                        foreach (var blurred in blurs)
                        {
                            if (blurred is not null)
                                worst = Math.Max(worst, (int)MathF.Max(MathF.Max(Steps(blurred.Get(x, y).X, c.X), Steps(blurred.Get(x, y).Y, c.Y)), MathF.Max(Steps(blurred.Get(x, y).Z, c.Z), Steps(blurred.Get(x, y).W, c.W))));
                        }
                    }
                }

                Line($"  the selected layer's outline round its box: the same with a box blur of 4 px and of 12 px as with none");
                Console.WriteLine($"        outline: {ring} ring pixels, {amber} amber; worst {worst}/255 with a blur");
                if (amber < ring / 3)
                    problems.Add($"3D outline: only {amber} of {ring} pixels round the selected box are its outline");
                if (worst > 2)
                    problems.Add($"3D outline: the outline round the selected box changes with a blur on the layer ({worst}/255)");
            }

            // A layer coming and going above two others: they, and the selected one's outline, stay where they were.
            const float stackYaw = 28, stackPitch = 14, stackZoom = 1.6f;
            (float X, float Y, float W, float H) top = (240, 12, 70, 34);
            RefImage? Stack(bool third)
            {
                Camera(stackYaw, stackPitch, stackZoom, spread);
                client.Scene.ExplodedDepth = 3;
                ref var bottom = ref Layer(0, GpuSourceKind.SolidColor, 0, 20, 30, 120, 80);
                (bottom.SolidR, bottom.SolidG, bottom.SolidB, bottom.SolidA, bottom.ZDepth) = (1, 0, 0, 1, 0);
                bottom.Flags |= GpuLayerDescriptor.FlagSelected;
                ref var middle = ref Layer(1, GpuSourceKind.SolidColor, 0, 110, 70, 120, 80);
                (middle.SolidR, middle.SolidG, middle.SolidB, middle.SolidA, middle.ZDepth) = (0, 1, 0, 1, 1);
                if (third)
                {
                    ref var over = ref Layer(2, GpuSourceKind.SolidColor, 0, top.X, top.Y, top.W, top.H);
                    (over.SolidR, over.SolidG, over.SolidB, over.SolidA, over.ZDepth) = (0, 0, 1, 1, 2);
                }

                return Render(third ? "3D stack, three layers" : "3D stack, the top one gone");
            }

            if (Stack(true) is { } three && Stack(false) is { } two)
            {
                var camera = HandPegApp.Compositor.ExplodedCamera.Create(cw, ch, ow, oh, stackYaw, stackPitch, stackZoom, spread, 3);
                // (Its plane goes with it: its box, and the hairline round the canvas at its depth.)
                Vector2[][] gone =
                [
                    [.. new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) }.Select(c => camera.Project(top.X + c.Item1 * top.W, top.Y + c.Item2 * top.H, camera.DepthOf(2)))],
                    [.. new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) }.Select(c => camera.Project(c.Item1 * cw, c.Item2 * ch, camera.DepthOf(2)))],
                ];
                var (looked, moved) = (0, 0);
                for (var y = 0; y < oh; y++)
                {
                    for (var x = 0; x < ow; x++)
                    {
                        var point = new Vector2(x + 0.5f, y + 0.5f);
                        if (NearEdge(point, gone, 8) || camera.HitPlane(point.X, point.Y, camera.DepthOf(2), out var on)
                            && on.X >= top.X - 8 && on.X <= top.X + top.W + 8 && on.Y >= top.Y - 8 && on.Y <= top.Y + top.H + 8)
                            continue;
                        looked++;
                        var (a, b) = (three.Get(x, y), two.Get(x, y));
                        moved += MathF.Max(MathF.Max(Steps(a.X, b.X), Steps(a.Y, b.Y)), MathF.Max(Steps(a.Z, b.Z), Steps(a.W, b.W))) > 2 ? 1 : 0;
                    }
                }

                Line($"  a layer above two others gone: they and the selected one's outline stay where they were, to the pixel");
                Console.WriteLine($"        places: {looked} pixels away from the top layer, {moved} changed");
                if (moved > 0 || looked < 10_000)
                    problems.Add($"3D places: {moved} of {looked} pixels away from the top layer changed when it went (the stack moved)");
            }
        }

        private static bool NearEdge(Vector2 point, Vector2[][] quads, float band)
        {
            foreach (var quad in quads)
            {
                for (var i = 0; i < quad.Length; i++)
                {
                    var (a, b) = (quad[i], quad[(i + 1) % quad.Length]);
                    if (!float.IsFinite(a.X) || !float.IsFinite(b.X))
                        return true;
                    var edge = b - a;
                    var t = Math.Clamp(Vector2.Dot(point - a, edge) / Math.Max(edge.LengthSquared(), 1e-6f), 0, 1);
                    if (Vector2.Distance(point, a + edge * t) < band)
                        return true;
                }
            }

            return false;
        }

        // A solid layer's colour as read back (premultiplied, opaque): 0 red, 1 green, 2 blue, -1 none of them.
        private static int Classify(Vector4 c) =>
            c.X > 0.6f && c.Y < 0.25f && c.Z < 0.25f ? 0
            : c.Y > 0.6f && c.X < 0.25f && c.Z < 0.25f ? 1
            : c.Z > 0.6f && c.X < 0.25f && c.Y < 0.25f ? 2
            : -1;

        private static string ColorName(int k) => k switch { 0 => "red", 1 => "green", 2 => "blue", _ => "nothing" };

        /// <summary>A picture drawn bigger as PSCopy draws it: each pixel the bilinear sample at its centre (LinearClamp).</summary>
        private static RefImage Enlarged(RefImage input, int width, int height)
        {
            var output = new RefImage(width, height);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    output.Set(x, y, input.Bilinear((x + 0.5f) / width, (y + 0.5f) / height));
            }

            return output;
        }

        private static RefImage Scale(RefImage input, float factor)
        {
            var output = new RefImage(input.Width, input.Height, [.. input.Rgba.Select(v => v * factor)]);
            return output;
        }

        /// <summary>The picture moved across by whole pixels, see-through where it left.</summary>
        private static RefImage Shifted(RefImage input, int dx)
        {
            var output = new RefImage(input.Width, input.Height);
            for (var y = 0; y < input.Height; y++)
                for (var x = 0; x < input.Width; x++)
                    output.Set(x, y, x - dx is >= 0 and < W ? input.Get(x - dx, y) : Vector4.Zero);
            return output;
        }

        private static RefImage Wiped(RefImage input, float shown)
        {
            var output = new RefImage(input.Width, input.Height);
            var feather = MathF.Max(2, MathF.Round(MathF.Max(W, H) * 0.02f));
            for (var y = 0; y < input.Height; y++)
                for (var x = 0; x < input.Width; x++)
                    output.Set(x, y, input.Get(x, y) * Math.Clamp(((W + feather) * shown - (x + 0.5f)) / feather, 0, 1));
            return output;
        }

        private static RefImage Blended(RefImage under, RefImage layer, int mode)
        {
            var output = new RefImage(under.Width, under.Height);
            for (var y = 0; y < under.Height; y++)
                for (var x = 0; x < under.Width; x++)
                {
                    var (d, s) = (under.Get(x, y), layer.Get(x, y));
                    var du = d.W > 1e-5f ? new Vector3(d.X, d.Y, d.Z) / d.W : Vector3.Zero;
                    var su = s.W > 1e-5f ? new Vector3(s.X, s.Y, s.Z) / s.W : Vector3.Zero;
                    var mixed = Vector3.Clamp(new Vector3(Mix(du.X, su.X, mode), Mix(du.Y, su.Y, mode), Mix(du.Z, su.Z, mode)), Vector3.Zero, Vector3.One);
                    var rgb = (1 - s.W) * new Vector3(d.X, d.Y, d.Z) + s.W * ((1 - d.W) * su + d.W * mixed);
                    output.Set(x, y, new Vector4(rgb, s.W + d.W * (1 - s.W)));
                }
            return output;
        }

        private static float Mix(float a, float b, int mode) => mode switch
        {
            1 => a * b,
            2 => 1 - (1 - a) * (1 - b),
            3 => MathF.Min(a + b, 1),
            4 => a < 0.5f ? 2 * a * b : 1 - 2 * (1 - a) * (1 - b),
            5 => a > 0.5f ? b + (1 - b) * (a - 0.5f) / 0.5f * (0.5f - MathF.Abs(b - 0.5f)) : b - b * ((0.5f - a) / 0.5f) * (0.5f - MathF.Abs(b - 0.5f)),
            6 => b < 0.5f ? 2 * a * b : 1 - 2 * (1 - a) * (1 - b),
            7 => MathF.Abs(a - b),
            _ => b,
        };

        // ----- Zone 0: a real video -----

        public void Decode()
        {
            Line($"decode (an H.264 clip made by HandPeg's FFmpeg: 30 grey levels, a key frame every 10, B-frames):");
            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null)
            {
                Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the decode checks were not run");
                Line($"  frame-exact seeks, forward and back");
                Line($"  BT.709 colour within 4/255");
                return;
            }

            var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.Compositor");
            Directory.CreateDirectory(folder);
            var steps = Path.Combine(folder, "steps.mp4");
            var color = Path.Combine(folder, "color.mp4");
            Run(ffmpeg, $"-f lavfi -i \"color=c=black:s={W}x{H}:r=30:d=1,format=yuv420p,geq=lum='16+N*7':cb=128:cr=128\" -c:v libx264 -preset veryfast -g 10 -keyint_min 10 -sc_threshold 0 -bf 2 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -an -y \"{steps}\"");
            Run(ffmpeg, $"-f lavfi -i \"color=c=0x2060C0:s={W}x{H}:r=30:d=0.5\" -vf \"scale=out_color_matrix=bt709:out_range=tv,format=yuv420p\" -c:v libx264 -preset veryfast -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -an -y \"{color}\"");

            client.SetSource(0, steps);
            client.SetSource(1, color);
            var wrong = new List<string>();
            foreach (var frame in new[] { 25, 3, 17, 18, 19, 29, 0, 12, 11, 26 })
            {
                Begin();
                ref var video = ref Layer(0, GpuSourceKind.Media);
                (video.SourceId, video.MediaSeconds) = (0, (frame + 0.5) / 30);
                if (Render($"frame {frame}") is not { } image)
                    continue;
                var grey = image.Get(W / 2, H / 2).X * 255;
                var seen = (int)MathF.Round(grey / (7 * 255f / 219));
                if (seen != frame)
                    wrong.Add($"{frame} showed {seen} (grey {grey:0.0}; worker: {client.Message})");
            }

            Line($"  frame-exact seeks, forward and back");
            if (wrong.Count > 0)
                problems.Add($"decode seeks: {string.Join("; ", wrong)}");

            InstantSeek();

            Begin();
            ref var swatch = ref Layer(0, GpuSourceKind.Media);
            (swatch.SourceId, swatch.MediaSeconds) = (1, 0.2);
            if (Render("colour") is { } colored)
            {
                var c = colored.Get(W / 2, H / 2) * 255;
                var off = MathF.Max(MathF.Abs(c.X - 0x20), MathF.Max(MathF.Abs(c.Y - 0x60), MathF.Abs(c.Z - 0xC0)));
                Line($"  BT.709 colour within 4/255");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        decoded colour ({c.X:0},{c.Y:0},{c.Z:0}) for (32,96,192); seeks {client.Memory.Control.DecoderSeeks}, frames {client.Memory.Control.DecodedFrames}"));
                if (off > 4)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"decode colour: ({c.X:0},{c.Y:0},{c.Z:0}) for (32,96,192)"));
            }
        }

        // ----- Instant Seek (5.0-AudioAnd3D): the same clip, without waiting for exact frames -----

        /// <summary>
        /// Publishes frame <paramref name="frame"/> of the steps clip as the player does (no FlagExactFrames) and waits for
        /// the exact frame: whether the first frame presented for it was already exact, and the grey level finally shown.
        /// </summary>
        private (bool FirstExact, int Shown) SeekLive(int frame)
        {
            Begin(flags: FrameSceneHeader.FlagRawOutput);
            ref var video = ref Layer(0, GpuSourceKind.Media);
            (video.SourceId, video.MediaSeconds, video.ClipKey) = (0, (frame + 0.5) / 30, 7);
            var sequence = client.Publish();
            ref var control = ref client.Memory.Control;
            var clock = Stopwatch.StartNew();
            bool? firstExact = null;
            while (clock.ElapsedMilliseconds < 5000)
            {
                if (Volatile.Read(ref control.PresentedSequence) >= sequence)
                {
                    var exact = Volatile.Read(ref control.PresentedExact) != 0;
                    firstExact ??= exact;
                    if (exact)
                        break;
                }

                Thread.Sleep(1);
            }

            if (!reader.TryRead(out var bgra, out var width, out var height))
                return (firstExact ?? false, -1);
            var at = ((height / 2) * width + width / 2) * 4;
            var grey = bgra[at + 2];
            return (firstExact ?? false, (int)MathF.Round(grey / (7 * 255f / 219)));
        }

        private void InstantSeek()
        {
            ref var control = ref client.Memory.Control;
            client.ClearFrameCache();
            Thread.Sleep(50);

            // A cold seek off the key frames: something at once, then the exact frame.
            var (_, shown) = SeekLive(25);
            Line($"  instant seek: a cold seek off a key frame ends on the exact frame");
            if (shown != 25)
                problems.Add($"instant seek: frame 25 ended showing {shown}");

            // Steps back over what the chase decoded: exact at once, not one frame decoded again (once the paused pre-roll
            // round the moment, which decodes the rest of this short clip, is done).
            Thread.Sleep(800);
            var (decoded, seeks, opens) = (Volatile.Read(ref control.DecodedFrames), Volatile.Read(ref control.DecoderSeeks), Volatile.Read(ref control.DecoderOpens));
            var steps = new List<string>();
            for (var frame = 24; frame >= 20; frame--)
            {
                var (firstExact, at) = SeekLive(frame);
                if (!firstExact || at != frame)
                    steps.Add($"{frame}: first exact {firstExact}, showed {at}");
                if (Volatile.Read(ref control.DecodedFrames) != decoded)
                    steps.Add($"{frame}: {Volatile.Read(ref control.DecodedFrames) - decoded} decoded so far");
            }

            Line($"  instant seek: frame steps back come from the cache, with no decode");
            if (steps.Count > 0 || Volatile.Read(ref control.DecodedFrames) != decoded)
                problems.Add($"instant seek steps back: {string.Join("; ", steps)} (decoded {Volatile.Read(ref control.DecodedFrames) - decoded} frames, " +
                    $"{Volatile.Read(ref control.DecoderSeeks) - seeks} seeks, {Volatile.Read(ref control.DecoderOpens) - opens} opens; {control.CachedFrames} frames cached)");

            // The pointer's hint: decoded before the click, so the click's first frame is the exact one.
            client.ClearFrameCache();
            Thread.Sleep(50);
            var predicted = Volatile.Read(ref control.PredictedHits);
            client.SetHints([new GpuSeekHint { SourceId = 0, Kind = (int)GpuSeekHintKind.Pointer, MediaSeconds = 15.5 / 30 }]);
            Thread.Sleep(400);
            var (hintedExact, hinted) = SeekLive(15);
            client.SetHints([]);
            Line($"  instant seek: a hinted moment is decoded before it is asked for");
            if (!hintedExact || hinted != 15 || Volatile.Read(ref control.PredictedHits) <= predicted)
                problems.Add($"instant seek hint: first exact {hintedExact}, showed {hinted}, predicted hits {Volatile.Read(ref control.PredictedHits) - predicted}");

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"        cache {control.CachedFrames} frames, {control.CacheBytes >> 20} MB of {control.CacheBudgetBytes >> 20} MB; hits {control.CacheHits}, misses {control.CacheMisses}, stand-ins {control.ApproximatePresents}, last exact {control.LastExactLatencyMs:0.0} ms"));
        }

        private static string? FindFfmpeg()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var candidate in new[] { Path.Combine(local, "HandPeg-Data", "deps", "ffmpeg", "ffmpeg.exe"), Path.Combine(local, "HandPeg", "deps", "ffmpeg", "ffmpeg.exe") })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static void Run(string program, string arguments)
        {
            using var process = Process.Start(new ProcessStartInfo(program, "-hide_banner -loglevel error " + arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg failed: {errors}");
        }

        // ----- Timing -----

        public void Timing()
        {
            Line($"timing (1920 x 1080, every slot on a full-frame layer, GPU timestamps, the best of four frames):");
            var best = new float[128];
            var runs = new int[128];
            Array.Fill(best, float.MaxValue);
            for (var round = 0; round < 5; round++)
            {
                Begin(1920, 1080, FrameSceneHeader.FlagTimePasses | FrameSceneHeader.FlagWaitForTimings);
                ref var a = ref Layer(0, GpuSourceKind.TestPattern, 3, 0, 0, 1920, 1080);
                (a.MaskResourceId, a.LutResourceId, a.CurveResourceId) = (1, 0, 2);
                foreach (var packet in new GpuEffectPacket[]
                {
                    new(GpuShaderSlot.Flip, 1, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.Lut3D, 0, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.Denoise, 4, 0, 0, 0, 0, 0, 0, 0),
                    new(GpuShaderSlot.ColorGrade, 1.1f, 0, 1.1f, 1, 0, 0, 0, 0), new(GpuShaderSlot.ColorBalance, 0.1f, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.ColorTemperature, 5000, 1, 0, 0, 0, 0, 0, 0),
                    new(GpuShaderSlot.Vignette, 0.6f, 100, 100, 0, 0, 0, 0, 0), new(GpuShaderSlot.HueSaturation, 10, 1.1f, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.ToneCurve, 0, 0, 0, 0, 0, 0, 0, 0),
                    new(GpuShaderSlot.ChromaKey, 0, 1, 0, 0.15f, 0.05f, 0.3f, 0, 0), new(GpuShaderSlot.MatteChoke, 2, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.MatteSmooth, 5, 0, 0, 0, 0, 0, 0, 0),
                    new(GpuShaderSlot.LayerMask, 0, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.CornerFeather, 10, 1, 12, 0, 0, 0, 0, 0), new(GpuShaderSlot.BoxBlur, 10, 0, 0, 0, 0, 0, 0, 0),
                    new(GpuShaderSlot.Sharpen, 0.5f, 0, 0, 0, 0, 0, 0, 0),
                })
                    Add(ref a, packet);
                ref var b = ref Layer(1, GpuSourceKind.TestPattern, 1, 200, 100, 800, 450);
                (b.BlurFillRadius, b.BlurFillPasses) = (20, 2);
                Add(ref b, new(GpuShaderSlot.DropShadow, 10, 135, 12, 0.6f, 0, 0, 0, 0));
                Add(ref b, new(GpuShaderSlot.BlendMode, 4, 0, 0, 0, 0, 0, 0, 0));
                Add(ref b, new(GpuShaderSlot.TransitionWipe, 0.6f, 1, 0, 0.6f, 1, 0, 0, 0));
                Add(ref b, new(GpuShaderSlot.TransitionBlurDissolve, 0.7f, 0, 0, 0.7f, 1, 0, 0, 0));
                var sequence = client.Publish();
                if (!client.WaitForFrame(sequence, 10_000))
                {
                    problems.Add($"timing: no frame ({client.Message})");
                    return;
                }

                // The first frame warms the shaders and the card's clocks: not counted.
                ref var timings = ref client.Memory.Control;
                for (var i = 0; round > 0 && i < 128; i++)
                {
                    if (timings.SlotRuns[i] > 0)
                        (best[i], runs[i]) = (MathF.Min(best[i], timings.SlotGpuMs[i]), timings.SlotRuns[i]);
                }
            }

            ref var control = ref client.Memory.Control;
            var slow = new List<string>();
            var report = new StringBuilder();
            foreach (var slot in Enum.GetValues<GpuShaderSlot>())
            {
                var (ms, count) = (best[(int)slot], runs[(int)slot]);
                if (count == 0)
                    continue;
                report.Append(string.Create(CultureInfo.InvariantCulture, $"{slot} {ms:0.000} ms/{count}, "));
                if (ms / count > 0.5f)
                    slow.Add(string.Create(CultureInfo.InvariantCulture, $"{slot} {ms / count:0.000} ms a pass"));
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        frame {control.LastFrameGpuMs:0.000} ms GPU, {control.LastFrameCpuMs:0.000} ms CPU: {report}"));
            Line($"  every pass of every slot under 0.5 ms");
            if (slow.Count > 0)
                problems.Add($"timing: {string.Join(", ", slow)}");
        }

        // ----- Recovery -----

        public void Recovery()
        {
            Line($"recovery (the standby takes over, the last scene is shown again):");
            Begin();
            Layer(0, GpuSourceKind.TestPattern, 1);
            _ = Render("before");
            foreach (var (name, command) in new[] { ("after a lost device (TDR)", CompositorCommand.SimulateDeviceLost), ("after a crash", CompositorCommand.SimulateCrash) })
            {
                var before = client.Recoveries;
                var oldWorker = client.WorkerProcessId;
                client.Send(command);
                var clock = Stopwatch.StartNew();
                while (client.Recoveries == before && clock.Elapsed.TotalSeconds < 15)
                    Thread.Sleep(5);

                Line($"  {name}: recovered, frame shown again");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {name}: {client.LastRecoveryMs:0.0} ms (worker {oldWorker} -> {client.WorkerProcessId})"));
                if (client.Recoveries == before)
                    problems.Add($"{name}: not recovered within 15 s ({client.State}: {client.Message})");
                else if (client.LastRecoveryMs > 1000)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: took {client.LastRecoveryMs:0} ms"));

                // The new worker's standby is started after its first frame; give it a moment before the next fall. The
                // writer's slot holds an older scene after a publish: the scene is written again before it is shown.
                Thread.Sleep(1500);
                Begin();
                Layer(0, GpuSourceKind.TestPattern, 1);
                Expect($"  {name}, the picture", Render(name), Pattern(1));
            }
        }
    }
}
