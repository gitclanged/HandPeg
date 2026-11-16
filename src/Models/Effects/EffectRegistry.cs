using System.Runtime.InteropServices;

namespace HandPegApp.Models.Effects;

/// <summary>
/// Processing zone in a clip's chain. Effects reorder within a zone but never across zones (keys/masks after colour, composite after
/// matte, motion last).
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

/// <summary>Preview cost class: green negligible, amber noticeable at large frames, red slows scrubbing.</summary>
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
/// Animation support: keyframed (Expression and Command are legacy names, both evaluated per frame by the compositor) or static.
/// </summary>
public enum ParamAnimation
{
    None,
    Expression,
    Command,
}

/// <summary>
/// Compositor pixel shader id referenced by a <see cref="GpuEffectPacket"/> for an effect or transition.
/// </summary>
/// <remarks>Compositor contract: values are fixed, new slots append, nothing is renumbered.</remarks>
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

    /// <summary>Reserved: despill pass after Chroma Key, driven by the key packet's fourth lane.</summary>
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

    // GPU-only effects (EffectDefinition.GpuOnly; Motion.hlsl, Light.hlsl, Vfx.hlsl).

    /// <summary>Optical flow between frames, and sub-frames of the layer's own moves: a real shutter.</summary>
    MotionBlur = 22,

    /// <summary>A dual-Kawase glow in linear light, its halo tinted as film's halation is.</summary>
    HalationGlow = 23,

    /// <summary>Variant in the last lane: 0 Neon Rim Light (distance field of layer shape), 1 Frosted Glass (blurred backdrop).</summary>
    RimGlass = 24,

    /// <summary>Variant in the last lane: 0 Impact Shockwave (ripple and shake), 1 Anamorphic Lens (streaks, barrel, fringe).</summary>
    ShockwaveLens = 25,

    /// <summary>A raymarched fireball through a baked 3D noise (two packets).</summary>
    Explosion = 26,

    /// <summary>Sparks and debris in 3D, drawn as particles (two packets).</summary>
    Sparks = 27,

    /// <summary>Anime impact and speed lines (two packets).</summary>
    AnimeLines = 28,

    /// <summary>Variant in the second packet's last lane: 0 Energy Shield, 1 Saber Glow.</summary>
    EnergyField = 29,

    /// <summary>
    /// Color Studio grade (EffectRegistry.ColorStudio): linear-light white balance and exposure, lift/gamma/gain, contrast, saturation,
    /// vibrance (reference: ColorGradeMath.Apply), then curves (Aux) and a mix-weighted LUT. Two packets.
    /// </summary>
    ColorStudio = 30,

    // Transitions (TransitionStyles.GpuSlot): a block of their own.
    TransitionDissolve = 64,
    TransitionWipe = 65,
    TransitionSlide = 66,
    TransitionZoom = 67,
    TransitionBlurDissolve = 68,

    /// <summary>The Thanos Snap (ParticleSnap.hlsl): the picture breaks into embers on a wind.</summary>
    TransitionParticleSnap = 69,

    // Social cut effects (SocialCuts.hlsl): one pass each.
    TransitionWhipPan = 70,
    TransitionZoomWarp = 71,
    TransitionRgbGlitch = 72,
    TransitionSpin = 73,
}

/// <summary>
/// Effect values at one time for the compositor control plane: shader slot plus eight float lanes, blittable (36 bytes) into an HLSL
/// cbuffer. Lanes follow parameter order (<see cref="EffectParam.GpuLanes"/>); more than eight spill into consecutive packets of the same slot.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuEffectPacket(GpuShaderSlot Slot, float P0, float P1, float P2, float P3, float P4, float P5, float P6, float P7)
{
    public const int Lanes = 8;

    public const int SizeInBytes = sizeof(int) + Lanes * sizeof(float);

    /// <summary>
    /// Sentinel in the last lane of an Audio Reactive Pulse packet (<see cref="EffectDefinition.GpuPulsePacket"/>), chosen to differ from
    /// every legitimate last-lane value of those slots, so the compositor pairs a following packet with its card only when marked and
    /// adjacent same-slot cards still render separately.
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

/// <param name="Key">Storage key.</param>
/// <param name="Step">Slider step; also determines the value box's decimals.</param>
public sealed record EffectParam(
    string Key, string Label, ParamKind Kind = ParamKind.Number, double Min = 0, double Max = 1, double Default = 0,
    double Step = 0.01, string Unit = "", ParamAnimation Animation = ParamAnimation.None)
{
    /// <summary>
    /// Bound Layer property for effects whose settings predate stacks (key, mask, edges, shadow); Layer Properties, layout pane and mask
    /// tools still use it.
    /// </summary>
    public string? Bind
    {
        get => _bind;
        init => (_bind, _binding) = (value, LayerBinding.Of(value));
    }

    private readonly string? _bind;
    private readonly LayerBinding? _binding;

    /// <summary>Cached accessor for <see cref="Bind"/>; null when unbound.</summary>
    public LayerBinding? Binding => _binding;

    /// <summary>For a choice: the values, as FFmpeg names them, each with what the card shows.</summary>
    public IReadOnlyList<(string Value, string Label)> Choices { get; init; } = [];

    public string TextDefault { get; init; } = "";

    public string Tip { get; init; } = "";

    /// <summary>
    /// Initial value for a newly added card so its effect is immediately visible. Null uses <see cref="Default"/>, which is also the
    /// value for missing saved data (usually neutral).
    /// </summary>
    public double? Starting { get; init; }

    /// <summary>The words a card the user adds starts with (a choice's value); null: <see cref="TextDefault"/>.</summary>
    public string? StartingText { get; init; }

    /// <summary>Shown in the card's collapsed Advanced section.</summary>
    public bool IsAdvanced { get; init; }

    /// <summary>
    /// Edited only in the Color Studio (wheels, curves); the Effects pane card hides it and shows an in-use indicator.
    /// </summary>
    public bool StudioOnly { get; init; }

    /// <summary>
    /// UI-to-shader unit scale for the lane (e.g. Vignette degrees to radians); 1 when units match.
    /// </summary>
    public double GpuScale { get; init; } = 1;

    /// <summary>
    /// Lane carries progress, not the value: seconds since clip (or Range) start divided by the value (a duration), unclamped so shaders
    /// see before/after.
    /// </summary>
    public bool GpuProgress { get; init; }

    public int Decimals => Step >= 1 ? 0 : Step >= 0.1 ? 1 : Step >= 0.01 ? 2 : 3;

    /// <summary>
    /// Canonical stored value: rounded to the step (integers for steps >= 1, else one decimal finer than displayed) and clamped. Used by
    /// both the Effects pane and the Color Studio so values agree.
    /// </summary>
    public double Quantize(double value) =>
        Math.Clamp(Step >= 1 ? Math.Round(value / Step) * Step : Math.Round(value, Decimals + 1), Min, Max);

    /// <summary>
    /// Packet lanes used: number, switch or choice index (<see cref="Choices"/>) = 1; colour = 3 (RGB 0-1); file, text, button = 0
    /// (delivered as textures).
    /// </summary>
    public int GpuLanes => Kind switch
    {
        ParamKind.Number or ParamKind.Toggle or ParamKind.Choice => 1,
        ParamKind.Color => 3,
        _ => 0,
    };
}

/// <summary>
/// Effect definition: name, zone, parameters, cost class and compositor shader slot. Built-ins use the same declarative shape as future
/// plugins. Effects never compile to FFmpeg filters.
/// </summary>
public sealed record EffectDefinition(
    string Type, string Name, string Category, EffectZone Zone, IReadOnlyList<EffectParam> Params, EffectCost Cost)
{
    public string Description { get; init; } = "";

    /// <summary>Geometric effect (e.g. flip): alpha is transformed along with colour.</summary>
    public bool Geometric { get; init; }

    /// <summary>Operates on alpha only (as greyscale).</summary>
    public bool AlphaOnly { get; init; }

    /// <summary>
    /// Layer property enabling a bound effect (ChromaKey, CustomMask, Shadow; "Edges" for corners/feather). Enabling adds the card;
    /// removing the card disables it.
    /// </summary>
    public string? Feature { get; init; }

    /// <summary>Fixed pane row rather than a stack card (opacity, animated by keyframes).</summary>
    public bool Pinned { get; init; }

    public bool Reorderable => Zone is EffectZone.Look or EffectZone.KeyAndMatte;

    /// <summary>Allowed on Sequence Output and adjustment layers (colour effects; not keys, mattes, composites).</summary>
    public bool ForWholePicture => Zone == EffectZone.Look;

    /// <summary>Compositor shader slot; <see cref="GpuShaderSlot.None"/> when unimplemented.</summary>
    public GpuShaderSlot GpuSlot { get; init; }

    /// <summary>Variant id distinguishing effects sharing a slot, sent in the last packet's free last lane.</summary>
    public int GpuVariant { get; init; }

    /// <summary>
    /// Alternate global lane index for the variant, skipped by parameter lanes (Anamorphic Lens uses packet 0's last lane, as the shared
    /// slot expects, and moves Rotation to packet 1). Null: last lane of the last packet.
    /// </summary>
    public int? GpuVariantLane { get; init; }

    /// <summary>
    /// Zero-based index of the Audio Reactive Pulse packet, or null. Sent only while the pulse is active (<see cref="EffectRegistry.PulseInUse"/>),
    /// so inactive cards keep their packet count, and marked with <see cref="GpuEffectPacket.PulseMarker"/> for unambiguous pairing. Used by
    /// Glow, Neon Rim and Impact Shockwave; Anime Lines carries its pulse in its always-present second packet.
    /// </summary>
    public int? GpuPulsePacket { get; init; }

    /// <summary>
    /// GPU-only effect with no FFmpeg equivalent (Law 4); reported as omitted if a non-GPU path is used (<c>GpuExport.LeftOutByFfmpeg</c>).
    /// </summary>
    public bool GpuOnly { get; init; }

    /// <summary>At most one card per clip (applied at composite time).</summary>
    public bool Once { get; init; }

    /// <summary>Total parameter lanes in order (<see cref="EffectParam.GpuLanes"/>).</summary>
    /// <remarks>Indexed loop: evaluated per packet, avoiding enumerator allocation.</remarks>
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
    /// Creates a user-added instance with starting values (<see cref="EffectParam.Starting"/>, <see cref="EffectParam.StartingText"/>) so it
    /// is visible immediately; clip-bound values are left untouched.
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
/// Registry of all effect definitions by type; plugins register declarative templates here.
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

    /// <summary>Brightness &amp; Contrast channel-balance keys (converted colour sliders; PSColorGrade P4/P5).</summary>
    public const string GradeBalanceBlue = "balance_cb", GradeBalanceRed = "balance_cr";

    private static readonly Dictionary<string, EffectDefinition> Definitions = new(StringComparer.Ordinal);

    /// <summary>In the order the Add Effect menu lists them.</summary>
    public static IReadOnlyList<EffectDefinition> All => [.. Definitions.Values];

    /// <summary>Category order in Add Effect menus; unlisted categories follow.</summary>
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

        // "Brightness & Contrast" (type id unchanged for project compatibility). Last two lanes are Cb/Cr gamma balance for PSColorGrade
        // (P4, P5; 0 = none), written only by the Color tab's Convert so converted grades render identically; hidden (StudioOnly).
        Register(new(Grade, "Brightness & Contrast", "Color", EffectZone.Look,
            [
                new("contrast", "Contrast", Min: 0, Max: 2, Default: 1, Animation: ParamAnimation.Expression) { Starting = 1.1 },
                new("brightness", "Brightness", Min: -1, Max: 1, Default: 0, Animation: ParamAnimation.Expression),
                new("saturation", "Saturation", Min: 0, Max: 3, Default: 1, Animation: ParamAnimation.Expression) { Starting = 1.2 },
                new("gamma", "Gamma", Min: 0.1, Max: 3, Default: 1, Animation: ParamAnimation.Expression) { IsAdvanced = true },
                new(GradeBalanceBlue, "Blue Balance", Min: 0, Max: 2, Default: 0, Step: 0.001) { StudioOnly = true },
                new(GradeBalanceRed, "Red Balance", Min: 0, Max: 2, Default: 0, Step: 0.001) { StudioOnly = true },
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

        // Unsharp mask: Strength on the card; Radius and Threshold under Advanced.
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
                // Degrees in the UI; PSVignette takes radians (GpuScale).
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

        // ----- Blur fill settings card -----
        // Pinned first row of the blur-fill stack (not a clip effect); values are Sequence-level (MainViewModel.BlurRadius, BackgroundZoom,
        // BlurPasses, BackgroundDim), rendered by DrawBlurFill.
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
                // Distance on the card; Angle, Softness and Color under Advanced.
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
                    // Append-only: the GPU packet carries the index.
                    Choices = [("normal", "Normal"), ("multiply", "Multiply"), ("screen", "Screen"), ("addition", "Add"), ("overlay", "Overlay"),
                        ("softlight", "Soft Light"), ("hardlight", "Hard Light"), ("difference", "Difference")],
                },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.BlendMode, Description = "How the clip's colours mix with the picture under it. Any mode but Normal works in RGB over the whole frame.",
        });

        RegisterGpuFx();
        RegisterColorStudio();
    }

    // ----- Blur kernel sizing -----
    // PSBoxBlur/PSSharpen sizes (SceneRenderer) derived here from card values in 1080-wide-frame pixels, so host and golden reference
    // agree to the pixel.

    /// <summary>Maximum blur-fill radius in frame pixels (continuous from 0).</summary>
    public const double BackgroundMostRadius = 64;

    /// <summary>
    /// Horizontal and vertical box radii (picture pixels) whose multi-pass box approximates a Gaussian with sigma = radius / 2:
    /// p r (r + 1) / 3 = sigma^2, radius scaled from 1080 to <paramref name="frameWidth"/>. <paramref name="ratio"/> above 1 narrows the
    /// vertical box, below 1 the horizontal. Each at least 1 and clamped to the chroma plane size (0 if too small).
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

    // ----- Sharpen (unsharp mask) -----

    /// <summary>Maximum and initial Sharpen radius (initial = 5 x 5 unsharp at 1080 wide).</summary>
    public const double SharpenMostRadius = 8, SharpenDefaultRadius = 2;

    /// <summary>Unsharp amount at Strength 1 (the initial 0.6 gives 1.2).</summary>
    public const double SharpenAmount = 2;

    /// <summary>
    /// Sharpen box radius in picture pixels: Radius (1080-wide units, max <see cref="SharpenMostRadius"/>) scaled to
    /// <paramref name="frameWidth"/>, min 1, capped at 11 (23 x 23) and by the picture size (0 if too small).
    /// </summary>
    public static int SharpenBox(double radius, int frameWidth, int width, int height)
    {
        radius = double.IsFinite(radius) && radius > 0 ? Math.Min(radius, SharpenMostRadius) : SharpenDefaultRadius;
        var limit = Math.Min(Math.Min(width, height) / 4 - 1, 11);
        return limit < 1 ? 0 : Math.Clamp((int)Math.Round(radius * Math.Max(frameWidth, 1) / 1080.0, MidpointRounding.AwayFromZero), 1, limit);
    }
}
