using System.Runtime.InteropServices;

namespace HandPegApp.Models.Effects;

/// <summary>
/// Where in a clip's chain an effect works. Effects move freely inside their zone and never between zones: a key or a
/// mask after the colour, the composite after the matte, and the motion last of all (the keyframe compiler's, unchanged).
/// </summary>
public enum EffectZone
{
    /// <summary>Fixed: the region, the speed, deinterlacing.</summary>
    Source,

    /// <summary>Re-orderable: colour, LUTs, blur, sharpening, flips.</summary>
    Look,

    /// <summary>Re-orderable: keys and everything that works on the transparency.</summary>
    KeyAndMatte,

    /// <summary>Fixed: opacity, the drop shadow, the blend mode.</summary>
    Composite,

    /// <summary>Fixed, last: rotation, animated scale, position.</summary>
    Transform,
}

/// <summary>What an effect costs Live Preview: green barely shows, amber is felt on large frames, red slows scrubbing.</summary>
public enum EffectCost
{
    Green,
    Amber,
    Red,
}

public enum ParamKind
{
    Number,
    Toggle,
    Choice,
    File,
    Color,
    Text,

    /// <summary>A button on the card (Edit Mask...), handled by the pane.</summary>
    Action,
}

/// <summary>
/// How a value can be animated: along its keyframes (Expression and Command alike, which named FFmpeg's two ways until its
/// filter strings went: the compositor reads the value at every frame either way), or not at all.
/// </summary>
public enum ParamAnimation
{
    None,
    Expression,
    Command,
}

/// <summary>
/// The v5.0 compositor's shader for an effect or a transition (HandPeg.Compositor.exe, see ARCHITECTURE_CONTRACTS.md): the
/// id a <see cref="GpuEffectPacket"/> names its pixel shader by.
/// </summary>
/// <remarks>A contract with the compositor: the numbers are fixed, new slots are added at the end, and none is renumbered.</remarks>
public enum GpuShaderSlot
{
    /// <summary>Nothing to draw: an effect without a shader, switched off, or outside its stretch of the clip.</summary>
    None = 0,

    /// <summary>The layer's own pass: its transform (the motion keys) and its opacity.</summary>
    TransformComposite = 1,
    Flip = 2,
    Lut3D = 3,
    Denoise = 4,
    ColorGrade = 5,
    ColorBalance = 6,
    ColorTemperature = 7,
    Vignette = 8,

    /// <summary>Reserved: no built-in effect yet.</summary>
    FilmGrain = 9,
    ChromaKey = 10,

    /// <summary>Reserved: the Chroma Key packet carries its despill (its fourth lane), run as this pass after the key.</summary>
    Despill = 11,
    MatteChoke = 12,
    MatteSmooth = 13,
    LayerMask = 14,
    CornerFeather = 15,
    DropShadow = 16,
    BlendMode = 17,
    HueSaturation = 18,
    ToneCurve = 19,
    BoxBlur = 20,
    Sharpen = 21,

    // Pass 5.1-GpuFx: drawn by the compositor alone (EffectDefinition.GpuOnly; Motion.hlsl, Light.hlsl, Vfx.hlsl).

    /// <summary>Optical flow between frames, and sub-frames of the layer's own moves: a real shutter.</summary>
    MotionBlur = 22,

    /// <summary>A dual-Kawase glow in linear light, its halo tinted as film's halation is.</summary>
    HalationGlow = 23,

    /// <summary>A variant in the last lane: 0 the Neon Rim Light (from a distance field of the layer's shape), 1 Frosted Glass (the canvas under it, blurred).</summary>
    RimGlass = 24,

    /// <summary>A variant in the last lane: 0 the Impact Shockwave (a ripple and a camera shake), 1 the Anamorphic Lens (streaks, barrel and colour fringe).</summary>
    ShockwaveLens = 25,

    /// <summary>A raymarched fireball through a baked 3D noise (two packets).</summary>
    Explosion = 26,

    /// <summary>Sparks and debris in 3D, drawn as particles (two packets).</summary>
    Sparks = 27,

    /// <summary>Anime impact and speed lines (two packets).</summary>
    AnimeLines = 28,

    /// <summary>A variant in the last lane of the second packet: 0 the Energy Shield, 1 the Saber Glow (two packets).</summary>
    EnergyField = 29,

    // Transitions (TransitionStyles.GpuSlot): a block of their own.
    TransitionDissolve = 64,
    TransitionWipe = 65,
    TransitionSlide = 66,
    TransitionZoom = 67,
    TransitionBlurDissolve = 68,

    /// <summary>The Thanos Snap (ParticleSnap.hlsl): the picture breaks into embers on a wind.</summary>
    TransitionParticleSnap = 69,
}

/// <summary>
/// One effect's values at one moment, as the v5.0 compositor's shared-memory control plane takes them: a shader and
/// eight lanes, blittable (36 bytes) into an HLSL cbuffer. The lanes are the definition's parameters in their order
/// (<see cref="EffectParam.GpuLanes"/>); an effect with more than eight is several packets in a row with the same slot.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuEffectPacket(GpuShaderSlot Slot, float P0, float P1, float P2, float P3, float P4, float P5, float P6, float P7)
{
    public const int Lanes = 8;

    public const int SizeInBytes = sizeof(int) + Lanes * sizeof(float);

    /// <summary>
    /// The last lane of an Audio Reactive Pulse's own packet (5.2B, <see cref="EffectDefinition.GpuPulsePacket"/>): a value no packet of
    /// its slots has there otherwise (the Glow's Mix is 0 or more, the Impact Shockwave's and the Neon Rim's variant lane 0, the Anamorphic
    /// Lens's 1 and its second packet's last lane 0, the Frosted Glass's 1), so the compositor takes a packet after a card as that card's
    /// pulse only when it carries it, and two cards of one slot side by side are each drawn.
    /// </summary>
    public const float PulseMarker = -1;

    /// <summary>Whether a packet is an Audio Reactive Pulse's own (<see cref="PulseMarker"/> in its last lane).</summary>
    public static bool IsPulsePacket(in GpuEffectPacket packet) => packet.P7 == PulseMarker;

    /// <summary>A packet from a slot and up to eight lanes (the rest 0).</summary>
    public static GpuEffectPacket From(GpuShaderSlot slot, ReadOnlySpan<float> lanes)
    {
        Span<float> l = stackalloc float[Lanes];
        lanes[..Math.Min(lanes.Length, Lanes)].CopyTo(l);
        return new(slot, l[0], l[1], l[2], l[3], l[4], l[5], l[6], l[7]);
    }
}

/// <param name="Key">The name the value is kept under.</param>
/// <param name="Step">The slider's step, which also gives the value box its decimals.</param>
public sealed record EffectParam(
    string Key, string Label, ParamKind Kind = ParamKind.Number, double Min = 0, double Max = 1, double Default = 0,
    double Step = 0.01, string Unit = "", ParamAnimation Animation = ParamAnimation.None)
{
    /// <summary>
    /// A property of the layer that holds this value, for the effects whose settings were the layer's before stacks
    /// (the key, the mask, the edges, the shadow): Layer Properties, the layout pane and the mask tools keep using them.
    /// </summary>
    public string? Bind
    {
        get => _bind;
        init => (_bind, _binding) = (value, LayerBinding.Of(value));
    }

    private readonly string? _bind;
    private readonly LayerBinding? _binding;

    /// <summary>How the bound property is read and written, found once from <see cref="Bind"/>; null when it is not bound.</summary>
    public LayerBinding? Binding => _binding;

    /// <summary>For a choice: the values, as FFmpeg names them, each with what the card shows.</summary>
    public IReadOnlyList<(string Value, string Label)> Choices { get; init; } = [];

    public string TextDefault { get; init; } = "";

    public string Tip { get; init; } = "";

    /// <summary>
    /// The value a card the user adds starts with (Add Effect, a track's Add Effect menu; 5.0-Release, the user's Q1): visible
    /// at once. Null: <see cref="Default"/>, which stays what a value missing from a saved card reads as (mostly "changes nothing").
    /// </summary>
    public double? Starting { get; init; }

    /// <summary>The words a card the user adds starts with (a choice's value); null: <see cref="TextDefault"/>.</summary>
    public string? StartingText { get; init; }

    /// <summary>A deeper control: the Effects pane shows it in the card's folded Advanced section, under the main ones (5.0-Release).</summary>
    public bool IsAdvanced { get; init; }

    /// <summary>
    /// What a number's lane in a <see cref="GpuEffectPacket"/> is multiplied by (5.1-Polish): the card shows one unit, the
    /// shader takes another (the Vignette's degrees as radians). 1 for the most, whose shader takes the card's own value.
    /// </summary>
    public double GpuScale { get; init; } = 1;

    /// <summary>
    /// Its lane is how far through it the card is, not the value (5.1-GpuFx: an effect that runs by itself, its value how many seconds it takes): the seconds
    /// since its clip began (or its Range did) over the value, unclamped, so a shader knows before and after as well.
    /// </summary>
    public bool GpuProgress { get; init; }

    public int Decimals => Step >= 1 ? 0 : Step >= 0.1 ? 1 : Step >= 0.01 ? 2 : 3;

    /// <summary>
    /// How many lanes of a <see cref="GpuEffectPacket"/> it takes: a number, a switch or a choice (its index in
    /// <see cref="Choices"/>) one, a colour three (red, green, blue from 0 to 1); a file, a text or a button none (the
    /// compositor loads those as textures of its own).
    /// </summary>
    public int GpuLanes => Kind switch
    {
        ParamKind.Number or ParamKind.Toggle or ParamKind.Choice => 1,
        ParamKind.Color => 3,
        _ => 0,
    };
}

/// <summary>
/// One effect: what it is called and where it goes, its parameters, what it costs, and the compositor's shader that draws it.
/// Built-in effects are written as plugins will be (declarative parameters, a cost class, a shader slot). No effect becomes an
/// FFmpeg filter any more (5.2-Arm64AndNpu: the filter-string compilers went with the graph builder).
/// </summary>
public sealed record EffectDefinition(
    string Type, string Name, string Category, EffectZone Zone, IReadOnlyList<EffectParam> Params, EffectCost Cost)
{
    public string Description { get; init; } = "";

    /// <summary>Moves pixels (a flip): a still's transparency, set aside while its colour is worked on, is moved with it.</summary>
    public bool Geometric { get; init; }

    /// <summary>Its filters work on the transparency alone (as a grey picture), between the layer's split and alphamerge.</summary>
    public bool AlphaOnly { get; init; }

    /// <summary>
    /// The layer property that switches a bound effect on (ChromaKey, CustomMask, Shadow; "Edges" for corners or a
    /// feather). The card comes when the switch goes on, wherever it was switched; removing the card switches it off.
    /// </summary>
    public string? Feature { get; init; }

    /// <summary>Not a card in the stack: a fixed row of the pane (the opacity, which the keyframe system animates).</summary>
    public bool Pinned { get; init; }

    public bool Reorderable => Zone is EffectZone.Look or EffectZone.KeyAndMatte;

    /// <summary>Whether it can go on the Sequence Output or an adjustment layer: the colour effects, not keys, mattes or composites.</summary>
    public bool ForWholePicture => Zone == EffectZone.Look;

    /// <summary>Its shader in the v5.0 compositor; <see cref="GpuShaderSlot.None"/> for an effect that has none yet.</summary>
    public GpuShaderSlot GpuSlot { get; init; }

    /// <summary>What tells apart two effects of one slot (5.1-GpuFx): sent in the last lane of its last packet, which its parameters leave free.</summary>
    public int GpuVariant { get; init; }

    /// <summary>
    /// Where its variant is sent instead (5.2), a lane counted across its packets that its parameters' lanes go round: the Anamorphic
    /// Lens keeps it in its first packet's last lane, where the Impact Shockwave's slot looks for it, and sends its Rotation in a second
    /// packet. Null: the last lane of its last packet.
    /// </summary>
    public int? GpuVariantLane { get; init; }

    /// <summary>
    /// Its Audio Reactive Pulse's own packet (5.2B), counted from 0, or null for none: sent only while the pulse is in use
    /// (<see cref="EffectRegistry.PulseInUse"/>), so a card at 0 is sent as it was before the pulse (one packet: its passes and the layer's
    /// sixteen as they were), and marked in its last lane (<see cref="GpuEffectPacket.PulseMarker"/>), so the compositor pairs it with its
    /// card and never two cards of one slot side by side. The Glow's, the Neon Rim's and the Impact Shockwave's; the Anime Lines' pulse
    /// is in lanes of the second packet it always sends.
    /// </summary>
    public int? GpuPulsePacket { get; init; }

    /// <summary>
    /// Drawn by the compositor alone, with no FFmpeg filter to fall back on (the roadmap's Law 4): an export FFmpeg has to make leaves it out, and says so
    /// (<c>GpuExport.LeftOutByFfmpeg</c>).
    /// </summary>
    public bool GpuOnly { get; init; }

    /// <summary>A clip takes one card of it (5.1-GpuFx: what is drawn as the layer is laid on).</summary>
    public bool Once { get; init; }

    /// <summary>The lanes its parameters take, in their order (<see cref="EffectParam.GpuLanes"/>).</summary>
    /// <remarks>Counted by index: it is asked for on every packet, and an enumerator of the list would be a new object each time.</remarks>
    public int GpuLaneCount
    {
        get
        {
            var lanes = 0;
            for (var i = 0; i < Params.Count; i++)
                lanes += Params[i].GpuLanes;
            return GpuVariantLane is { } reserved ? Math.Max(lanes + (reserved < lanes ? 1 : 0), reserved + 1) : lanes;
        }
    }

    /// <summary>How many packets in a row it is sent as: one for every eight lanes; none without a shader.</summary>
    public int GpuPacketCount => GpuSlot == GpuShaderSlot.None ? 0 : Math.Max((GpuLaneCount + GpuEffectPacket.Lanes - 1) / GpuEffectPacket.Lanes, 1);

    public EffectParam? Param(string key) => Params.FirstOrDefault(p => p.Key == key);

    /// <summary>Whether any of its parameters is an Advanced one (the card has the folded section).</summary>
    public bool HasAdvanced => Params.Any(p => p.IsAdvanced);

    /// <summary>
    /// A new effect as the user adds it: each value with a starting value (<see cref="EffectParam.Starting"/>,
    /// <see cref="EffectParam.StartingText"/>) written into it, so that it shows at once. A value bound to the clip is the
    /// clip's, and is left alone.
    /// </summary>
    public EffectInstance NewEffect()
    {
        var card = new EffectInstance { Type = Type };
        foreach (var param in Params)
        {
            if (param.Bind is not null)
                continue;
            if (param.Starting is { } value)
                card.Values[param.Key] = Math.Clamp(value, param.Min, param.Max);
            if (param.StartingText is { } text)
                card.Texts[param.Key] = text;
        }

        return card;
    }
}

/// <summary>
/// Every effect HandPeg knows, by type. v5 plugins register here too (declarative templates; see V4_AUDIT_REPORT.md 4.4).
/// </summary>
public static partial class EffectRegistry
{
    // The types the graph builder and the layer refer to by name.
    public const string Flip = "builtin.transform.flip", Lut = "builtin.color.lut3d", Denoise = "builtin.detail.denoise", Grade = "builtin.color.eq",
        Hue = "builtin.color.hue", Balance = "builtin.color.balance", Temperature = "builtin.color.temperature", Curves = "builtin.color.curves",
        Blur = "builtin.blur.box", Sharpen = "builtin.detail.sharpen", Vignette = "builtin.look.vignette",
        ChromaKey = "builtin.key.chroma", Choke = "builtin.matte.choke", Smooth = "builtin.matte.smooth", Mask = "builtin.matte.mask", Edges = "builtin.matte.edges",
        Opacity = "builtin.composite.opacity", Shadow = "builtin.composite.shadow", BlendMode = "builtin.composite.blend",
        BackgroundBlur = "builtin.background.blur";

    private static readonly Dictionary<string, EffectDefinition> Definitions = new(StringComparer.Ordinal);

    /// <summary>In the order the Add Effect menu lists them.</summary>
    public static IReadOnlyList<EffectDefinition> All => [.. Definitions.Values];

    /// <summary>The categories, in the order the Add Effect menus list them (the Effects pane's, a track's); any other after them.</summary>
    private static readonly string[] Categories = ["Color", "Detail", "Blur", "Look", "3D VFX", "Transform", "Key", "Matte", "Composite"];

    /// <summary>Where a category comes in the Add Effect menus.</summary>
    public static int CategoryRank(string category) => Array.IndexOf(Categories, category) is var i and >= 0 ? i : Categories.Length;

    public static EffectDefinition? Find(string? type) => type is not null && Definitions.TryGetValue(type, out var definition) ? definition : null;

    public static void Register(EffectDefinition definition) => Definitions[definition.Type] = definition;

    static EffectRegistry()
    {
        // ----- Look -----
        Register(new(Flip, "Flip", "Transform", EffectZone.Look,
            [new("h", "Flip Horizontally", ParamKind.Toggle) { Starting = 1 }, new("v", "Flip Vertically", ParamKind.Toggle)], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.Flip, Geometric = true, Description = "Mirrors the picture left to right, or upside down.",
        });

        Register(new(Lut, "LUT 3D", "Color", EffectZone.Look,
            [new("file", "LUT File", ParamKind.File) { Tip = "A .cube file whose look is given to the picture." }], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.Lut3D, Description = "A colour look from a .cube file.",
        });

        Register(new(Denoise, "Denoise", "Detail", EffectZone.Look,
            [new("strength", "Strength", Min: 0, Max: 20, Default: 4, Step: 0.5) { Tip = "How much grain is smoothed away (hqdn3d's spatial strength; 4 is its default)." }], EffectCost.Red)
        {
            GpuSlot = GpuShaderSlot.Denoise, Description = "Smooths grain and compression noise, between frames as well as within them.",
        });

        Register(new(Grade, "Color Grade", "Color", EffectZone.Look,
            [
                new("contrast", "Contrast", Min: 0, Max: 2, Default: 1, Animation: ParamAnimation.Expression) { Starting = 1.1 },
                new("brightness", "Brightness", Min: -1, Max: 1, Default: 0, Animation: ParamAnimation.Expression),
                new("saturation", "Saturation", Min: 0, Max: 3, Default: 1, Animation: ParamAnimation.Expression) { Starting = 1.2 },
                new("gamma", "Gamma", Min: 0.1, Max: 3, Default: 1, Animation: ParamAnimation.Expression) { IsAdvanced = true },
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.ColorGrade, Description = "Contrast, brightness, saturation and gamma.",
        });

        Register(new(Hue, "Hue", "Color", EffectZone.Look,
            [
                new("h", "Hue", Min: -180, Max: 180, Default: 0, Step: 1, Unit: "°", Animation: ParamAnimation.Expression) { Starting = 20 },
                new("s", "Saturation", Min: 0, Max: 10, Default: 1, Animation: ParamAnimation.Expression) { Starting = 1.15 },
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.HueSaturation, Description = "Turns every colour round the colour wheel.",
        });

        Register(new(Balance, "Color Balance", "Color", EffectZone.Look,
            [
                new("rs", "Shadows Red", Min: -1, Max: 1, Animation: ParamAnimation.Command) { Starting = -0.05 }, new("gs", "Shadows Green", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("bs", "Shadows Blue", Min: -1, Max: 1, Animation: ParamAnimation.Command) { Starting = 0.1 },
                new("rm", "Midtones Red", Min: -1, Max: 1, Animation: ParamAnimation.Command), new("gm", "Midtones Green", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("bm", "Midtones Blue", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("rh", "Highlights Red", Min: -1, Max: 1, Animation: ParamAnimation.Command) { Starting = 0.1 }, new("gh", "Highlights Green", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("bh", "Highlights Blue", Min: -1, Max: 1, Animation: ParamAnimation.Command) { Starting = -0.05 },
                new("pl", "Preserve Lightness", ParamKind.Toggle) { Starting = 1, IsAdvanced = true },
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.ColorBalance, Description = "Pushes the shadows, midtones and highlights towards red, green or blue.",
        });

        Register(new(Temperature, "Color Temperature", "Color", EffectZone.Look,
            [
                new("temperature", "Temperature", Min: 1000, Max: 40000, Default: 6500, Step: 50, Unit: "K", Animation: ParamAnimation.Command) { Starting = 5200 },
                new("mix", "Mix", Default: 1, Animation: ParamAnimation.Command) { IsAdvanced = true },
                new("pl", "Preserve Lightness", Default: 0) { IsAdvanced = true },
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.ColorTemperature, Description = "Warmer (lower) or cooler (higher) than daylight's 6500 K.",
        });

        Register(new(Curves, "Curves", "Color", EffectZone.Look,
            [
                new("preset", "Preset", ParamKind.Choice)
                {
                    TextDefault = "none", StartingText = "medium_contrast",
                    Choices =
                    [
                        ("none", "None"), ("lighter", "Lighter"), ("darker", "Darker"), ("increase_contrast", "Increase Contrast"),
                        ("linear_contrast", "Linear Contrast"), ("medium_contrast", "Medium Contrast"), ("strong_contrast", "Strong Contrast"),
                        ("cross_process", "Cross Process"), ("vintage", "Vintage"), ("color_negative", "Color Negative"), ("negative", "Negative"),
                    ],
                },
                new("master", "Master Curve", ParamKind.Text) { IsAdvanced = true, Tip = "Points of the curve as input/output pairs from 0 to 1, e.g. 0/0 0.5/0.6 1/1. Applied after the preset." },
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.ToneCurve, Description = "A tone curve: one of FFmpeg's presets, or points of your own.",
        });

        Register(new(Blur, "Blur", "Blur", EffectZone.Look,
            [
                new("radius", "Radius", Min: 0, Max: 50, Default: 0, Step: 0.5, Unit: "px") { Starting = 12, Tip = "In pixels of a 1080-wide frame, scaled to the frame being made." },
                new("passes", "Passes", Min: 1, Max: 4, Default: 2, Step: 1)
                    { IsAdvanced = true, Tip = "How many times the box is run: more passes fall off more smoothly (as a Gaussian does) over the same reach." },
                new("ratio", "Horizontal / Vertical", Min: 0.25, Max: 4, Default: 1, Step: 0.05)
                    { IsAdvanced = true, Tip = "Above 1 blurs less down than across (the radius down is the radius divided by it); below 1 less across than down." },
                new("dim", "Dim", Min: 0, Max: 50, Default: 0, Step: 1, Unit: "%") { IsAdvanced = true, Tip = "Darkens the blurred picture by so much (eq's brightness)." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.BoxBlur, Description = "Softens the picture (a box blur run twice, or as many times as its Passes).",
        });

        // An unsharp mask (5.1-Polish, the user's Q3): its Strength on the card, its Radius and Threshold in the Advanced fold.
        Register(new(Sharpen, "Sharpen", "Detail", EffectZone.Look,
            [
                new("strength", "Strength", Min: 0, Max: 1) { Starting = 0.6, Tip = "How much of the detail is added back: 1 adds twice the difference between the picture and its blur." },
                new("radius", "Radius", Min: 1, Max: SharpenMostRadius, Default: SharpenDefaultRadius, Step: 1, Unit: "px")
                    { IsAdvanced = true, Tip = "How wide the detail it sharpens is: its blur reaches so many pixels of a 1080-wide frame each way (scaled to the frame being made). Small for fine detail, larger for a bolder look." },
                new("threshold", "Threshold", Min: 0, Max: 64, Default: 0, Step: 1)
                    { IsAdvanced = true, Tip = "Differences in brightness smaller than this (in levels of 255) are left as they are, so flat areas and noise are not sharpened." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.Sharpen, Description = "An unsharp mask: the detail of the picture made stronger, with a radius and a threshold of its own.",
        });

        Register(new(Vignette, "Vignette", "Look", EffectZone.Look,
            [
                // In degrees on the card (5.1-Polish, the user's Q5); FFmpeg's vignette and PSVignette take radians (GpuScale).
                new("angle", "Strength", Min: 10, Max: 90, Default: 25, Step: 1, Unit: "°", Animation: ParamAnimation.Expression)
                    { GpuScale = Math.PI / 180, Tip = "The lens angle, in degrees: larger darkens more of the corners." },
                new("round", "Roundness", Min: 0, Max: 100, Default: 100, Step: 1, Unit: "%")
                    { IsAdvanced = true, Tip = "100 % a circle; less, towards the frame's own shape (an ellipse as wide as the picture)." },
                new("feather", "Feather", Min: 5, Max: 100, Default: 100, Step: 1, Unit: "%")
                    { IsAdvanced = true, Tip = "How much of the way out from the middle it darkens over: 100 % all of it (a lens's falloff), less a harder edge nearer the corners." },
            ], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.Vignette, Description = "Darkens the corners, as an old lens does.",
        });

        // ----- Key & Matte -----
        Register(new(ChromaKey, "Chroma Key", "Key", EffectZone.KeyAndMatte,
            [
                new("color", "Key Color", ParamKind.Color) { Bind = nameof(Layer.ChromaColor), TextDefault = "#00FF00" },
                new("similarity", "Similarity", Min: 0.01, Max: 1, Default: 0.15, Animation: ParamAnimation.Command) { Bind = nameof(Layer.ChromaSimilarity) },
                new("blend", "Blend", Min: 0, Max: 1, Default: 0.05, Animation: ParamAnimation.Command) { Bind = nameof(Layer.ChromaBlend) },
                new("despill", "Spill", Min: 0, Max: 1, Default: 0) { IsAdvanced = true, Tip = "Takes the key colour's spill off the edges that remain (a linear despill)." },
                new("choke", "Choke", Min: -20, Max: 20, Default: 0, Step: 1, Unit: "px")
                    { IsAdvanced = true, Tip = "Pulls the key's edge in (above 0) or pushes it out (below 0) by so many pixels, as Matte Choke does." },
                new("smooth", "Smooth", Min: 0, Max: 20, Default: 0, Step: 1, Unit: "px")
                    { IsAdvanced = true, Tip = "Softens the key's edge: its transparency averaged over so many pixels either side (Hann weights)." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.ChromaKey, Feature = nameof(Layer.ChromaKey), Description = "Makes one colour transparent: a green or blue screen.",
        });

        Register(new(Choke, "Matte Choke", "Matte", EffectZone.KeyAndMatte,
            [new("amount", "Amount", Min: -20, Max: 20, Default: 0, Step: 1, Unit: "px") { Starting = 2, Tip = "Above 0 shrinks what shows (erosion); below 0 spreads it (dilation)." }], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.MatteChoke, AlphaOnly = true, Description = "Pulls the edge of what shows in, or pushes it out, by so many pixels.",
        });

        Register(new(Smooth, "Matte Smooth", "Matte", EffectZone.KeyAndMatte,
            [new("frames", "Frames", Min: 1, Max: 9, Default: 5, Step: 2) { Tip = "How many frames the edge is averaged over (Hann weights): steadies a key that flickers." }], EffectCost.Red)
        {
            GpuSlot = GpuShaderSlot.MatteSmooth, AlphaOnly = true, Description = "Averages the transparency over a few frames, so a keyed edge stops crawling.",
        });

        Register(new(Mask, "Mask", "Matte", EffectZone.KeyAndMatte,
            [
                new("edit", "Edit Mask...", ParamKind.Action) { Tip = "Draw a mask, choose a picture, or take one of the whole frame." },
                new("grow", "Grow", Min: -Layer.MaskGrowLimit, Max: Layer.MaskGrowLimit, Default: 0, Step: 1, Unit: "px") { Bind = nameof(Layer.MaskGrow) },
                new("invert", "Invert", ParamKind.Toggle) { Bind = nameof(Layer.MaskInvert) },
                new("offsetX", "Mask Offset X", Min: -Layer.MaskOffsetLimit, Max: Layer.MaskOffsetLimit, Default: 0, Step: 0.1, Unit: "%")
                    { Bind = nameof(Layer.MaskOffsetX), IsAdvanced = true, Tip = "Moves the mask picture left (negative) or right, in percent of the clip's width: a mask nudged to fit, not drawn again." },
                new("offsetY", "Mask Offset Y", Min: -Layer.MaskOffsetLimit, Max: Layer.MaskOffsetLimit, Default: 0, Step: 0.1, Unit: "%")
                    { Bind = nameof(Layer.MaskOffsetY), IsAdvanced = true, Tip = "Moves the mask picture up (negative) or down, in percent of the clip's height." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.LayerMask, Feature = nameof(Layer.CustomMask), Description = "A drawn, picture or whole-frame mask: the clip shows where it is white.",
        });

        Register(new(Edges, "Rounded & Feathered Edges", "Matte", EffectZone.KeyAndMatte,
            [
                new("radius", "Corner Radius", Min: 0, Max: 50, Default: 0, Step: 1, Unit: "%") { Bind = nameof(Layer.CornerRadius) },
                new("feather", "Soft Edges", ParamKind.Toggle) { Bind = nameof(Layer.Feather) },
                new("featherRadius", "Feather Radius", Min: 1, Max: 100, Default: 12, Step: 1, Unit: "px") { Bind = nameof(Layer.FeatherRadius), IsAdvanced = true },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.CornerFeather, Feature = "Edges", Description = "Rounds the corners, and lets the outline fade out.",
        });

        // ----- The blur fill's own (5.0-Release) -----
        // Not an effect of a clip: the pinned first row of the blur fill's stack in the Effects pane, its values the sequence's
        // (MainViewModel.BlurRadius, BackgroundZoom, BlurPasses, BackgroundDim), drawn by DrawBlurFill and the builder's [bg].
        Register(new(BackgroundBlur, "Blur Fill", "Blur", EffectZone.Look,
            [
                new("radius", "Blur Radius", Min: 0, Max: BackgroundMostRadius, Default: 20, Step: 0.5, Unit: "px")
                    { Tip = "How far the blur fill is blurred, in pixels of the frame: 0 is sharp, and every value between is its own." },
                new("zoom", "Zoom", Min: 100, Max: 400, Default: 100, Step: 1, Unit: "%")
                    { Tip = "How much the blur fill is enlarged about its middle: 100 % just covers the frame." },
                new("passes", "Blur Passes", Min: 1, Max: 4, Default: 2, Step: 1)
                    { IsAdvanced = true, Tip = "How many times its box blur is run: more passes fall off more smoothly, and blur further." },
                new("dim", "Dim", Min: 0, Max: 50, Default: 15, Step: 1, Unit: "%") { IsAdvanced = true, Tip = "How much darker than the video it is (eq's brightness)." },
            ], EffectCost.Amber)
        {
            Pinned = true, Description = "The blurred copy of each video clip that fills the frame around it (its Blur Fill): its blur and zoom.",
        });

        // ----- Composite -----
        Register(new(Opacity, "Opacity", "Composite", EffectZone.Composite,
            [new("opacity", "Opacity", Min: 0, Max: 100, Default: 100, Step: 1, Unit: "%", Animation: ParamAnimation.Expression) { Bind = nameof(Layer.Opacity) }], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.TransformComposite, Pinned = true, Description = "How solid the clip is; its keyframes are the clip's Opacity keyframes.",
        });

        Register(new(Shadow, "Drop Shadow", "Composite", EffectZone.Composite,
            [
                new("distance", "Distance", Min: 0, Max: 100, Default: LayerState.DefaultShadowDistance, Step: 1, Unit: "px") { Bind = nameof(Layer.ShadowDistance) },
                new("angle", "Angle", Min: 0, Max: 360, Default: LayerState.DefaultShadowAngle, Step: 1, Unit: "\u00B0") { Bind = nameof(Layer.ShadowAngle), IsAdvanced = true },
                new("softness", "Softness", Min: 0, Max: 50, Default: LayerState.DefaultShadowSoftness, Step: 1, Unit: "px") { Bind = nameof(Layer.ShadowSoftness), IsAdvanced = true },
                // Above the fold with the Distance (5.1-Polish, the user's Q4); Angle, Softness and Color in the Advanced fold.
                new("opacity", "Shadow Opacity", Min: 0, Max: 1, Default: 0.5) { Bind = nameof(Layer.ShadowOpacity) },
                new("color", "Shadow Color", ParamKind.Color) { Bind = nameof(Layer.ShadowColor), TextDefault = "#000000", IsAdvanced = true },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.DropShadow, Feature = nameof(Layer.Shadow),
            Description = "The clip's outline in a colour under it: Distance and Angle say where it falls (135\u00B0 is lit from the upper left, so it falls right and down), Softness feathers its edge (0 is hard).",
        });

        Register(new(BlendMode, "Blend Mode", "Composite", EffectZone.Composite,
            [
                new("mode", "Mode", ParamKind.Choice)
                {
                    TextDefault = "normal", StartingText = "overlay",
                    // Append-only (the GPU packet carries the index): Soft Light, Hard Light and Difference came with 5.0-Core.
                    Choices = [("normal", "Normal"), ("multiply", "Multiply"), ("screen", "Screen"), ("addition", "Add"), ("overlay", "Overlay"),
                        ("softlight", "Soft Light"), ("hardlight", "Hard Light"), ("difference", "Difference")],
                },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.BlendMode, Description = "How the clip's colours mix with the picture under it. Any mode but Normal works in RGB over the whole frame.",
        });

        RegisterGpuFx();
    }

    // ----- Blur (5.0-Release: its Advanced values) -----
    // The sizes the compositor's PSBoxBlur and PSSharpen are drawn at (SceneRenderer): worked out here, from the card's values in
    // pixels of a 1080-wide frame, so the host and the goldens' reference agree on them to the pixel.

    /// <summary>The blur fill's widest blur, in pixels of the frame (5.0-Release: from 0, sharp, continuously).</summary>
    public const double BackgroundMostRadius = 64;

    /// <summary>
    /// A blur's boxes, across and down, in pixels of the picture: so many passes of a box are as wide as a Gaussian of half the
    /// radius (in pixels of a 1080-wide frame, scaled to <paramref name="frameWidth"/>): p r (r + 1) / 3 = sigma squared. Above
    /// 1, <paramref name="ratio"/> narrows the box down (the radius divided by it); below 1 the box across. Each is at least 1
    /// and held to what the colour planes of a picture this size allow (0 when it is too small for any).
    /// </summary>
    public static (int Across, int Down) BlurSizes(double radius, double ratio, int passes, int frameWidth, int width, int height)
    {
        ratio = double.IsFinite(ratio) && ratio > 0 ? Math.Clamp(ratio, 0.25, 4) : 1;
        passes = Math.Clamp(passes, 1, 4);
        var sigma = Math.Max(radius * frameWidth / 1080.0 / 2, 0.2);
        var limit = Math.Max(Math.Min(width, height) / 4 - 1, 0);
        int Box(double s) => Math.Min(Math.Max((int)Math.Round((Math.Sqrt(12 * s * s / passes + 1) - 1) / 2), 1), limit);
        return (Box(sigma * Math.Min(1, ratio)), Box(sigma * Math.Min(1, 1 / ratio)));
    }

    // ----- Sharpen (5.1-Polish, the user's Q3: an unsharp mask) -----

    /// <summary>Sharpen's widest Radius on the card, and the one it starts with (unsharp's own 5 x 5 square on a 1080-wide frame).</summary>
    public const double SharpenMostRadius = 8, SharpenDefaultRadius = 2;

    /// <summary>What the difference is multiplied by at Strength 1 (unsharp's amount; 0.6, the card's start, is 1.2).</summary>
    public const double SharpenAmount = 2;

    /// <summary>
    /// Sharpen's box radius in pixels of the picture: its Radius (in pixels of a 1080-wide frame, at most
    /// <see cref="SharpenMostRadius"/>) scaled to <paramref name="frameWidth"/>, at least 1, and held to unsharp's widest square
    /// (23: a radius of 11) and to what a picture this size allows (0 when it is too small for any).
    /// </summary>
    public static int SharpenBox(double radius, int frameWidth, int width, int height)
    {
        radius = double.IsFinite(radius) && radius > 0 ? Math.Min(radius, SharpenMostRadius) : SharpenDefaultRadius;
        var limit = Math.Min(Math.Min(width, height) / 4 - 1, 11);
        return limit < 1 ? 0 : Math.Clamp((int)Math.Round(radius * Math.Max(frameWidth, 1) / 1080.0, MidpointRounding.AwayFromZero), 1, limit);
    }
}
