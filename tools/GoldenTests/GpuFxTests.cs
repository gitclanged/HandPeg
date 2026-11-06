using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.1-GpuFx on the real models: the cards and what they send the compositor, a procedural effect dropped in as an ordinary clip, the 3D Fly-In + Shockwave Impact macro as
/// ordinary keyframes and one step of Undo, what the plan tells the worker about a shutter, and an export FFmpeg makes never leaving a shader out silently. Every line says what it proved.
/// </summary>
public static class GpuFxTests
{
    private const string Overlay = "D:/media/overlay.mkv";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("gpufx-cards", "# Every game-engine effect is an effect of the registry: its slot, its lanes, its variant, its clock.", Cards);
        yield return Case("gpufx-drop-in", "# One click makes an adjustment clip with the card on it: nothing locked, one step of Undo.", DropIn);
        yield return Case("gpufx-fly-in-macro", "# The 3D Fly-In + Shockwave Impact is keyframes and a clip: nothing locked, one step of Undo.", FlyIn);
        yield return Case("gpufx-tilt-3d", "# True 3D (5.2A): a clip's Pitch and Yaw go into its layer, and its quad is drawn in perspective.", Tilt);
        yield return Case("gpufx-tilt-corners", "# Tilt on the Canvas and in the 3D view (5.2B, the user's Q4): a box's corners as its quad lays them, and the exploded planes turned with it.", TiltCorners);
        yield return Case("gpufx-tilt-pick", "# Tilted handles and the 3D view's pick (5.2C, the user's Q4): a tilted plane met where it is drawn, the grip on the drawn corner.", TiltPick);
        yield return Case("gpufx-shutter-lanes","# What the plan tells the worker: where a layer is as the shutter opens and closes, only for a layer with the card.", ShutterLanes);
        yield return Case("gpufx-left-out", "# An export says what the compositor could not draw (5.2-Audit: no shader-only effect is left out any more; a second LUT on a clip is named).", LeftOut);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            run(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    private static void Cards(Action<bool, string> expect)
    {
        var cards = EffectRegistry.All.Where(d => d.GpuOnly).ToList();
        expect(cards.Count == 11, "eleven effects are the compositor's alone");
        expect(cards.All(d => d.GpuSlot is >= GpuShaderSlot.MotionBlur and <= GpuShaderSlot.EnergyField), "each in slots 22 to 29");
        expect(Enumerable.Range(22, 8).All(slot => cards.Any(d => (int)d.GpuSlot == slot)), "and every one of those slots has one");
        expect(cards.All(d => d.Params.All(p => p.Min <= p.Default && p.Default <= p.Max)), "every default lies within its range");
        expect(cards.All(d => d.GpuPacketCount is 1 or 2), "each one or two packets");

        // A slot of two effects tells them apart in a lane none of its parameters takes.
        foreach (var definition in cards.Where(d => d.GpuVariant != 0))
        {
            // Its variant's lane: the last of its last packet, or the one it keeps for it (5.2: the Anamorphic Lens's P7, its Rotation after it).
            var last = definition.GpuPacketCount * GpuEffectPacket.Lanes;
            var at = definition.GpuVariantLane ?? last - 1;
            expect(definition.GpuLaneCount < last || definition.GpuVariantLane is not null, $"{definition.Name}: its last lane is free for its variant");
            var packet = definition.NewEffect().EvaluateGpuPacket(0, null, at / GpuEffectPacket.Lanes);
            var lanes = new[] { packet.P0, packet.P1, packet.P2, packet.P3, packet.P4, packet.P5, packet.P6, packet.P7 };
            expect((int)lanes[at % GpuEffectPacket.Lanes] == definition.GpuVariant, $"{definition.Name}: and carries it");
        }

        var shockwave = EffectRegistry.Find(EffectRegistry.Shockwave)!.NewEffect();
        shockwave.Set("duration", 2);
        expect(Math.Abs(shockwave.EvaluateGpuPacket(0.5).P0 - 0.25f) < 1e-5, "an effect that runs by itself sends how far through it is: 0.5 s of 2 s is a quarter");
        expect(shockwave.EvaluateGpuPacket(3).P0 > 1, "past its end, past 1, so the shader knows");
        shockwave.SetRange((1, 3));
        expect(Math.Abs(shockwave.EvaluateGpuPacket(1.5).P0 - 0.25f) < 1e-5, "an effect with a range counts from the start of it");

        var glass = new Layer { Kind = LayerKind.VideoFile };
        var rim = EffectRegistry.Find(EffectRegistry.NeonRim)!;
        expect(glass.CanTake(rim), "a clip takes a Neon Rim");
        glass.AddEffect(rim.NewEffect());
        expect(!glass.CanTake(rim), "and only one");
        expect(EffectRegistry.CategoryRank("3D VFX") < EffectRegistry.CategoryRank("Composite"), "the 3D VFX have a place of their own in the Add Effect menus");
    }

    private static void DropIn(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open();
        vm.PositionMs = 2000;
        var clips = vm.Document.Tracks.Sum(t => t.Clips.Count);
        var clip = vm.AddVfxClip(EffectRegistry.Explosion);
        expect(clip is { IsAdjustment: true }, "the Explosion lands as an adjustment clip");
        expect(clip is not null && Math.Abs(clip.StartTime - 2) < 1e-6, "at the playhead");
        expect(clip is not null && Math.Abs(clip.Duration - 1.6) < 1e-6, "as long as it runs");
        expect(clip?.Find(EffectRegistry.Explosion) is { Enabled: true }, "with its effect on it, to tune");
        expect(vm.Document.Tracks.Sum(t => t.Clips.Count) == clips + 1, "one clip more");
        vm.Undo();
        expect(vm.Document.Tracks.Sum(t => t.Clips.Count) == clips, "Undo takes it away in one step");

        var saber = vm.AddVfxClip(EffectRegistry.Saber);
        expect(saber is not null && Math.Abs(saber.Duration - 5) < 1e-6, "what stays on gets five seconds");
        expect(vm.AddVfxClip("builtin.vfx.nothing") is null, "a name not in the registry adds nothing");
        expect(EffectRegistry.VfxClips.All(type => EffectRegistry.Find(type) is { ForWholePicture: true }), "every drop-in can go on an adjustment clip");
    }

    private static void FlyIn(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Overlay, 1, 7)));
        var layer = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        (layer.SizeWidth, layer.SizeHeight) = (0.2, 0.2);
        (layer.PositionX, layer.PositionY) = (0.05, 0.1);

        // A keyframe of its own, later on, which the macro must leave alone.
        layer.SetKeys(KeyProperty.Rotation, [new Keyframe(0, 0), new Keyframe(5, 30)]);
        vm.PositionMs = 2000;
        var clips = vm.Document.Tracks.Sum(t => t.Clips.Count);

        expect(vm.FlyInWithShockwave(layer), "the macro takes an overlay");
        expect(layer.GetKeys(KeyProperty.X).Count == 4 && layer.GetKeys(KeyProperty.Y).Count == 4 && layer.GetKeys(KeyProperty.Scale).Count == 4, "four keyframes each of X, Y and Scale");
        var (from, impact, home) = (1.0, 1.0 + MainViewModel.FlyOutSeconds, 1.0 + MainViewModel.FlyOutSeconds + MainViewModel.ImpactSeconds * 0 + MainViewModel.ImpactHoldSeconds + MainViewModel.FlyBackSeconds);
        expect(Math.Abs(layer.GetKeys(KeyProperty.X)[0].Time - from) < 1e-6, "starting at the playhead, in the clip's own time");
        expect(Math.Abs(layer.Evaluate(KeyProperty.X, from) - 0.05) < 1e-6, "from where it was");
        var peak = layer.Evaluate(KeyProperty.Scale, impact);
        expect(peak > 0.39 && peak <= 0.85, "at the impact it is twice as large");
        expect(Math.Abs(layer.Evaluate(KeyProperty.X, impact) + peak / 2 - 0.5) < 1e-6, "and centred across the frame");
        expect(Math.Abs(layer.Evaluate(KeyProperty.X, home) - 0.05) < 1e-6 && Math.Abs(layer.Evaluate(KeyProperty.Scale, home) - 0.2) < 1e-6, "and back where it was by the end");
        expect(layer.GetKeys(KeyProperty.Rotation).Any(k => Math.Abs(k.Time - 5) < 1e-6 && Math.Abs(k.Value - 30) < 1e-6), "a keyframe outside its stretch is kept");

        // 5.2A: a real 3D swing, its Yaw and a lean back as it rises, square to the viewer on the impact and at rest again by the end.
        var swing = layer.Evaluate(KeyProperty.TiltY, from + (impact - from) * 0.45);
        expect(swing < -30 && swing >= -Layer.TiltLimit, "it swings round its upright axis on the way out (Tilt Y)");
        expect(layer.Evaluate(KeyProperty.TiltX, from + (impact - from) * 0.45) > 10, "leaning back as it rises (Tilt X)");
        expect(Math.Abs(layer.Evaluate(KeyProperty.TiltY, impact)) < 1e-6 && Math.Abs(layer.Evaluate(KeyProperty.TiltX, impact)) < 1e-6, "square to the viewer on the impact");
        expect(Math.Abs(layer.Evaluate(KeyProperty.TiltY, home)) < 1e-6 && layer.IsTilted, "and flat again by the end");

        var impactClip = vm.Layers.FirstOrDefault(l => l.IsAdjustment);
        expect(impactClip is not null, "the impact is a clip of its own");
        expect(impactClip is not null && Math.Abs(impactClip.StartTime - (2 + MainViewModel.FlyOutSeconds)) < 0.034, "starting as the layer lands, to the frame");
        expect(impactClip?.Find(EffectRegistry.Shockwave) is { } card && card.Get("shake") > 0 && card.Get("ripple") > 0, "with the Shockwave and the Camera Shake on it, to change");
        expect(vm.Document.Tracks.Sum(t => t.Clips.Count) == clips + 1, "one clip more");

        vm.Undo();
        layer = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        expect(vm.Document.Tracks.Sum(t => t.Clips.Count) == clips, "Undo takes the clip away");
        expect(layer.GetKeys(KeyProperty.X).Count == 0 && layer.GetKeys(KeyProperty.Scale).Count == 0, "and the keyframes, in one step");
        expect(!layer.IsTilted, "the swing's too");
        expect(layer.GetKeys(KeyProperty.Rotation).Count == 2, "and leaves what was there");

        vm.PositionMs = 6800;
        expect(vm.FlyInWithShockwave(layer), "near the clip's end the beats fit what is left");
        expect(layer.GetKeys(KeyProperty.X)[^1].Time <= 6 + 1e-6, "ending inside the clip");

        var background = vm.Layers.FirstOrDefault(l => l.IsAdjustment);
        expect(background is not null && !vm.FlyInWithShockwave(background), "an adjustment clip has no picture to fly");
    }

    /// <summary>
    /// True 3D (5.2A): a clip's Tilt X / Tilt Y (Pitch / Yaw) go into its layer as the plan writes it, keyed or set, held to 60 degrees; and the
    /// perspective quad is foreshortened as a card turned away from the viewer is: the far edge shorter, the near one longer, the anchor
    /// where it was, and a flat layer's matrix the same as before.
    /// </summary>
    private static void Tilt(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Overlay, 0, 8)));
        var layer = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        (layer.SizeWidth, layer.SizeHeight) = (0.4, 0.4);
        (layer.PositionX, layer.PositionY) = (0.3, 0.3);

        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        GpuLayerDescriptor? At(double seconds)
        {
            var plan = GpuScenePlan.Create(vm.LivePreviewState(), broker);
            var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
            var header = default(FrameSceneHeader);
            var count = plan.Evaluate(seconds, ref header, layers);
            for (var k = 0; k < count; k++)
            {
                if (layers[k].ClipKey == GpuHash.Of(vm.ClipOf(layer)!.Id))
                    return layers[k];
            }

            return null;
        }

        expect(At(2) is { TiltX: 0, TiltY: 0 }, "a flat clip's layer is flat");
        layer.TiltY = 75;
        expect(layer.TiltY == Layer.TiltLimit, "a tilt is held to 60 degrees");
        layer.TiltX = -20;
        expect(At(2) is { TiltX: -20, TiltY: 60 }, "set, its Pitch and Yaw go into its layer");
        layer.SetKeys(KeyProperty.TiltY, [new Keyframe(0, 0, EasingType.Linear), new Keyframe(4, 40, EasingType.Linear)]);
        expect(At(2) is { } keyed && Math.Abs(keyed.TiltY - 20) < 0.01f, "keyed, the Yaw at the moment (half way: 20 degrees)");
        var state = layer.ToState();
        var back = Layer.FromState(state, 1920, 1080, 1920, 1080);
        expect(back.TiltX == -20 && back.GetKeys(KeyProperty.TiltY).Count == 2, "kept in its look, keys and all");

        // The quad: 400 x 400 at (760, 340) on 1920 x 1080, turned 40 degrees about its upright axis through its middle.
        static (float X, float Y) Corner(HandPegApp.Compositor.Matrix4 m, float u, float v)
        {
            var w = u * m.M14 + v * m.M24 + m.M44;
            var (cx, cy) = ((u * m.M11 + v * m.M21 + m.M41) / w, (u * m.M12 + v * m.M22 + m.M42) / w);
            return ((cx + 1) * 1920 / 2, (1 - cy) * 1080 / 2);
        }

        var flat = HandPegApp.Compositor.Matrix4.Box(760, 340, 400, 400, 0, 0.5f, 0.5f, 1920, 1080);
        var same = HandPegApp.Compositor.Matrix4.Box(760, 340, 400, 400, 0, 0.5f, 0.5f, 1920, 1080, 0, 0);
        expect(flat.Equals(same) && flat.M14 == 0 && flat.M24 == 0 && flat.M44 == 1, "untilted, the matrix is the flat one, w 1");
        var yaw = HandPegApp.Compositor.Matrix4.Box(760, 340, 400, 400, 0, 0.5f, 0.5f, 1920, 1080, 0, 40);
        var (topLeft, bottomLeft, topRight, bottomRight) = (Corner(yaw, 0, 0), Corner(yaw, 0, 1), Corner(yaw, 1, 0), Corner(yaw, 1, 1));
        var (near, far) = (bottomLeft.Y - topLeft.Y, bottomRight.Y - topRight.Y);
        expect(far < 400 && near > 400 && far < near, "swung (Yaw 40), its right edge is further away: shorter, its left edge longer");
        expect(topRight.X - topLeft.X < 400 * MathF.Cos(40 * MathF.PI / 180) + 1, "and it is narrower, as a card turned away is");
        var middle = Corner(yaw, 0.5f, 0.5f);
        expect(Math.Abs(middle.X - 960) < 0.01f && Math.Abs(middle.Y - 540) < 0.01f, "about its anchor, which stays where it was");
        var pitch = HandPegApp.Compositor.Matrix4.Box(760, 340, 400, 400, 0, 0.5f, 0.5f, 1920, 1080, 30, 0);
        expect(Corner(pitch, 1, 0).X - Corner(pitch, 0, 0).X < Corner(pitch, 1, 1).X - Corner(pitch, 0, 1).X, "tilted back (Pitch 30), its top edge is the shorter");
    }

    /// <summary>
    /// 5.2B (the user's Q4): Matrix4.Corners, the outline the Canvas and the player draw, is the quad's own corners (the flat box's turned
    /// corners untilted, the perspective-divided ones tilted); ExplodedCamera.PlaneMatrix is the flat plane's to the number untilted, and turned
    /// about the anchor tilted (the anchor stays, the far edge is further from the camera).
    /// </summary>
    private static void TiltCorners(Action<bool, string> expect)
    {
        static (float X, float Y) Corner(HandPegApp.Compositor.Matrix4 m, float u, float v)
        {
            var w = u * m.M14 + v * m.M24 + m.M44;
            var (cx, cy) = ((u * m.M11 + v * m.M21 + m.M41) / w, (u * m.M12 + v * m.M22 + m.M42) / w);
            return ((cx + 1) * 1920 / 2, (1 - cy) * 1080 / 2);
        }

        static float Off((float X, float Y) a, (float X, float Y) b) => MathF.Max(MathF.Abs(a.X - b.X), MathF.Abs(a.Y - b.Y));

        (float U, float V)[] order = [(0, 0), (1, 0), (1, 1), (0, 1)];
        var flat = HandPegApp.Compositor.Matrix4.Corners(700, 300, 400, 300, 25, 0.5f, 0.5f, 1920, 1080);
        var turned = HandPegApp.Compositor.ExplodedCamera.BoxPoint;
        expect(flat.Length == 4 && order.Select((c, k) => Off(flat[k], (turned(700, 300, 400, 300, 25, 0.5f, 0.5f, c.U, c.V).X, turned(700, 300, 400, 300, 25, 0.5f, 0.5f, c.U, c.V).Y))).Max() < 1e-3f,
            "untilted, the four corners (top left, top right, bottom right, bottom left) are the turned box's");

        var matrix = HandPegApp.Compositor.Matrix4.Box(700, 300, 400, 300, 25, 0.5f, 0.5f, 1920, 1080, 30, -40);
        var tilted = HandPegApp.Compositor.Matrix4.Corners(700, 300, 400, 300, 25, 0.5f, 0.5f, 1920, 1080, 30, -40);
        expect(order.Select((c, k) => Off(tilted[k], Corner(matrix, c.U, c.V))).Max() < 0.01f, "tilted, they are where the perspective quad puts its corners");
        expect(Off(tilted[0], flat[0]) > 5, "and not where the flat box's are");

        var camera = HandPegApp.Compositor.ExplodedCamera.Create(1920, 1080, 1280, 720, 30, 15, 1, 40, 3);
        var plain = camera.PlaneMatrix(100, 200, 640, 360, 10, 0.3f, 0.6f, 40);
        expect(plain.Equals(camera.PlaneMatrix(100, 200, 640, 360, 10, 0.3f, 0.6f, 40, 0, 0)), "an untilted plane's matrix is the flat one, number for number");
        var swung = camera.PlaneMatrix(100, 200, 640, 360, 10, 0.3f, 0.6f, 40, 0, 50);
        System.Numerics.Vector2 Seen(System.Numerics.Matrix4x4 m, float u, float v)
        {
            var clip = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(u, v, 0, 1), m);
            return new System.Numerics.Vector2((clip.X / clip.W + 1) * 1280 / 2, (1 - clip.Y / clip.W) * 720 / 2);
        }

        expect(System.Numerics.Vector2.Distance(Seen(plain, 0.3f, 0.6f), Seen(swung, 0.3f, 0.6f)) < 0.01f, "a tilted plane turns about the anchor: the anchor stays where it was");
        var (right, left) = (System.Numerics.Vector4.Transform(new System.Numerics.Vector4(1, 0.5f, 0, 1), swung).W, System.Numerics.Vector4.Transform(new System.Numerics.Vector4(0, 0.5f, 0, 1), swung).W);
        expect(right > left, "swung (Yaw 50), its right edge is further from the camera than its left");
    }

    /// <summary>
    /// 5.2C (the user's Q4): the 3D view picks and resizes a tilted plane where it is drawn (ExplodedCamera.HitBox meets the ray with the
    /// plane PlaneMatrix lays, MainWindow.OnExplodedBox takes the box on it), and the Canvas's grip on a tilted clip's drawn corner moves the
    /// flat box's corner so that the drawn one follows the pointer (LayerGizmoAdorner.FlatDelta).
    /// </summary>
    private static void TiltPick(Action<bool, string> expect)
    {
        var camera = HandPegApp.Compositor.ExplodedCamera.Create(1920, 1080, 1280, 720, 30, 15, 1, 40, 3);
        const float x = 600, y = 250, width = 640, height = 360, rotation = 10, anchorX = 0.3f, anchorY = 0.6f, z = 40;

        // Untilted: the very point HitPlane finds, and the box's own place on it.
        var flatWorld = HandPegApp.Compositor.ExplodedCamera.OnBox(x, y, width, height, rotation, anchorX, anchorY, z, 0, 0, 0.25f, 0.75f);
        var flatSeen = camera.Project(flatWorld.X, flatWorld.Y, flatWorld.Z);
        expect(camera.HitBox(flatSeen.X, flatSeen.Y, x, y, width, height, rotation, anchorX, anchorY, z, 0, 0, out var flatUv, out var flatHit)
            && camera.HitPlane(flatSeen.X, flatSeen.Y, z, out var onPlane) && System.Numerics.Vector2.Distance(onPlane, new(flatHit.X, flatHit.Y)) < 0.05f
            && MathF.Abs(flatUv.X - 0.25f) < 1e-3f && MathF.Abs(flatUv.Y - 0.75f) < 1e-3f,
            "untilted, the ray through a box's point meets its plane at that point (HitPlane's), a quarter across and three down");

        // Tilted (Pitch 25, Yaw -35): each of its corners as drawn is met at that corner of the box, on the tilted plane.
        const float tiltX = 25, tiltY = -35;
        var worst = 0f;
        foreach (var (u, v) in new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f), (0.5f, 0.5f) })
        {
            var world = HandPegApp.Compositor.ExplodedCamera.OnBox(x, y, width, height, rotation, anchorX, anchorY, z, tiltX, tiltY, u, v);
            var seen = camera.Project(world.X, world.Y, world.Z);
            if (!camera.HitBox(seen.X, seen.Y, x, y, width, height, rotation, anchorX, anchorY, z, tiltX, tiltY, out var uv, out var hit))
            {
                worst = float.PositiveInfinity;
                continue;
            }

            worst = MathF.Max(worst, MathF.Max(MathF.Abs(uv.X - u), MathF.Abs(uv.Y - v)));
            worst = MathF.Max(worst, System.Numerics.Vector3.Distance(hit, world) / width);
        }

        expect(worst < 1e-3f, "tilted, the ray through each drawn corner (and the middle) meets the box there on its tilted plane (within a thousandth of the box)");

        // Where the tilted plane is drawn and the flat one is not: the pick takes the tilted box there, and not where only the flat box is.
        var plane = new HandPegApp.Services.ExplodedPlane { X = x, Y = y, Width = width, Height = height, Rotation = rotation, AnchorX = anchorX, AnchorY = anchorY, TiltX = tiltX, TiltY = tiltY };
        var flatPlane = plane with { TiltX = 0, TiltY = 0 };
        System.Numerics.Vector2 Seen(float u, float v, float tx, float ty)
        {
            var world = HandPegApp.Compositor.ExplodedCamera.OnBox(x, y, width, height, rotation, anchorX, anchorY, z, tx, ty, u, v);
            return camera.Project(world.X, world.Y, world.Z);
        }

        bool Picks(in HandPegApp.Services.ExplodedPlane p, System.Numerics.Vector2 point) => HandPegApp.MainWindow.OnExplodedBox(camera, point, p, z, out _, out _);
        (float U, float V)? onlyTilted = null, onlyFlat = null;
        for (var u = 0.02f; u < 1 && (onlyTilted is null || onlyFlat is null); u += 0.04f)
        {
            for (var v = 0.02f; v < 1; v += 0.04f)
            {
                if (onlyTilted is null && !Picks(flatPlane, Seen(u, v, tiltX, tiltY)))
                    onlyTilted = (u, v);
                if (onlyFlat is null && !Picks(plane, Seen(u, v, 0, 0)))
                    onlyFlat = (u, v);
            }
        }

        expect(onlyTilted is { } t && Picks(plane, Seen(t.U, t.V, tiltX, tiltY)), "a point of the tilted box as drawn, outside the flat box, picks the tilted plane");
        expect(onlyFlat is { } f && !Picks(plane, Seen(f.U, f.V, 0, 0)), "a point of the flat box only, outside the tilted box as drawn, does not");

        // The grip: a flat box's move is the move; a tilted one's, along its drawn bottom edge, is the flat corner's move across alone.
        var size = new System.Windows.Size(400, 300);
        System.Windows.Point[] box = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)];
        var same = HandPegApp.LayerGizmoAdorner.FlatDelta(box, size, new System.Windows.Vector(7, -3));
        expect(same == new System.Windows.Vector(7, -3), "a flat box's grip moves its corner as the pointer moves");
        var drawn = HandPegApp.Compositor.Matrix4.Corners(0, 0, 400, 300, 0, 0.5f, 0.5f, 1920, 1080, 20, 30).Select(c => new System.Windows.Point(c.X, c.Y)).ToArray();
        var alongBottom = (drawn[2] - drawn[3]) / 400 * 10;
        var flat = HandPegApp.LayerGizmoAdorner.FlatDelta(drawn, size, alongBottom);
        expect(Math.Abs(flat.X - 10) < 1e-6 && Math.Abs(flat.Y) < 1e-6, "a tilted box's drawn corner moved 10 pixels' worth along its drawn bottom edge grows it 10 across, none down");

        TiltGrips(expect, drawn, size);
    }

    private static readonly HandPegApp.GripCorner[] Corners =
        [HandPegApp.GripCorner.TopLeft, HandPegApp.GripCorner.TopRight, HandPegApp.GripCorner.BottomRight, HandPegApp.GripCorner.BottomLeft];

    /// <summary>
    /// Pass 5.2D (the user's Q3): a grip on each of a tilted clip's four drawn corners. Each corner's drag along its own
    /// drawn edges is that flat corner's move along those sides (LayerGizmoAdorner.FlatDelta), and a drag of any corner resizes the box about
    /// its anchor point (MainWindow.ResizedFromCorner): the anchor stays, and the drawn corner ends under the pointer. Ctrl keeps the opposite
    /// corner instead (a tilted box's where it is drawn: TiltCtrl); the bottom right corner's arithmetic is the single grip's as it always was.
    /// </summary>
    private static void TiltGrips(Action<bool, string> expect, System.Windows.Point[] drawn, System.Windows.Size size)
    {
        // Along its own drawn edges (the top or bottom one across, the left or right one down), 10 pixels' worth of each.
        var edgesHold = true;
        foreach (var corner in Corners)
        {
            var (right, bottom) = HandPegApp.LayerGizmoAdorner.SidesOf(corner);
            var alongAcross = (bottom == 1 ? drawn[2] - drawn[3] : drawn[1] - drawn[0]) / size.Width * 10;
            var alongDown = (right == 1 ? drawn[2] - drawn[1] : drawn[3] - drawn[0]) / size.Height * 10;
            var across = HandPegApp.LayerGizmoAdorner.FlatDelta(drawn, size, alongAcross, corner);
            var down = HandPegApp.LayerGizmoAdorner.FlatDelta(drawn, size, alongDown, corner);
            edgesHold &= Math.Abs(across.X - 10) < 1e-6 && Math.Abs(across.Y) < 1e-6 && Math.Abs(down.X) < 1e-6 && Math.Abs(down.Y - 10) < 1e-6;
        }

        expect(edgesHold, "each of a tilted box's four drawn corners moved 10 pixels' worth along its own drawn top or bottom edge grows it 10 across, none down; along its own left or right edge, 10 down, none across");

        // Dragged 24 x 16 pixels outward, and 20 x 12 inward, from each corner of a tilted box (Pitch 25, Yaw -35, its anchor off its middle),
        // as a grip's drag comes: each event the pointer's offset from the grip where it now stands, three of them.
        const double frameW = 1920, frameH = 1080, boxX = 600, boxY = 250, boxW = 640, boxH = 360, anchorX = 0.3, anchorY = 0.6;
        const float tiltX = 25, tiltY = -35;
        static System.Windows.Point[] Drawn((double X, double Y, double Width, double Height) box) =>
            HandPegApp.Compositor.Matrix4.Corners((float)(box.X * frameW), (float)(box.Y * frameH), (float)(box.Width * frameW), (float)(box.Height * frameH), 0,
                (float)anchorX, (float)anchorY, (float)frameW, (float)frameH, tiltX, tiltY).Select(c => new System.Windows.Point(c.X, c.Y)).ToArray();

        var start = (X: boxX / frameW, Y: boxY / frameH, Width: boxW / frameW, Height: boxH / frameH);
        var anchorAt = (X: boxX + anchorX * boxW, Y: boxY + anchorY * boxH);
        double worstCorner = 0, worstAnchor = 0;
        foreach (var corner in Corners)
        {
            var (right, bottom) = HandPegApp.LayerGizmoAdorner.SidesOf(corner);
            foreach (var (outX, outY) in new[] { (24.0, 16.0), (-20.0, -12.0) })
            {
                var target = Drawn(start)[(int)corner] + new System.Windows.Vector(right == 1 ? outX : -outX, bottom == 1 ? outY : -outY);
                var box = start;
                for (var step = 0; step < 3; step++)
                {
                    var now = Drawn(box);
                    var flatMove = HandPegApp.LayerGizmoAdorner.FlatDelta(now, new System.Windows.Size(box.Width * frameW, box.Height * frameH), target - now[(int)corner], corner);
                    box = HandPegApp.MainWindow.ResizedFromCorner(box.X, box.Y, box.Width, box.Height, anchorX, anchorY, corner, flatMove.X / frameW, flatMove.Y / frameH,
                        heightFollows: false, box.Height / box.Width, fromOpposite: false);
                }

                worstCorner = Math.Max(worstCorner, (Drawn(box)[(int)corner] - target).Length);
                worstAnchor = Math.Max(worstAnchor, Math.Max(Math.Abs((box.X + anchorX * box.Width) * frameW - anchorAt.X), Math.Abs((box.Y + anchorY * box.Height) * frameH - anchorAt.Y)));
            }
        }

        Console.WriteLine(FormattableString.Invariant($"        tilted grips: the dragged corner ends {worstCorner:0.0000} px from the pointer at worst, the anchor {worstAnchor:0.0e0} px from where it was"));
        expect(worstCorner < 1, "dragged from any of its four corners (out and in), a tilted box's drawn corner ends under the pointer (within 1 px)");
        expect(worstAnchor < 1e-6, "and its anchor point stays where it was");

        // Ctrl: the opposite corner of the flat box stays where it is, from every corner; locked, the height follows the width.
        var ctrlHolds = true;
        foreach (var corner in Corners)
        {
            var (right, bottom) = HandPegApp.LayerGizmoAdorner.SidesOf(corner);
            var (x, y, width, height) = HandPegApp.MainWindow.ResizedFromCorner(0.2, 0.3, 0.4, 0.3, anchorX, anchorY, corner, 0.05, -0.04, heightFollows: false, 0.75, fromOpposite: true);
            var (oppositeX, oppositeY) = (right == 1 ? x : x + width, bottom == 1 ? y : y + height);
            ctrlHolds &= Math.Abs(oppositeX - (right == 1 ? 0.2 : 0.6)) < 1e-12 && Math.Abs(oppositeY - (bottom == 1 ? 0.3 : 0.6)) < 1e-12
                && Math.Abs(width - (0.4 + (right == 1 ? 0.05 : -0.05))) < 1e-12 && Math.Abs(height - (0.3 + (bottom == 1 ? -0.04 : 0.04))) < 1e-12;
        }

        var locked = HandPegApp.MainWindow.ResizedFromCorner(0.2, 0.3, 0.4, 0.3, anchorX, anchorY, HandPegApp.GripCorner.TopLeft, -0.1, 0.5, heightFollows: true, 0.75, fromOpposite: true);
        expect(ctrlHolds && Math.Abs(locked.Width - 0.5) < 1e-12 && Math.Abs(locked.Height - 0.375) < 1e-12 && Math.Abs(locked.Y + locked.Height - 0.6) < 1e-12,
            "with Ctrl, the opposite corner stays where it is from every corner (and a locked box's height follows its width)");

        TiltCtrl(expect, start);

        // The bottom right corner, about the anchor, is the single grip's arithmetic: the width by the move over (1 - anchor).
        var plain = HandPegApp.MainWindow.ResizedFromCorner(0.2, 0.3, 0.4, 0.3, anchorX, anchorY, HandPegApp.GripCorner.BottomRight, 0.07, 0.02, heightFollows: false, 0.75, fromOpposite: false);
        expect(Math.Abs(plain.Width - (0.4 + 0.07 / (1 - anchorX))) < 1e-12 && Math.Abs(plain.Height - (0.3 + 0.02 / (1 - anchorY))) < 1e-12,
            "the bottom right corner grows the box by the move over its share past the anchor, as the one grip always did");
    }

    /// <summary>
    /// Pass 5.2D: Ctrl on a tilted clip's grip keeps the opposite corner where it is drawn (the tilted quad the user
    /// sees), not only in the flat box: MainWindow.ResizedFromCorner moves the box's place by what the drawn corner moved. A flat clip's Ctrl
    /// is as it was. The box (fractions of a 1920 x 1080 frame) is TiltGrips' own, turned 10 degrees as well.
    /// </summary>
    private static void TiltCtrl(Action<bool, string> expect, (double X, double Y, double Width, double Height) start)
    {
        const double frameW = 1920, frameH = 1080, anchorX = 0.3, anchorY = 0.6, turned = 10;
        const float tiltX = 25, tiltY = -35;
        static System.Windows.Point[] Drawn((double X, double Y, double Width, double Height) box) =>
            HandPegApp.Compositor.Matrix4.Corners((float)(box.X * frameW), (float)(box.Y * frameH), (float)(box.Width * frameW), (float)(box.Height * frameH), (float)turned,
                (float)anchorX, (float)anchorY, (float)frameW, (float)frameH, tiltX, tiltY).Select(c => new System.Windows.Point(c.X, c.Y)).ToArray();

        // Dragged 24 x 16 pixels outward, and 20 x 12 inward, from each corner, its height free and locked, as a grip's drag comes (three
        // events, each the pointer's offset from the grip where it now stands): the drawn opposite corner after every event against where it was.
        double worstOpposite = 0, worstDragged = 0;
        foreach (var corner in Corners)
        {
            var (right, bottom) = HandPegApp.LayerGizmoAdorner.SidesOf(corner);
            var opposite = ((int)corner + 2) % 4;
            foreach (var locked in new[] { false, true })
            {
                foreach (var (outX, outY) in new[] { (24.0, 16.0), (-20.0, -12.0) })
                {
                    var stays = Drawn(start)[opposite];
                    var target = Drawn(start)[(int)corner] + new System.Windows.Vector(right == 1 ? outX : -outX, bottom == 1 ? outY : -outY);
                    var box = start;
                    for (var step = 0; step < 3; step++)
                    {
                        var now = Drawn(box);
                        var flatMove = HandPegApp.LayerGizmoAdorner.FlatDelta(now, new System.Windows.Size(box.Width * frameW, box.Height * frameH), target - now[(int)corner], corner);
                        box = HandPegApp.MainWindow.ResizedFromCorner(box.X, box.Y, box.Width, box.Height, anchorX, anchorY, corner, flatMove.X / frameW, flatMove.Y / frameH,
                            heightFollows: locked, start.Height / start.Width, fromOpposite: true, frameW, frameH, turned, tiltX, tiltY);
                        worstOpposite = Math.Max(worstOpposite, (Drawn(box)[opposite] - stays).Length);
                    }

                    worstDragged = Math.Max(worstDragged, (Drawn(box)[(int)corner] - target).Length);
                }
            }
        }

        // What keeping the flat box's corner alone would do (the tilt not given): 5 % of the frame across and 4 % down outward from each corner
        // moves the drawn opposite corner. And an untilted box's Ctrl is the flat one, the frame and the turn given or not.
        double unkept = 0;
        var flatSame = true;
        foreach (var corner in Corners)
        {
            var (right, bottom) = HandPegApp.LayerGizmoAdorner.SidesOf(corner);
            var opposite = ((int)corner + 2) % 4;
            var (moveX, moveY) = (right == 1 ? 0.05 : -0.05, bottom == 1 ? 0.04 : -0.04);
            var flatOnly = HandPegApp.MainWindow.ResizedFromCorner(start.X, start.Y, start.Width, start.Height, anchorX, anchorY, corner, moveX, moveY,
                heightFollows: false, 1, fromOpposite: true);
            unkept = Math.Max(unkept, (Drawn(flatOnly)[opposite] - Drawn(start)[opposite]).Length);
            var untilted = HandPegApp.MainWindow.ResizedFromCorner(start.X, start.Y, start.Width, start.Height, anchorX, anchorY, corner, moveX, moveY,
                heightFollows: false, 1, fromOpposite: true, frameW, frameH, turned, 0, 0);
            flatSame &= untilted == flatOnly;
        }

        Console.WriteLine(FormattableString.Invariant($"        tilted Ctrl: the drawn opposite corner moves {worstOpposite:0.0e0} px at worst (the flat box's corner alone: {unkept:0.00} px); the dragged corner ends {worstDragged:0.00} px from the pointer"));
        expect(worstOpposite < 1, "with Ctrl, a tilted (and turned) box's opposite corner stays where it is drawn, dragged out and in from each of its four corners, its height free or locked (within 1 px)");
        expect(unkept > 2 && flatSame, "where keeping only its flat box's corner would move it (more than 2 px); an untilted box's Ctrl is the flat one as before");
    }

    private static void ShutterLanes(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Overlay, 0, 8)));
        var layer = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        (layer.SizeWidth, layer.SizeHeight) = (0.3, 0.3);
        (layer.PositionX, layer.PositionY) = (0, 0.1);
        layer.SetKeys(KeyProperty.X, [new Keyframe(0, 0, EasingType.Linear), new Keyframe(4, 0.8, EasingType.Linear)]);

        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        GpuLayerDescriptor? Layer()
        {
            var plan = GpuScenePlan.Create(vm.LivePreviewState(), broker);
            var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
            var header = default(FrameSceneHeader);
            var count = plan.Evaluate(2, ref header, layers);
            for (var k = 0; k < count; k++)
            {
                if (layers[k].ClipKey == GpuHash.Of(vm.ClipOf(layer)!.Id))
                    return layers[k];
            }

            return null;
        }

        var without = Layer();
        expect(without is { } a && (a.Flags & (GpuLayerDescriptor.FlagShutterPlace | GpuLayerDescriptor.FlagPreviousMoment)) == 0, "with no such effect, no shutter is written");

        layer.AddEffect(EffectRegistry.Find(EffectRegistry.MotionBlur)!.NewEffect());
        var moving = Layer();
        expect(moving is { } b && (b.Flags & GpuLayerDescriptor.FlagShutterPlace) != 0, "with the card, a keyframed layer has its shutter's places");
        if (moving is { } d)
        {
            // 0.2 of the frame's width a second, half a frame of the Sequence open (its rate since 5.2-AuditB, 30 fps here): 1920 x 0.2 / 60 = 6.4 px
            // across the shutter, a little either side of where it is.
            var frame = vm.Document.FrameRate.FrameDuration;
            var swept = d.ShutterToX - d.ShutterFromX;
            expect(Math.Abs(swept - 1920 * 0.2 * frame / 2) < 0.3f, "the shutter sweeps half a frame of its move");
            expect(d.ShutterFromX < d.X && d.X < d.ShutterToX, "either side of where it is at the frame");
            expect((d.Flags & GpuLayerDescriptor.FlagPreviousMoment) != 0 && Math.Abs(d.MediaSeconds - d.PreviousMoment - frame) < 1e-3, "and its source's moment a frame ago");
        }

        layer.SetKeys(KeyProperty.X, null);
        var still = Layer();
        expect(still is { } c && (c.Flags & GpuLayerDescriptor.FlagShutterPlace) == 0, "a layer that does not move has no sub-frames to draw");
        CutsAreNotMotion(expect);
    }

    // The file opened cut from 4 s of it back to 2 s: what lies across the cut is another shot, however fast it looks. A clip at the fastest it can run still measures its motion.
    private static void CutsAreNotMotion(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open();
        var document = vm.Document.Clone();
        var first = document.Tracks[0].Clips[0];
        first.End = 4;
        document.Tracks[0].Clips.Add(new TimelineClip { ContentKind = LayerKind.VideoFile, SourcePath = first.SourcePath, Start = 4, End = 8, MediaOffset = 2, SourceDuration = 8, State = first.State });
        typeof(MainViewModel).GetMethod("InstallDocument", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, [document]);
        vm.ViewOf(vm.Document.Tracks[0].Clips[1])!.AddEffect(EffectRegistry.Find(EffectRegistry.MotionBlur)!.NewEffect());

        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        var plan = GpuScenePlan.Create(vm.LivePreviewState(), broker);
        GpuLayerDescriptor? At(double seconds)
        {
            var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
            var header = default(FrameSceneHeader);
            var count = plan.Evaluate(seconds, ref header, layers);
            for (var k = 0; k < count; k++)
            {
                for (var e = 0; e < layers[k].EffectCount; e++)
                {
                    if (layers[k].Effects[e].Slot == GpuShaderSlot.MotionBlur)
                        return layers[k];
                }
            }

            return null;
        }

        expect(At(4.005) is { } cut && (cut.Flags & GpuLayerDescriptor.FlagPreviousMoment) == 0, "the first frame after a cut measures no motion across it");
        expect(At(4.2) is { } on && (on.Flags & GpuLayerDescriptor.FlagPreviousMoment) != 0, "the frames after it do");

        var clip = DogfoodTests.FileClip(Overlay, 0, 4);
        clip.Speed = Layer.FastestSpeed;
        var fast = DogfoodTests.Open(DogfoodTests.Lane(clip));
        var view = fast.ViewOf(fast.Document.Tracks[1].Clips[0])!;
        view.AddEffect(EffectRegistry.Find(EffectRegistry.MotionBlur)!.NewEffect());
        using var fastBroker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        var fastPlan = GpuScenePlan.Create(fast.LivePreviewState(), fastBroker);
        var drawn = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var scene = default(FrameSceneHeader);
        var total = fastPlan.Evaluate(2, ref scene, drawn);
        var ramp = drawn.Take(total).Where(l => l.ClipKey == GpuHash.Of(fast.ClipOf(view)!.Id)).ToList();
        expect(ramp.Count == 1 && (ramp[0].Flags & GpuLayerDescriptor.FlagPreviousMoment) != 0, "the fastest a clip runs is still motion, not a cut");
    }

    private static void LeftOut(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Overlay, 0, 8)));
        expect(GpuExport.LeftOut(vm.LivePreviewState()).Count == 0, "nothing to say of a project with no such effect");
        var layer = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        layer.AddEffect(EffectRegistry.Find(EffectRegistry.Glow)!.NewEffect());
        vm.AddVfxClip(EffectRegistry.Explosion);
        var names = GpuExport.LeftOut(vm.LivePreviewState());
        expect(names.Count == 0, "the shader-only effects are no longer left out of anything (5.2-Audit: there is no FFmpeg export to leave them out of)");
        layer.AddEffect(EffectRegistry.Find(EffectRegistry.Lut)!.NewEffect());
        layer.AddEffect(EffectRegistry.Find(EffectRegistry.Lut)!.NewEffect());
        expect(GpuExport.LeftOut(vm.LivePreviewState()).Any(n => n.Contains("second", StringComparison.Ordinal)), "a second LUT on one clip is named: the compositor draws one");
        expect(GpuExport.WhyNot(vm.LivePreviewState() with { IsPreview = false, CopyVideoEncoder = false, VideoCodecArguments = "-c:v libx264 -preset medium -crf 18" }) is null, "and the GPU export draws them: it has no reason to refuse");
    }
}
