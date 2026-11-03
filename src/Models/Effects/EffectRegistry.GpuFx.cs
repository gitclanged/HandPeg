namespace HandPegApp.Models.Effects;

// ----- Pass 5.1-GpuFx: the game-engine effects -----
// Pure HLSL (Motion.hlsl, Light.hlsl, Vfx.hlsl), with no FFmpeg filter behind them (the roadmap's Law 4): GpuOnly, so an export only FFmpeg can make says what it left out.
// Every card starts visible, and every number of what it does is on it, keyframeable where that means anything (the Creator Ethos: one click, full manual override).
// Each lane is a parameter in its order (EvaluateGpuPacket); the shaders' comments name them. Slots of two effects carry a variant in the last lane.
public static partial class EffectRegistry
{
    public const string MotionBlur = "builtin.gpu.motionblur", Glow = "builtin.gpu.glow", NeonRim = "builtin.gpu.neonrim", FrostedGlass = "builtin.gpu.frostedglass",
        Shockwave = "builtin.gpu.shockwave", AnamorphicLens = "builtin.gpu.anamorphic", Explosion = "builtin.vfx.explosion", Sparks = "builtin.vfx.sparks",
        AnimeLines = "builtin.vfx.animelines", EnergyShield = "builtin.vfx.shield", Saber = "builtin.vfx.saber";

    /// <summary>The effects that are clips of their own, one click from the Timeline: each an adjustment clip with this card (MainViewModel.AddVfxClip), in the order the menus list them.</summary>
    public static IReadOnlyList<string> VfxClips { get; } = [Explosion, Sparks, AnimeLines, EnergyShield, Saber, Shockwave];

    // A point of the picture, in % of it.
    private static EffectParam PointAt(string key, string label, double value, bool across) =>
        new(key, label, Min: -20, Max: 120, Default: value, Step: 0.5, Unit: "%", Animation: ParamAnimation.Expression)
            { Tip = across ? "Where it is across the picture: 0 % its left edge, 100 % its right." : "Where it is down the picture: 0 % its top edge, 100 % its bottom." };

    private static EffectParam RunsFor(double seconds, double most = 10) =>
        new("duration", "Duration", Min: 0.05, Max: most, Default: seconds, Step: 0.05, Unit: "s")
            { GpuProgress = true, Tip = "How long it takes to play through, from the start of its clip (or of the card's range)." };

    private static EffectParam LightSpill(double value) =>
        new("spill", "Light Spill", Default: value, Animation: ParamAnimation.Expression)
            { Tip = "How much it lights what is under it. On an adjustment clip that is everything under the clip: the video catches its light, in its colour, without being touched." };

    private static EffectParam Seed() =>
        new("seed", "Variation", Min: 0, Max: 100, Default: 7, Step: 1) { IsAdvanced = true, Tip = "A different one each number, the same one every time for any number." };

    private static void RegisterGpuFx()
    {
        Register(new(MotionBlur, "Motion Blur", "Blur", EffectZone.Look,
            [
                new("shutter", "Shutter Angle", Min: 0, Max: 720, Default: 180, Step: 5, Unit: "°", Animation: ParamAnimation.Expression)
                    { Tip = "How long the shutter stays open: 180° is half a frame, as film has it; 360° a whole one." },
                new("samples", "Samples", Min: 8, Max: 16, Default: 12, Step: 1)
                    { IsAdvanced = true, Tip = "Steps along each streak (taken twice over, so 12 is 144 in all). More is smoother and costs a little more." },
                new("video", "Motion in the Picture", ParamKind.Toggle, Default: 1) { Tip = "Measures what moves inside the video from frame to frame (a whip-turn, a speed ramp) and blurs it along its way." },
                new("layer", "This Clip's Own Moves", ParamKind.Toggle, Default: 1) { Tip = "Blurs this clip's own keyframed moves, zooms and turns: real sub-frames of it along the way, nothing estimated." },
                new("amount", "Amount", Min: 0, Max: 100, Default: 100, Step: 1, Unit: "%", Animation: ParamAnimation.Expression) { Tip = "How much of the streak is drawn: keyframe it to bring the blur in and out." },
                new("longest", "Longest Streak", Min: 1, Max: 50, Default: 12, Step: 1, Unit: "%") { IsAdvanced = true, Tip = "The longest a streak may get, in % of the picture: keeps a wrong measure from smearing a whole frame." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.MotionBlur, GpuOnly = true,
            Description = "A real shutter: optical flow between frames for what moves in the video, sub-frames for the clip's own moves. On an adjustment clip it blurs everything under it.",
        });

        Register(new(Glow, "Film Halation & Glow", "Look", EffectZone.Look,
            [
                new("amount", "Glow", Min: 0, Max: 3, Default: 0, Animation: ParamAnimation.Expression) { Starting = 0.8, Tip = "How strong the light is that blooms round the highlights." },
                new("threshold", "Threshold", Default: 0.7, Animation: ParamAnimation.Expression) { Tip = "How bright a part of the picture must be to bloom." },
                new("spread", "Spread", Min: 1, Max: 8, Default: 5, Step: 0.1) { Tip = "How far the light goes: each step is twice as far." },
                new("halation", "Halation", Default: 0, Animation: ParamAnimation.Expression) { Starting = 0.6, Tip = "0 the highlights' own colour; 1 film's halo, in the Halation Color." },
                new("color", "Halation Color", ParamKind.Color) { TextDefault = "#FF4A1C", IsAdvanced = true },
                new("mix", "Mix", Min: 0, Max: 100, Default: 100, Step: 1, Unit: "%", Animation: ParamAnimation.Expression) { IsAdvanced = true, Tip = "How much of the glow's picture is laid over the original." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.HalationGlow, GpuOnly = true, Description = "Light blooming from the highlights in linear light, with film's warm halo round them.",
        });

        Register(new(AnamorphicLens, "Anamorphic Lens", "Look", EffectZone.Look,
            [
                new("streak", "Streaks", Min: 0, Max: 3, Default: 0, Animation: ParamAnimation.Expression) { Starting = 0.9, Tip = "How strong the long horizontal flares from bright lights are." },
                new("threshold", "Threshold", Default: 0.75) { Tip = "How bright a light must be to flare." },
                new("length", "Streak Length", Default: 0.6) { Tip = "How far the flares run across the picture." },
                new("color", "Streak Color", ParamKind.Color) { TextDefault = "#3A7BFF" },
                new("lens", "Barrel & Fringe", Default: 0, Animation: ParamAnimation.Expression) { Starting = 0.3, Tip = "The lens's bow and the colour fringe towards its corners." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.ShockwaveLens, GpuVariant = 1, GpuOnly = true, Description = "A cinema lens: long blue flares off bright lights, a slight bow and a colour fringe at the corners.",
        });

        Register(new(NeonRim, "Neon Rim Light", "Composite", EffectZone.Composite,
            [
                new("color", "Rim Color", ParamKind.Color) { TextDefault = "#22E6FF" },
                new("width", "Line Width", Min: 0, Max: 40, Default: 3, Step: 0.5, Unit: "px", Animation: ParamAnimation.Expression) { Tip = "The bright line round the shape." },
                new("glow", "Glow Reach", Min: 0, Max: 128, Default: 24, Step: 1, Unit: "px", Animation: ParamAnimation.Expression) { Tip = "How far the light falls off the line." },
                new("strength", "Strength", Min: 0, Max: 4, Default: 1.5, Animation: ParamAnimation.Expression) { Tip = "Keyframe it to make the sign flicker." },
                new("inward", "Inner Light", Default: 0.25) { IsAdvanced = true, Tip = "How much of the light also falls inside the shape." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.RimGlass, GpuOnly = true, Once = true,
            Description = "A neon line and its glow round the clip's exact shape: its mask, its key, its rounded corners, drawn from a distance field. The glow runs off the clip's box.",
        });

        Register(new(FrostedGlass, "Frosted Glass", "Composite", EffectZone.Composite,
            [
                new("frost", "Frost", Min: 1, Max: 128, Default: 24, Step: 1, Unit: "px", Animation: ParamAnimation.Expression) { Tip = "How much what lies under the clip is blurred." },
                new("tint", "Tint", Default: 0.12, Animation: ParamAnimation.Expression) { Tip = "How much of the Tint Color is in the glass." },
                new("color", "Tint Color", ParamKind.Color) { TextDefault = "#FFFFFF" },
                new("picture", "Clip Showing", Default: 0.35, Animation: ParamAnimation.Expression) { Tip = "How much of this clip's own picture lies on the glass: 0 the glass alone, cut to its shape." },
                new("edge", "Edge Light", Default: 0.6) { Tip = "The light caught along the edge of the pane." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.RimGlass, GpuVariant = 1, GpuOnly = true, Once = true,
            Description = "The clip's shape as a pane of frosted glass: what is under it, blurred and tinted, with its edge catching the light.",
        });

        Register(new(Shockwave, "Impact Shockwave", "3D VFX", EffectZone.Look,
            [
                RunsFor(0.9, 5),
                PointAt("x", "Center X", 50, across: true),
                PointAt("y", "Center Y", 50, across: false),
                new("ripple", "Ripple", Min: 0, Max: 200, Default: 60, Step: 1, Unit: "px", Animation: ParamAnimation.Expression) { Tip = "How far the ring bends the picture." },
                new("reach", "Reach", Min: 5, Max: 150, Default: 90, Step: 1, Unit: "%") { Tip = "How far the ring gets, in % of the picture's height." },
                new("ring", "Ring Width", Min: 2, Max: 60, Default: 22, Step: 1, Unit: "%") { IsAdvanced = true, Tip = "How thick the ring is, as a share of how far it has got." },
                new("shake", "Camera Shake", Min: 0, Max: 120, Default: 24, Step: 1, Unit: "px", Animation: ParamAnimation.Expression) { Tip = "How hard the frame shakes, dying away as the ring goes out." },
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.ShockwaveLens, GpuOnly = true,
            Description = "A ring of bent air going out from a point, and a camera shake. Works on what is under it on an adjustment clip.",
        });

        Register(new(Explosion, "3D Explosion", "3D VFX", EffectZone.Look,
            [
                RunsFor(1.6),
                PointAt("x", "Center X", 50, across: true),
                PointAt("y", "Center Y", 55, across: false),
                new("size", "Size", Min: 5, Max: 250, Default: 80, Step: 1, Unit: "%", Animation: ParamAnimation.Expression) { Tip = "How wide the fireball gets, in % of the picture's height." },
                new("color", "Fire Color", ParamKind.Color) { TextDefault = "#FF7A1E" },
                new("smoke", "Smoke", Default: 0.7) { Tip = "How much dark smoke it leaves." },
                LightSpill(0.7),
                Seed(),
            ], EffectCost.Red)
        {
            GpuSlot = GpuShaderSlot.Explosion, GpuOnly = true, Description = "A raymarched 3D fireball that bursts, rolls, cools and leaves smoke, and lights what is under it.",
        });

        Register(new(Sparks, "3D Sparks & Debris", "3D VFX", EffectZone.Look,
            [
                RunsFor(1.4),
                PointAt("x", "Center X", 50, across: true),
                PointAt("y", "Center Y", 60, across: false),
                new("count", "Particles", Min: 10, Max: 800, Default: 220, Step: 10) { Tip = "How many fly." },
                new("speed", "Speed", Min: 0, Max: 3, Default: 1) { Tip = "How fast they leave." },
                new("gravity", "Gravity", Min: 0, Max: 3, Default: 1) { Tip = "How hard they fall." },
                new("spread", "Spread", Min: 5, Max: 360, Default: 200, Step: 5, Unit: "°") { Tip = "How wide a fan they fly out in." },
                new("particle", "Particle Size", Min: 0.2, Max: 4, Default: 1) { IsAdvanced = true },
                new("color", "Spark Color", ParamKind.Color) { TextDefault = "#FFB347" },
                new("debris", "Debris", Default: 0.25) { Tip = "The share of them that are dark tumbling shards, not sparks." },
                LightSpill(0.5),
                Seed(),
                new("direction", "Direction", Min: -180, Max: 180, Default: -90, Step: 1, Unit: "°") { IsAdvanced = true, Tip = "Where the fan points: -90° straight up, 0° to the right." },
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.Sparks, GpuOnly = true, Description = "Sparks and tumbling debris flying out in 3D, falling, cooling, lighting what is under them.",
        });

        Register(new(AnimeLines, "Anime Lines", "3D VFX", EffectZone.Look,
            [
                new("style", "Style", ParamKind.Choice) { TextDefault = "impact", Choices = [("impact", "Impact (Radial)"), ("across", "Speed (Across)"), ("down", "Speed (Down)")] },
                PointAt("x", "Center X", 50, across: true),
                PointAt("y", "Center Y", 50, across: false),
                new("count", "Lines", Min: 10, Max: 400, Default: 140, Step: 5) { Tip = "How many lines." },
                new("thick", "Thickness", Min: 0.2, Max: 4, Default: 1, Animation: ParamAnimation.Expression),
                new("clear", "Clear Middle", Min: 0, Max: 150, Default: 55, Step: 1, Unit: "%", Animation: ParamAnimation.Expression) { Tip = "How much of the middle the lines leave alone, in % of the picture's height." },
                new("rate", "Drawings a Second", Min: 2, Max: 60, Default: 12, Step: 1) { Tip = "How often the lines are drawn anew: 12 is on twos, as animation is." },
                new("opacity", "Opacity", Min: 0, Max: 100, Default: 100, Step: 1, Unit: "%", Animation: ParamAnimation.Expression),
                new("color", "Line Color", ParamKind.Color) { TextDefault = "#FFFFFF" },
                LightSpill(0.1),
                Seed(),
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.AnimeLines, GpuOnly = true, Description = "Hand-drawn looking impact or speed lines, new ones several times a second.",
        });

        Register(new(EnergyShield, "Energy Shield", "3D VFX", EffectZone.Look,
            [
                new("rise", "Rises In", Min: 0.05, Max: 5, Default: 0.6, Step: 0.05, Unit: "s") { GpuProgress = true, Tip = "How long it takes to come up, from the start of its clip." },
                PointAt("x", "Center X", 50, across: true),
                PointAt("y", "Center Y", 50, across: false),
                new("radius", "Radius", Min: 5, Max: 150, Default: 42, Step: 1, Unit: "%", Animation: ParamAnimation.Expression) { Tip = "In % of the picture's height." },
                new("color", "Shield Color", ParamKind.Color) { TextDefault = "#3CC8FF" },
                new("hex", "Hexagons", Default: 0.8, Animation: ParamAnimation.Expression) { Tip = "How much the hexagon cells show." },
                LightSpill(0.5),
                new("pulse", "Pulse Rate", Min: 0, Max: 4, Default: 0.8) { IsAdvanced = true, Tip = "Waves of light across it, each second." },
                Seed(),
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.EnergyField, GpuOnly = true, Description = "A dome of hexagons with a bright rim, lighting what is round it.",
        });

        Register(new(Saber, "Saber Glow", "3D VFX", EffectZone.Look,
            [
                new("ignite", "Ignites In", Min: 0.02, Max: 3, Default: 0.25, Step: 0.01, Unit: "s") { GpuProgress = true, Tip = "How long the blade takes to come out, from the start of its clip." },
                PointAt("x1", "Hilt X", 35, across: true),
                PointAt("y1", "Hilt Y", 80, across: false),
                PointAt("x2", "Tip X", 65, across: true),
                PointAt("y2", "Tip Y", 20, across: false),
                new("color", "Blade Color", ParamKind.Color) { TextDefault = "#32FF6A" },
                LightSpill(0.6),
                new("width", "Core Width", Min: 1, Max: 60, Default: 9, Step: 0.5, Unit: "px", Animation: ParamAnimation.Expression),
                new("glow", "Glow", Min: 0, Max: 3, Default: 1, Animation: ParamAnimation.Expression),
                new("flicker", "Flicker", Default: 0.3) { IsAdvanced = true },
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.EnergyField, GpuVariant = 1, GpuOnly = true, Description = "A blade of light with a white core: keyframe its two ends to swing it. Lights what is under it.",
        });
    }
}
