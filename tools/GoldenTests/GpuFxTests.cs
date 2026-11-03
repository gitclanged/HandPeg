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
        yield return Case("gpufx-cards", "# Every game-engine effect is a card of the registry: its slot, its lanes, its variant, its clock.", Cards);
        yield return Case("gpufx-drop-in", "# One click makes an adjustment clip with the card on it: nothing locked, one step of Undo.", DropIn);
        yield return Case("gpufx-fly-in-macro", "# The 3D Fly-In + Shockwave Impact is keyframes and a clip: nothing locked, one step of Undo.", FlyIn);
        yield return Case("gpufx-shutter-lanes", "# What the plan tells the worker: where a layer is as the shutter opens and closes, only for a layer with the card.", ShutterLanes);
        yield return Case("gpufx-ffmpeg-left-out", "# An export FFmpeg makes says what it could not draw: a shader is never left out silently.", LeftOut);
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
        expect(cards.All(d => d.Compile is null), "none has an FFmpeg filter string behind it (Law 4)");
        expect(cards.All(d => d.Params.All(p => p.Min <= p.Default && p.Default <= p.Max)), "every default lies within its range");
        expect(cards.All(d => d.GpuPacketCount is 1 or 2), "each one or two packets");

        // A slot of two effects tells them apart in a lane none of its parameters takes.
        foreach (var definition in cards.Where(d => d.GpuVariant != 0))
        {
            var last = definition.GpuPacketCount * GpuEffectPacket.Lanes;
            expect(definition.GpuLaneCount < last, $"{definition.Name}: its last lane is free for its variant");
            var packet = definition.NewCard().EvaluateGpuPacket(0, null, definition.GpuPacketCount - 1);
            expect((int)packet.P7 == definition.GpuVariant, $"{definition.Name}: and carries it");
        }

        var shockwave = EffectRegistry.Find(EffectRegistry.Shockwave)!.NewCard();
        shockwave.Set("duration", 2);
        expect(Math.Abs(shockwave.EvaluateGpuPacket(0.5).P0 - 0.25f) < 1e-5, "a card that runs by itself sends how far through it is: 0.5 s of 2 s is a quarter");
        expect(shockwave.EvaluateGpuPacket(3).P0 > 1, "past its end, past 1, so the shader knows");
        shockwave.SetRange((1, 3));
        expect(Math.Abs(shockwave.EvaluateGpuPacket(1.5).P0 - 0.25f) < 1e-5, "a card with a range counts from the start of it");

        var glass = new Layer { Kind = LayerKind.VideoFile };
        var rim = EffectRegistry.Find(EffectRegistry.NeonRim)!;
        expect(glass.CanTake(rim), "a clip takes a Neon Rim");
        glass.AddEffect(rim.NewCard());
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
        expect(clip?.Find(EffectRegistry.Explosion) is { Enabled: true }, "with its card on it, to tune");
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

        var impactClip = vm.Layers.FirstOrDefault(l => l.IsAdjustment);
        expect(impactClip is not null, "the impact is a clip of its own");
        expect(impactClip is not null && Math.Abs(impactClip.StartTime - (2 + MainViewModel.FlyOutSeconds)) < 0.034, "starting as the layer lands, to the frame");
        expect(impactClip?.Find(EffectRegistry.Shockwave) is { } card && card.Get("shake") > 0 && card.Get("ripple") > 0, "with the Shockwave and the Camera Shake on it, to change");
        expect(vm.Document.Tracks.Sum(t => t.Clips.Count) == clips + 1, "one clip more");

        vm.Undo();
        layer = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        expect(vm.Document.Tracks.Sum(t => t.Clips.Count) == clips, "Undo takes the clip away");
        expect(layer.GetKeys(KeyProperty.X).Count == 0 && layer.GetKeys(KeyProperty.Scale).Count == 0, "and the keyframes, in one step");
        expect(layer.GetKeys(KeyProperty.Rotation).Count == 2, "and leaves what was there");

        vm.PositionMs = 6800;
        expect(vm.FlyInWithShockwave(layer), "near the clip's end the beats fit what is left");
        expect(layer.GetKeys(KeyProperty.X)[^1].Time <= 6 + 1e-6, "ending inside the clip");

        var background = vm.Layers.FirstOrDefault(l => l.IsAdjustment);
        expect(background is not null && !vm.FlyInWithShockwave(background), "an adjustment clip has no picture to fly");
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
        expect(without is { } a && (a.Flags & (GpuLayerDescriptor.FlagShutterPlace | GpuLayerDescriptor.FlagPreviousMoment)) == 0, "with no card, no shutter is written");

        layer.AddEffect(EffectRegistry.Find(EffectRegistry.MotionBlur)!.NewCard());
        var moving = Layer();
        expect(moving is { } b && (b.Flags & GpuLayerDescriptor.FlagShutterPlace) != 0, "with the card, a keyframed layer has its shutter's places");
        if (moving is { } d)
        {
            // 0.2 of the frame's width a second, half a frame of 60 fps open: 1920 x 0.2 / 120 = 3.2 px across the shutter, a little either side of where it is.
            var swept = d.ShutterToX - d.ShutterFromX;
            expect(Math.Abs(swept - 3.2f) < 0.3f, "the shutter sweeps half a frame of its move");
            expect(d.ShutterFromX < d.X && d.X < d.ShutterToX, "either side of where it is at the frame");
            expect((d.Flags & GpuLayerDescriptor.FlagPreviousMoment) != 0 && Math.Abs(d.MediaSeconds - d.PreviousMoment - 1 / 60.0) < 1e-3, "and its source's moment a frame ago");
        }

        layer.SetKeys(KeyProperty.X, null);
        var still = Layer();
        expect(still is { } c && (c.Flags & GpuLayerDescriptor.FlagShutterPlace) == 0, "a layer that does not move has no sub-frames to draw");
        CutsAreNotMotion(expect);
    }

    // The main video cut from 4 s of its file back to 2 s: what lies across the cut is another shot, however fast it looks. A clip at the fastest it can run still measures its motion.
    private static void CutsAreNotMotion(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open();
        var document = vm.Document.Clone();
        var first = document.Tracks[0].Clips[0];
        first.End = 4;
        document.Tracks[0].Clips.Add(new TimelineClip { ContentKind = LayerKind.MainVideo, SourcePath = first.SourcePath, Start = 4, End = 8, MediaOffset = 2, SourceDuration = 8, State = first.State });
        typeof(MainViewModel).GetMethod("InstallDocument", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, [document]);
        vm.ViewOf(vm.Document.Tracks[0].Clips[1])!.AddEffect(EffectRegistry.Find(EffectRegistry.MotionBlur)!.NewCard());

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
        view.AddEffect(EffectRegistry.Find(EffectRegistry.MotionBlur)!.NewCard());
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
        expect(GpuExport.LeftOutByFfmpeg(vm.LivePreviewState()).Count == 0, "nothing to say of a project with no such card");
        var layer = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        layer.AddEffect(EffectRegistry.Find(EffectRegistry.Glow)!.NewCard());
        vm.AddVfxClip(EffectRegistry.Explosion);
        var names = GpuExport.LeftOutByFfmpeg(vm.LivePreviewState());
        expect(names.Contains("Film Halation & Glow") && names.Contains("3D Explosion"), "a clip's card and an adjustment clip's are both named");
        expect(GpuExport.WhyNot(vm.LivePreviewState() with { IsPreview = false, CopyVideoEncoder = false, VideoCodecArguments = "-c:v libx264 -preset medium -crf 18" }) is null, "and the GPU export draws them: it has no reason to refuse");
    }
}
