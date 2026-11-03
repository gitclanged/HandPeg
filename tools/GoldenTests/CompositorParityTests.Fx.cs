using System.Globalization;
using System.IO;
using System.Numerics;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using static HandPegGoldenTests.CompositorReference;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.1-GpuFx on a real worker. What has a formula (the glow, the rim, the ripple, the lens, frosted glass, the sub-frames of a shutter) is compared with its CPU mirror within
/// 3/255. What is procedural (the fireball, the sparks, the lines, the shield, the saber) is held to what it must always do, as the Thanos Snap is: nothing before and after its run,
/// one moment one frame, its light falling on what is under it, and its shape where the card says. Motion in the picture is measured on a real clip of a moving bar.
/// </summary>
public static partial class CompositorParityTests
{
    private sealed partial class Harness
    {
        private RefImage? Fx(string what, int pattern, GpuEffectPacket first, GpuEffectPacket? second = null, double seconds = 0)
        {
            Begin();
            client.Scene.PlayheadSeconds = seconds;
            ref var layer = ref Layer(0, GpuSourceKind.TestPattern, pattern);
            Add(ref layer, first);
            if (second is { } q)
                Add(ref layer, q);
            return Render(what);
        }

        private void Same(string what, RefImage? a, RefImage? b)
        {
            Line($"  {what}");
            if (a is null || b is null || !a.Rgba.SequenceEqual(b.Rgba))
                problems.Add($"{what}: the two frames differ");
        }

        // How far a picture differs from another, on average over its pixels, in 8-bit steps.
        private static float Changed(RefImage a, RefImage b)
        {
            var sum = 0f;
            for (var i = 0; i < a.Rgba.Length; i++)
                sum += MathF.Abs(a.Rgba[i] - b.Rgba[i]);
            return sum / a.Rgba.Length * 255;
        }

        // The mean of a region's red, green and blue.
        private static Vector3 Mean(RefImage image, int x0, int y0, int x1, int y1)
        {
            var (sum, count) = (Vector3.Zero, 0);
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var c = image.Get(x, y);
                    sum += new Vector3(c.X, c.Y, c.Z);
                    count++;
                }
            }

            return sum / Math.Max(count, 1);
        }

        private static float Brightness(RefImage image, int x0, int y0, int x1, int y1)
        {
            var mean = Mean(image, x0, y0, x1, y1);
            return (mean.X + mean.Y + mean.Z) / 3;
        }

        private void Holds(bool holds, FormattableString what, string? numbers = null)
        {
            Line($"  {what}");
            if (numbers is not null)
                Console.WriteLine($"        {numbers}");
            if (!holds)
                problems.Add(what.ToString(CultureInfo.InvariantCulture) + (numbers is null ? "" : $" ({numbers})"));
        }

        private static string Numbers(FormattableString numbers) => numbers.ToString(CultureInfo.InvariantCulture);

        public void GpuFx()
        {
            Line($"game-engine effects (5.1-GpuFx):");
            var scale = W / 1080f;

            // ----- Film Halation & Glow (23) -----
            float[] glow = [1.2f, 0.5f, 5, 0.6f, 1, 0.29f, 0.11f, 100];
            One("film halation and glow (23)", 0, GpuEffectPacket.From(GpuShaderSlot.HalationGlow, glow), input => Glow(input, glow, scale));
            float[] wide = [0.8f, 0.3f, 7.5f, 0, 1, 1, 1, 60];
            One("glow in its own colour, wide, at 60 % (23)", 0, GpuEffectPacket.From(GpuShaderSlot.HalationGlow, wide), input => Glow(input, wide, scale));
            One("glow of nothing is the picture", 0, new(GpuShaderSlot.HalationGlow, 0, 0.5f, 5, 0, 1, 1, 1, 100), input => input);

            // ----- Impact Shockwave (25) -----
            float[] wave = [0.35f, 40, 55, 80, 90, 25, 0];
            One("impact shockwave (25)", 2, GpuEffectPacket.From(GpuShaderSlot.ShockwaveLens, wave), input => Shockwave(input, wave, scale));
            One("shockwave before its start is the picture", 2, new(GpuShaderSlot.ShockwaveLens, -0.1f, 50, 50, 80, 90, 25, 40, 0), input => input);
            One("shockwave after its end is the picture", 2, new(GpuShaderSlot.ShockwaveLens, 1, 50, 50, 80, 90, 25, 40, 0), input => input);
            GpuEffectPacket Shaken(float through) => new(GpuShaderSlot.ShockwaveLens, through, 50, 50, 0, 90, 25, 60, 0);
            var (one, again, later) = (Fx("shake", 2, Shaken(0.2f), seconds: 1.0), Fx("shake again", 2, Shaken(0.2f), seconds: 1.0), Fx("shake on", 2, Shaken(0.2f), seconds: 1.1));
            Same("the camera shake: one moment, one frame", one, again);
            if (one is not null && later is not null)
                Holds(Changed(one, later) > 1, $"the camera shake moves from moment to moment", Numbers($"{Changed(one, later):0.0} steps"));

            // ----- Anamorphic Lens (25, variant 1) -----
            float[] lens = [1.5f, 0.5f, 0.7f, 0.23f, 0.48f, 1, 0.5f, 1];
            One("anamorphic lens (25)", 0, GpuEffectPacket.From(GpuShaderSlot.ShockwaveLens, lens), input => Anamorphic(input, lens));

            // ----- Neon Rim Light (24): a soft disc, its distance field by jump flooding against the exact distances -----
            float[] rim = [0.13f, 0.9f, 1, 3, 12, 1.5f, 0.25f, 0];
            const int rx = 80, ry = 45, rw = 96, rh = 54, room = 22;
            Begin();
            Add(ref Layer(0, GpuSourceKind.TestPattern, 3, rx, ry, rw, rh), GpuEffectPacket.From(GpuShaderSlot.RimGlass, rim));
            var lit = RimLight(Pattern(3, rw, rh), rim, room, 1);
            var canvas = new RefImage(W, H);
            for (var y = 0; y < lit.Height; y++)
            {
                for (var x = 0; x < lit.Width; x++)
                    canvas.Set(rx - room + x, ry - room + y, lit.Get(x, y));
            }

            Expect("neon rim light, out past its box (24)", Render("neon rim"), canvas.ToHalf());

            // ----- Frosted Glass (24, variant 1): a white pane over a checkerboard, its picture not showing -----
            const int gx = 64, gy = 36, gw = 128, gh = 72;
            float[] pane = [8, 0.25f, 1, 0.5f, 0.2f, 0, 0.5f, 1];
            Begin();
            Layer(0, GpuSourceKind.TestPattern, 2);
            ref var glass = ref Layer(1, GpuSourceKind.SolidColor, 0, gx, gy, gw, gh);
            (glass.SolidR, glass.SolidG, glass.SolidB, glass.SolidA) = (1, 1, 1, 1);
            Add(ref glass, GpuEffectPacket.From(GpuShaderSlot.RimGlass, pane));
            var checker = Pattern(2);
            var frosted = Kawase(checker, 8);
            var expected = new RefImage(W, H, (float[])checker.Rgba.Clone());
            bool On(int x, int y) => x >= gx && y >= gy && x < gx + gw && y < gy + gh;
            for (var y = gy; y < gy + gh; y++)
            {
                for (var x = gx; x < gx + gw; x++)
                {
                    // The shape's slope 3 texels each way, the border sampler making the box's sides an edge.
                    var slope = new Vector2((On(x + 3, y) ? 1 : 0) - (On(x - 3, y) ? 1 : 0), (On(x, y + 3) ? 1 : 0) - (On(x, y - 3) ? 1 : 0));
                    var under = frosted.Bilinear((x + 0.5f + slope.X * 14) / W, (y + 0.5f + slope.Y * 14) / H);
                    var seen = new Vector3(under.X, under.Y, under.Z);
                    var tinted = seen + (new Vector3(1, 0.5f, 0.2f) - seen) * 0.25f + Vector3.One * (MathF.Min(slope.Length(), 1) * 0.5f * 0.6f);
                    expected.Set(x, y, new Vector4(tinted, 1));
                }
            }

            Expect("frosted glass over a checkerboard (24)", Render("frosted glass"), expected.ToHalf());

            // ----- The shutter (22): sub-frames of a layer's own move -----
            const float from = 80.2f, to = 120.2f;
            RefImage Swept(float amount)
            {
                var image = new RefImage(W, H);
                for (var k = 0; k < 8; k++)
                {
                    var at = 0.5f + ((k + 0.5f) / 8 - 0.5f) * amount;
                    var left = from + (to - from) * at;
                    for (var y = 52; y < 92; y++)
                    {
                        for (var x = 0; x < W; x++)
                        {
                            if (x + 0.5f > left && x + 0.5f < left + 40)
                                image.Set(x, y, image.Get(x, y) + new Vector4(1, 0, 0, 1) / 8);
                        }
                    }
                }

                return image;
            }

            RefImage? Moving(string what, float amount)
            {
                Begin();
                ref var moving = ref Layer(0, GpuSourceKind.SolidColor, 0, 100.2f, 52, 40, 40);
                (moving.SolidR, moving.SolidG, moving.SolidB, moving.SolidA) = (1, 0, 0, 1);
                moving.Flags |= GpuLayerDescriptor.FlagShutterPlace;
                (moving.ShutterFromX, moving.ShutterFromY, moving.ShutterFromWidth, moving.ShutterFromHeight) = (from, 52, 40, 40);
                (moving.ShutterToX, moving.ShutterToY, moving.ShutterToWidth, moving.ShutterToHeight) = (to, 52, 40, 40);
                Add(ref moving, new(GpuShaderSlot.MotionBlur, 180, 8, 0, 1, amount, 12, 0, 0));
                return Render(what);
            }

            Expect("motion blur: a layer's move in sub-frames (22)", Moving("shutter", 100), Swept(1));
            Expect("motion blur at half its amount", Moving("half a shutter", 50), Swept(0.5f));

            Generators();
        }

        // ----- Slots 26 to 29, which no reference draws -----

        private void Generators()
        {
            var plain = Pattern(1);
            GpuEffectPacket Fire(float through) => new(GpuShaderSlot.Explosion, through, 50, 50, 70, 1, 0.48f, 0.12f, 0.7f);
            GpuEffectPacket FireLight(float spill) => new(GpuShaderSlot.Explosion, spill, 7, 0, 0, 0, 0, 0, 0);
            Expect("explosion before its start is the picture (26)", Fx("explosion before", 1, Fire(0), FireLight(1)), plain);
            Expect("explosion after its end is the picture", Fx("explosion after", 1, Fire(1), FireLight(1)), plain);
            Same("explosion: one moment, one frame", Fx("explosion", 1, Fire(0.3f), FireLight(0.7f)), Fx("explosion again", 1, Fire(0.3f), FireLight(0.7f)));
            if (Fx("early fire", 1, Fire(0.2f), FireLight(0)) is { } early && Fx("late fire", 1, Fire(0.8f), FireLight(0)) is { } late
                && Fx("lit fire", 1, Fire(0.2f), FireLight(1)) is { } lit)
            {
                var (hot, cold) = (Brightness(early, W / 2 - 8, H / 2 - 8, W / 2 + 8, H / 2 + 8), Brightness(late, W / 2 - 8, H / 2 - 8, W / 2 + 8, H / 2 + 8));
                Holds(hot > 0.6f, $"its heart burns bright", Numbers($"{hot:0.00}"));
                Holds(cold < hot * 0.6f, $"and it cools to smoke", Numbers($"{hot:0.00} to {cold:0.00}"));
                var (near, away) = (Brightness(early, 4, 4, 40, H - 4), Brightness(lit, 4, 4, 40, H - 4));
                Holds(away > near + 0.01f, $"its light spills on the picture beside it", Numbers($"{near:0.000} to {away:0.000}"));
            }

            GpuEffectPacket Burst(float through, float count = 220) => new(GpuShaderSlot.Sparks, through, 50, 60, count, 1, 1, 200, 1);
            GpuEffectPacket BurstLook(float debris, float spill) => new(GpuShaderSlot.Sparks, 1, 0.7f, 0.28f, debris, spill, 7, -90, 0);
            Expect("sparks before their start are the picture (27)", Fx("sparks before", 1, Burst(0), BurstLook(0.25f, 0.5f)), plain);
            Expect("sparks after their end are the picture", Fx("sparks after", 1, Burst(1), BurstLook(0.25f, 0.5f)), plain);
            Same("sparks: one moment, one frame", Fx("sparks", 1, Burst(0.3f), BurstLook(0.25f, 0.5f)), Fx("sparks again", 1, Burst(0.3f), BurstLook(0.25f, 0.5f)));
            if (Fx("few sparks", 1, Burst(0.3f, 30), BurstLook(0, 0)) is { } few && Fx("many sparks", 1, Burst(0.3f, 400), BurstLook(0, 0)) is { } many
                && Fx("debris", 1, Burst(0.3f, 400), BurstLook(1, 0)) is { } debris)
            {
                Holds(Changed(many, plain) > Changed(few, plain) * 3 && Changed(few, plain) > 0.01f, $"more particles, more light", Numbers($"{Changed(few, plain):0.00} and {Changed(many, plain):0.00}"));
                var sum = (RefImage image) => image.Rgba.Where((_, i) => i % 4 != 3).Sum();
                Holds(sum(debris) < sum(plain) && sum(many) > sum(plain), $"sparks add light, debris takes it away", Numbers($"{sum(many):0} and {sum(debris):0} against {sum(plain):0}"));
            }

            GpuEffectPacket Lines(float style, float opacity = 100) => new(GpuShaderSlot.AnimeLines, style, 50, 50, 120, 1, 35, 12, opacity);
            var ink = new GpuEffectPacket(GpuShaderSlot.AnimeLines, 1, 1, 1, 0, 7, 0, 0, 0);
            Same("anime lines: one moment, one frame (28)", Fx("lines", 1, Lines(0), ink, 1), Fx("lines again", 1, Lines(0), ink, 1));
            Expect("anime lines at no opacity are the picture", Fx("no lines", 1, Lines(0, 0), ink), plain);
            if (Fx("drawing", 1, Lines(0), ink, 1) is { } drawing && Fx("next drawing", 1, Lines(0), ink, 1 + 1 / 12.0 + 0.01) is { } next
                && Fx("across", 1, Lines(1), ink, 1) is { } across)
            {
                Holds(Changed(drawing, next) > 1, $"a new drawing twelve times a second", Numbers($"{Changed(drawing, next):0.0} steps"));
                Holds(Changed(drawing, across) > 1, $"radial and speed lines differ");
                var (middle, edge) = (Brightness(drawing, W / 2 - 12, H / 2 - 12, W / 2 + 12, H / 2 + 12) - Brightness(plain, W / 2 - 12, H / 2 - 12, W / 2 + 12, H / 2 + 12),
                    Brightness(drawing, 2, H / 2 - 12, 26, H / 2 + 12) - Brightness(plain, 2, H / 2 - 12, 26, H / 2 + 12));
                Holds(MathF.Abs(middle) < 0.01f && edge > 0.02f, $"they leave its middle clear", Numbers($"{middle:0.000} in the middle, {edge:0.000} at the edge"));
            }

            GpuEffectPacket Shield(float risen) => new(GpuShaderSlot.EnergyField, risen, 50, 50, 42, 0.24f, 0.78f, 1, 0.8f);
            var shieldLook = new GpuEffectPacket(GpuShaderSlot.EnergyField, 0, 0.8f, 7, 0, 0, 0, 0, 0);
            Expect("energy shield before it rises is the picture (29)", Fx("shield before", 1, Shield(0), shieldLook), plain);
            Same("energy shield: one moment, one frame", Fx("shield", 1, Shield(1), shieldLook), Fx("shield again", 1, Shield(1), shieldLook));
            if (Fx("shield up", 3, Shield(1), shieldLook) is { } up)
            {
                // Pattern 3 is see-through at the corners, so the corners show what it adds alone.
                var (rimAt, far) = (Brightness(up, W / 2 + (int)(H * 0.42f) - 3, H / 2 - 3, W / 2 + (int)(H * 0.42f) + 3, H / 2 + 3), Brightness(up, 2, 2, 12, 12));
                Holds(rimAt > 0.3f && far < 0.02f, $"a bright rim at its radius, nothing far off", Numbers($"{rimAt:0.00} and {far:0.000}"));
            }

            GpuEffectPacket Blade(float through) => new(GpuShaderSlot.EnergyField, through, 20, 50, 80, 50, 0.2f, 1, 0.4f);
            var bladeLook = new GpuEffectPacket(GpuShaderSlot.EnergyField, 0, 9, 1, 0, 0, 0, 0, 1);
            Same("saber: one moment, one frame (29)", Fx("saber", 1, Blade(1), bladeLook, 1), Fx("saber again", 1, Blade(1), bladeLook, 1));
            if (Fx("saber out", 1, Blade(1), bladeLook, 1) is { } blade && Fx("saber coming out", 1, Blade(0.3f), bladeLook, 1) is { } coming)
            {
                var (core, tip, tipNow) = (blade.Get(W / 2, H / 2), blade.Get((int)(W * 0.78f), H / 2), coming.Get((int)(W * 0.78f), H / 2));
                Holds(core.X > 0.9f && core.Y > 0.9f && core.Z > 0.9f, $"a white core along the blade");
                Holds(tip.Y > 0.7f && tipNow.Y < tip.Y, $"it comes out along its length", Numbers($"{tipNow.Y:0.00} to {tip.Y:0.00}"));
            }
        }

        // ----- Motion in the picture, on a real clip -----

        /// <summary>A bar 40 px wide going 8 px a frame: how wide its edges are, without the card, with it, at a speed ramp's double, and on a frozen moment.</summary>
        public void MotionBlurOnVideo(string ffmpeg, string folder)
        {
            var moving = Path.Combine(folder, "bar.mp4");
            Run(ffmpeg, $"-f lavfi -i \"color=c=black:s={W}x{H}:r=30:d=1,format=yuv420p,geq=lum='if(lt(mod(X-N*8,{W}),40),235,16)':cb=128:cr=128\" -c:v libx264 -preset veryfast -crf 10 -g 10 -keyint_min 10 -sc_threshold 0 -bf 0 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -an -y \"{moving}\"");
            client.SetSource(2, moving);
            const int frame = 15;
            var moment = (frame + 0.5) / 30;
            int EdgeWidth(string what, bool card, double before)
            {
                // The frame before first, as playing does: the cache holds it, and nothing is ever decoded for a shutter.
                Begin();
                ref var previous = ref Layer(0, GpuSourceKind.Media);
                (previous.SourceId, previous.MediaSeconds, previous.ClipKey) = (2, moment - 2.0 / 30, 11);
                Render($"{what}, two frames back");
                Begin();
                ref var last = ref Layer(0, GpuSourceKind.Media);
                (last.SourceId, last.MediaSeconds, last.ClipKey) = (2, moment - 1.0 / 30, 11);
                Render($"{what}, a frame back");

                Begin();
                ref var video = ref Layer(0, GpuSourceKind.Media);
                (video.SourceId, video.MediaSeconds, video.ClipKey) = (2, moment, 11);
                if (card)
                {
                    video.Flags |= GpuLayerDescriptor.FlagPreviousMoment;
                    video.PreviousMoment = before;
                    Add(ref video, new(GpuShaderSlot.MotionBlur, 180, 12, 1, 0, 100, 40, 0, 0));
                }

                if (Render(what) is not { } image)
                    return -1;
                var width = 0;
                for (var x = 0; x < W; x++)
                {
                    var level = image.Get(x, H / 2).X;
                    width += level is > 0.15f and < 0.85f ? 1 : 0;
                }

                return width;
            }

            var sharp = EdgeWidth("no motion blur", false, 0);
            var blurred = EdgeWidth("motion blur on a moving bar (22)", true, moment - 1.0 / 30);
            var ramped = EdgeWidth("motion blur at twice the speed", true, moment - 2.0 / 30);
            var frozen = EdgeWidth("motion blur on a frozen moment", true, moment);
            var numbers = Numbers($"edges {sharp} px sharp, {blurred} px at 180 degrees, {ramped} px at twice the speed, {frozen} px frozen");
            Holds(blurred >= sharp + 4 && ramped >= blurred + 3 && frozen == sharp, $"motion blur: the flow of a real clip, by its own speed (22)", numbers);
        }

        // ----- The Studio Stage -----

        public void StudioStage()
        {
            const int cw = 320, ch = 180, ow = 400, oh = 300;
            RefImage? Stage(string what, Vector3 colour, int flags, float yaw = 18, float pitch = 16, float spread = 40, float zoom = 1.2f)
            {
                // Long enough for the light, gathered as each frame is drawn and eased, to settle on this picture. (A scene is written anew for each publish: its slot holds another.)
                RefImage? shown = null;
                for (var frame = 0; frame < 24; frame++)
                {
                    Begin(cw, ch, FrameSceneHeader.FlagExactFrames | flags);
                    ref var scene = ref client.Scene;
                    (scene.OutputWidth, scene.OutputHeight) = (ow, oh);
                    (scene.ViewMode, scene.CameraYaw, scene.CameraPitch, scene.CameraZoom, scene.ExplodedZSpread) = (1, yaw, pitch, zoom, spread);
                    ref var layer = ref Layer(0, GpuSourceKind.SolidColor, 0, 0, 0, cw, ch);
                    (layer.SolidR, layer.SolidG, layer.SolidB, layer.SolidA, layer.ZDepth) = (colour.X, colour.Y, colour.Z, 1, 0);
                    ref var over = ref Layer(1, GpuSourceKind.SolidColor, 0, 90, 50, 140, 80);
                    (over.SolidR, over.SolidG, over.SolidB, over.SolidA, over.ZDepth) = (1, 1, 1, 1, 1);
                    shown = Render(what);
                }

                return shown;
            }

            Line($"the Studio Stage (Extra Animations):");
            const int stage = FrameSceneHeader.FlagStudioStage;
            var (red, blue) = (new Vector3(1, 0.05f, 0.05f), new Vector3(0.05f, 0.05f, 1));
            if (Stage("plain 3D", red, 0) is not { } plain || Stage("stage", red, stage) is not { } lit || Stage("blue stage", blue, stage) is not { } blueLit)
                return;

            var floorBand = (X0: 60, Y0: oh - 60, X1: ow - 60, Y1: oh - 16);
            var (floorPlain, floorRed, floorBlue) = (Mean(plain, floorBand.X0, floorBand.Y0, floorBand.X1, floorBand.Y1), Mean(lit, floorBand.X0, floorBand.Y0, floorBand.X1, floorBand.Y1),
                Mean(blueLit, floorBand.X0, floorBand.Y0, floorBand.X1, floorBand.Y1));
            Holds(floorRed.X > floorPlain.X + 0.02f, $"the floor shines under the stack", Numbers($"red {floorPlain.X:0.000} plain, {floorRed.X:0.000} on the stage"));
            Holds(floorRed.X > floorBlue.X + 0.02f && floorBlue.Z > floorRed.Z + 0.02f, $"the floor takes the picture's colour, red or blue",
                Numbers($"({floorRed.X:0.00}, {floorRed.Z:0.00}) and ({floorBlue.X:0.00}, {floorBlue.Z:0.00})"));
            var (backRed, backBlue) = (Mean(lit, 2, 2, 40, 40), Mean(blueLit, 2, 2, 40, 40));
            Holds(backRed.X > backBlue.X && backBlue.Z > backRed.Z, $"the room's light follows the picture", Numbers($"({backRed.X:0.000}, {backRed.Z:0.000}) and ({backBlue.X:0.000}, {backBlue.Z:0.000})"));

            // Dead straight with no spread is the flat picture, stage or no stage.
            var (flatPlain, flatStage) = (Stage("flat", red, 0, 0, 0, 0, 1), Stage("flat on the stage", red, stage, 0, 0, 0, 1));
            if (flatPlain is not null && flatStage is not null)
                Holds(Changed(flatPlain, flatStage) < 3, $"the way out of the flat picture stays quiet", Numbers($"{Changed(flatPlain, flatStage):0.00} steps"));
        }

        // ----- What they cost -----

        public void TimingFx()
        {
            Line($"timing of the game-engine effects (1920 x 1080, GPU timestamps, the best of four frames):");
            var cases = new (string Name, GpuShaderSlot Slot, float BudgetMs, GpuEffectPacket First, GpuEffectPacket? Second)[]
            {
                ("glow", GpuShaderSlot.HalationGlow, 2, new(GpuShaderSlot.HalationGlow, 1, 0.6f, 6, 0.6f, 1, 0.3f, 0.1f, 100), null),
                ("shockwave", GpuShaderSlot.ShockwaveLens, 1, new(GpuShaderSlot.ShockwaveLens, 0.4f, 50, 50, 60, 90, 22, 24, 0), null),
                ("lens", GpuShaderSlot.ShockwaveLens, 2, new(GpuShaderSlot.ShockwaveLens, 1, 0.7f, 0.6f, 0.2f, 0.5f, 1, 0.3f, 1), null),
                ("explosion", GpuShaderSlot.Explosion, 6, new(GpuShaderSlot.Explosion, 0.35f, 50, 50, 80, 1, 0.48f, 0.12f, 0.7f), new(GpuShaderSlot.Explosion, 0.7f, 7, 0, 0, 0, 0, 0, 0)),
                ("sparks", GpuShaderSlot.Sparks, 3, new(GpuShaderSlot.Sparks, 0.35f, 50, 60, 800, 1, 1, 200, 1), new(GpuShaderSlot.Sparks, 1, 0.7f, 0.28f, 0.25f, 0.5f, 7, -90, 0)),
                ("lines", GpuShaderSlot.AnimeLines, 2, new(GpuShaderSlot.AnimeLines, 0, 50, 50, 140, 1, 55, 12, 100), new(GpuShaderSlot.AnimeLines, 1, 1, 1, 0.1f, 7, 0, 0, 0)),
                ("shield", GpuShaderSlot.EnergyField, 2, new(GpuShaderSlot.EnergyField, 1, 50, 50, 42, 0.24f, 0.78f, 1, 0.8f), new(GpuShaderSlot.EnergyField, 0.5f, 0.8f, 7, 0, 0, 0, 0, 0)),
                ("saber", GpuShaderSlot.EnergyField, 2, new(GpuShaderSlot.EnergyField, 1, 35, 80, 65, 20, 0.2f, 1, 0.4f), new(GpuShaderSlot.EnergyField, 0.6f, 9, 1, 0.3f, 0, 0, 0, 1)),
                ("neon rim", GpuShaderSlot.RimGlass, 3, new(GpuShaderSlot.RimGlass, 0.13f, 0.9f, 1, 3, 24, 1.5f, 0.25f, 0), null),
            };

            var plainMs = Clocked(null, null);
            foreach (var (name, slot, budget, first, second) in cases)
            {
                var (best, whole, passes) = (float.MaxValue, float.MaxValue, 0);
                for (var round = 0; round < 5; round++)
                {
                    Begin(1920, 1080, FrameSceneHeader.FlagTimePasses | FrameSceneHeader.FlagWaitForTimings);
                    ref var layer = ref Layer(0, GpuSourceKind.TestPattern, 3, 0, 0, 1920, 1080);
                    Add(ref layer, first);
                    if (second is { } q)
                        Add(ref layer, q);
                    var sequence = client.Publish();
                    if (!client.WaitForFrame(sequence, 10_000))
                    {
                        problems.Add($"timing of {name}: no frame ({client.Message})");
                        return;
                    }

                    if (round > 0)
                        (best, whole, passes) = (MathF.Min(best, client.Memory.Control.SlotGpuMs[(int)slot]), MathF.Min(whole, client.Memory.Control.LastFrameGpuMs), client.Memory.Control.SlotRuns[(int)slot]);
                }

                Holds(best <= budget, $"{name} within its budget of {budget} ms", Numbers($"{name} {best:0.000} ms in {passes} passes, the frame {whole:0.000} ms"));
                Console.WriteLine(Numbers($"        {name}: {Clocked(first, second) - plainMs:0.00} ms a frame more than a plain layer, by the wall clock"));
            }
        }

        // The GPU's timestamps can read short of what a frame costs on a card that runs passes out of step: the other measure is the wall clock, each frame read back, so finished.
        private double Clocked(GpuEffectPacket? first, GpuEffectPacket? second)
        {
            var clock = new System.Diagnostics.Stopwatch();
            var best = double.MaxValue;
            for (var round = 0; round < 12; round++)
            {
                Begin(1920, 1080);
                ref var layer = ref Layer(0, GpuSourceKind.TestPattern, 3, 0, 0, 1920, 1080);
                if (first is { } p)
                    Add(ref layer, p);
                if (second is { } q)
                    Add(ref layer, q);
                clock.Restart();
                if (Render("clocked") is null)
                    return 0;
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            }

            return best;
        }
    }
}
