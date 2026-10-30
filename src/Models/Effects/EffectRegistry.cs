using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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

/// <summary>How a value can be animated: an expression FFmpeg works out for every frame, a command per keyframe, or not at all.</summary>
public enum ParamAnimation
{
    None,
    Expression,
    Command,
}

/// <summary>
/// The v5.0 compositor's shader for an effect or a transition (HandPeg.Compositor.exe, see ARCHITECTURE_CONTRACTS.md): the
/// id a <see cref="GpuEffectPacket"/> names its pixel shader by. The FFmpeg graph is compiled from the same definitions.
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

    // Transitions (TransitionStyles.GpuSlot): a block of their own.
    TransitionDissolve = 64,
    TransitionWipe = 65,
    TransitionSlide = 66,
    TransitionZoom = 67,
    TransitionBlurDissolve = 68,
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
/// One effect: what it is called and where it goes, its parameters, what it costs, and how it becomes filters. Built-in
/// effects are written as plugins will be (declarative parameters, a cost class, a compile that only formats them).
/// </summary>
/// <param name="Compile">The filters for a card's values, in order; null or none when the values do nothing. Null for the
/// effects the graph builder places itself (the key, the mask and edges, the shadow, the blend), whose filters need labels.</param>
public sealed record EffectDefinition(
    string Type, string Name, string Category, EffectZone Zone, IReadOnlyList<EffectParam> Params, EffectCost Cost,
    Func<EffectCompileContext, EffectInstance, IReadOnlyList<string>?>? Compile = null)
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

    /// <summary>The lanes its parameters take, in their order (<see cref="EffectParam.GpuLanes"/>).</summary>
    /// <remarks>Counted by index: it is asked for on every packet, and an enumerator of the list would be a new object each time.</remarks>
    public int GpuLaneCount
    {
        get
        {
            var lanes = 0;
            for (var i = 0; i < Params.Count; i++)
                lanes += Params[i].GpuLanes;
            return lanes;
        }
    }

    /// <summary>How many packets in a row it is sent as: one for every eight lanes; none without a shader.</summary>
    public int GpuPacketCount => GpuSlot == GpuShaderSlot.None ? 0 : Math.Max((GpuLaneCount + GpuEffectPacket.Lanes - 1) / GpuEffectPacket.Lanes, 1);

    public EffectParam? Param(string key) => Params.FirstOrDefault(p => p.Key == key);

    /// <summary>Whether any of its parameters is an Advanced one (the card has the folded section).</summary>
    public bool HasAdvanced => Params.Any(p => p.IsAdvanced);

    /// <summary>
    /// A new card as the user adds it: each value with a starting value (<see cref="EffectParam.Starting"/>,
    /// <see cref="EffectParam.StartingText"/>) written into it, so that it shows at once. A value bound to the layer is the
    /// layer's, and is left alone.
    /// </summary>
    public EffectInstance NewCard()
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

/// <summary>What an effect's compile is told about where it is being built.</summary>
public sealed class EffectCompileContext
{
    /// <summary>The picture it works on, in pixels (a layer as it is built, smaller in Live Preview).</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>The frame being composed, in pixels: what a blur given for a 1080-wide frame is scaled to.</summary>
    public int FrameWidth { get; init; }

    /// <summary>How much smaller than the export Live Preview builds it: pixel distances are scaled by this.</summary>
    public double PixelScale { get; init; } = 1;

    /// <summary>Where the clip's own time 0 falls on the clock of the picture being composed, in seconds.</summary>
    public double ClipStart { get; init; }

    /// <summary>Whether a file named in a value is there (from the state's list, never the disk).</summary>
    public Func<string, bool> Exists { get; init; } = _ => false;

    /// <summary>A path as a filter option takes it, inside single quotes.</summary>
    public Func<string, string> EscapePath { get; init; } = path => path;

    /// <summary>Keyframes as an expression of t (the flat sum of clipped ramps the motion compiler writes), given where the clip begins.</summary>
    public Func<IReadOnlyList<Keyframe>, double, string> KeyExpression { get; init; } = (keys, _) => Number(keys[0].Value);

    /// <summary>
    /// A name no other filter of the graph has, for a filter that sendcmd sends its animated values to (filter@name). The
    /// builder makes a new one for every card it compiles, so the same card compiled for two stretches never shares one.
    /// </summary>
    public string InstanceName { get; init; } = "fx";

    /// <summary>How often a value animated by commands (<see cref="ParamAnimation.Command"/>) is sent between its keyframes.</summary>
    public const double CommandsPerSecond = 12;

    /// <summary>The most commands one value is sent as: a long, slow animation is sampled more sparsely.</summary>
    public const int MostCommands = 720;

    /// <summary>A number as FFmpeg reads it, whatever the machine's language.</summary>
    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A value as a filter option: the number, or, animated, its keyframes as an expression held to the range, in quotes.</summary>
    public string Value(EffectInstance effect, string key, double min, double max) => effect.HasKeys(key)
        ? $"'clip({KeyExpression(effect.GetKeys(key), ClipStart)},{Number(min)},{Number(max)})'"
        : Number(Math.Clamp(effect.Get(key), min, max));

    /// <summary>
    /// A value as a filter option where it can only be animated by commands: its set value, or, animated, the value its
    /// keyframes give where the picture being composed begins (the commands take it on from there).
    /// </summary>
    public string Initial(EffectInstance effect, string key, double min, double max) =>
        Number(Math.Clamp(effect.HasKeys(key) ? EasingCurves.ValueAt(effect.GetKeys(key), Math.Max(-ClipStart, 0)) : effect.Get(key), min, max));

    /// <summary>
    /// The sendcmd filter that animates values of the filter named <paramref name="target"/> (filter@InstanceName) for
    /// the filters that have runtime commands but no per-frame expressions (colorbalance, colortemperature, chromakey);
    /// null when none of them has keyframes. Each value is sent at each of its keyframes and, between two, as often as
    /// <see cref="CommandsPerSecond"/> along its easing (a Hold only at the keyframes); every command is "enter" at a
    /// moment of the clock of the picture being composed and holds until the next, so a seek into the middle settles on
    /// the latest one. Nothing before that picture's start is sent: the filter's own options hold that value (Initial).
    /// </summary>
    /// <param name="values">Each value's key on the card, the filter's name for it, and its range.</param>
    public string? Commands(EffectInstance effect, string target, params (string Key, string Option, double Min, double Max)[] values)
    {
        var moments = new SortedDictionary<double, List<string>>();
        foreach (var (key, option, min, max) in values)
        {
            if (effect.GetKeys(key) is not { Count: > 0 } keys)
                continue;

            var times = new SortedSet<double>(keys.Select(k => k.Time));
            var step = Math.Max(1 / CommandsPerSecond, (keys[^1].Time - keys[0].Time) / MostCommands);
            for (var i = 1; i < keys.Count; i++)
            {
                if (keys[i - 1].Easing == EasingType.Hold || Math.Abs(keys[i].Value - keys[i - 1].Value) < 1e-9)
                    continue;
                for (var at = keys[i - 1].Time + step; at < keys[i].Time - step / 2; at += step)
                    times.Add(Math.Round(at, 3));
            }

            foreach (var time in times)
            {
                var clock = Math.Round(ClipStart + time, 3);
                if (clock <= 0)
                    continue;
                if (!moments.TryGetValue(clock, out var list))
                    moments[clock] = list = [];
                list.Add($"{target}@{InstanceName} {option} {Number(Math.Clamp(EasingCurves.ValueAt(keys, time), min, max))}");
            }
        }

        return moments.Count == 0 ? null
            : $"sendcmd=c='{string.Join(";", moments.Select(m => $"{Number(m.Key)} {string.Join(",", m.Value)}"))}'";
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
    private static readonly string[] Categories = ["Color", "Detail", "Blur", "Look", "Transform", "Key", "Matte", "Composite"];

    /// <summary>Where a category comes in the Add Effect menus.</summary>
    public static int CategoryRank(string category) => Array.IndexOf(Categories, category) is var i and >= 0 ? i : Categories.Length;

    public static EffectDefinition? Find(string? type) => type is not null && Definitions.TryGetValue(type, out var definition) ? definition : null;

    public static void Register(EffectDefinition definition) => Definitions[definition.Type] = definition;

    private static string N(double value) => EffectCompileContext.Number(value);

    static EffectRegistry()
    {
        // ----- Look -----
        Register(new(Flip, "Flip", "Transform", EffectZone.Look,
            [new("h", "Flip Horizontally", ParamKind.Toggle) { Starting = 1 }, new("v", "Flip Vertically", ParamKind.Toggle)], EffectCost.Green,
            (_, fx) => [.. (fx.IsOn("h") ? ["hflip"] : Array.Empty<string>()), .. (fx.IsOn("v") ? ["vflip"] : Array.Empty<string>())])
        {
            GpuSlot = GpuShaderSlot.Flip, Geometric = true, Description = "Mirrors the picture left to right, or upside down.",
        });

        Register(new(Lut, "LUT 3D", "Color", EffectZone.Look,
            [new("file", "LUT File", ParamKind.File) { Tip = "A .cube file whose look is given to the picture." }], EffectCost.Amber,
            (context, fx) => fx.GetText("file").Trim().Trim('"') is { Length: > 0 } file && context.Exists(file) ? [$"lut3d=file='{context.EscapePath(file)}'"] : null)
        {
            GpuSlot = GpuShaderSlot.Lut3D, Description = "A colour look from a .cube file.",
        });

        Register(new(Denoise, "Denoise", "Detail", EffectZone.Look,
            [new("strength", "Strength", Min: 0, Max: 20, Default: 4, Step: 0.5) { Tip = "How much grain is smoothed away (hqdn3d's spatial strength; 4 is its default)." }], EffectCost.Red,
            (_, fx) => fx.Get("strength") is var strength && strength < 0.05 ? null
                : [Math.Abs(strength - 4) < 0.01 ? "hqdn3d" : $"hqdn3d={N(Math.Clamp(strength, 0, 20))}"])
        {
            GpuSlot = GpuShaderSlot.Denoise, Description = "Smooths grain and compression noise, between frames as well as within them.",
        });

        Register(new(Grade, "Color Grade", "Color", EffectZone.Look,
            [
                new("contrast", "Contrast", Min: 0, Max: 2, Default: 1, Animation: ParamAnimation.Expression) { Starting = 1.1 },
                new("brightness", "Brightness", Min: -1, Max: 1, Default: 0, Animation: ParamAnimation.Expression),
                new("saturation", "Saturation", Min: 0, Max: 3, Default: 1, Animation: ParamAnimation.Expression) { Starting = 1.2 },
                new("gamma", "Gamma", Min: 0.1, Max: 3, Default: 1, Animation: ParamAnimation.Expression) { IsAdvanced = true },
            ], EffectCost.Green, CompileGrade)
        {
            GpuSlot = GpuShaderSlot.ColorGrade, Description = "Contrast, brightness, saturation and gamma.",
        });

        Register(new(Hue, "Hue", "Color", EffectZone.Look,
            [
                new("h", "Hue", Min: -180, Max: 180, Default: 0, Step: 1, Unit: "°", Animation: ParamAnimation.Expression) { Starting = 20 },
                new("s", "Saturation", Min: 0, Max: 10, Default: 1, Animation: ParamAnimation.Expression) { Starting = 1.15 },
            ], EffectCost.Green, CompileHue)
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
            ], EffectCost.Green,
            CompileBalance)
        {
            GpuSlot = GpuShaderSlot.ColorBalance, Description = "Pushes the shadows, midtones and highlights towards red, green or blue.",
        });

        Register(new(Temperature, "Color Temperature", "Color", EffectZone.Look,
            [
                new("temperature", "Temperature", Min: 1000, Max: 40000, Default: 6500, Step: 50, Unit: "K", Animation: ParamAnimation.Command) { Starting = 5200 },
                new("mix", "Mix", Default: 1, Animation: ParamAnimation.Command) { IsAdvanced = true },
                new("pl", "Preserve Lightness", Default: 0) { IsAdvanced = true },
            ], EffectCost.Green,
            (context, fx) =>
            {
                var animated = fx.HasKeys("temperature") || fx.HasKeys("mix");
                if (!animated && (Math.Abs(fx.Get("temperature") - 6500) < 1 || fx.Get("mix") < 0.005))
                    return null;

                var options = $"temperature={context.Initial(fx, "temperature", 1000, 40000)}:mix={context.Initial(fx, "mix", 0, 1)}:pl={N(Math.Clamp(fx.Get("pl"), 0, 1))}";
                return animated && context.Commands(fx, "colortemperature", ("temperature", "temperature", 1000, 40000), ("mix", "mix", 0, 1)) is { } commands
                    ? [commands, $"colortemperature@{context.InstanceName}={options}"]
                    : [$"colortemperature={options}"];
            })
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
            ], EffectCost.Green,
            (_, fx) =>
            {
                var preset = fx.GetText("preset");
                var master = fx.GetText("master").Trim();
                var options = new List<string>();
                if (preset is { Length: > 0 } && preset != "none" && CurvePresetName().IsMatch(preset))
                    options.Add($"preset={preset}");
                if (master.Length > 0 && CurvePoints().IsMatch(master))
                    options.Add($"master='{Regex.Replace(Regex.Replace(master, @"\s*/\s*", "/"), @"\s+", " ")}'");
                return options.Count == 0 ? null : [$"curves={string.Join(":", options)}"];
            })
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
            ], EffectCost.Amber, CompileBlur)
        {
            GpuSlot = GpuShaderSlot.BoxBlur, Description = "Softens the picture (a box blur run twice, or as many times as its Passes).",
        });

        Register(new(Sharpen, "Sharpen", "Detail", EffectZone.Look,
            [new("strength", "Strength", Min: 0, Max: 1) { Starting = 0.6 }], EffectCost.Amber,
            (_, fx) => fx.Get("strength") < 0.005 ? null : [$"cas=strength={N(Math.Clamp(fx.Get("strength"), 0, 1))}"])
        {
            GpuSlot = GpuShaderSlot.Sharpen, Description = "Contrast-adaptive sharpening: crisper edges without halos.",
        });

        Register(new(Vignette, "Vignette", "Look", EffectZone.Look,
            [
                new("angle", "Strength", Min: 0, Max: 1.5, Default: Math.PI / 5, Animation: ParamAnimation.Expression)
                    { Starting = 0.44, Tip = "The lens angle, in radians (0.44 is 25 degrees): larger darkens more of the corners." },
                new("round", "Roundness", Min: 0, Max: 100, Default: 100, Step: 1, Unit: "%")
                    { IsAdvanced = true, Tip = "100 % a circle; less, towards the frame's own shape (an ellipse as wide as the picture)." },
                new("feather", "Feather", Min: 5, Max: 100, Default: 100, Step: 1, Unit: "%")
                    { IsAdvanced = true, Tip = "How much of the way out from the middle it darkens over: 100 % all of it (a lens's falloff), less a harder edge nearer the corners." },
            ], EffectCost.Green, CompileVignette)
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
            [new("amount", "Amount", Min: -20, Max: 20, Default: 0, Step: 1, Unit: "px") { Starting = 2, Tip = "Above 0 shrinks what shows (erosion); below 0 spreads it (dilation)." }], EffectCost.Amber,
            (context, fx) =>
            {
                var amount = Math.Clamp(fx.Get("amount"), -20, 20);
                if (Math.Abs(amount) < 0.5)
                    return null;
                var passes = Math.Max((int)Math.Round(Math.Abs(amount) * context.PixelScale), 1);
                return Enumerable.Repeat(amount > 0 ? "erosion" : "dilation", passes).ToList();
            })
        {
            GpuSlot = GpuShaderSlot.MatteChoke, AlphaOnly = true, Description = "Pulls the edge of what shows in, or pushes it out, by so many pixels.",
        });

        Register(new(Smooth, "Matte Smooth", "Matte", EffectZone.KeyAndMatte,
            [new("frames", "Frames", Min: 1, Max: 9, Default: 5, Step: 2) { Tip = "How many frames the edge is averaged over (Hann weights): steadies a key that flickers." }], EffectCost.Red,
            (_, fx) =>
            {
                var frames = (int)Math.Round(Math.Clamp(fx.Get("frames"), 1, 9));
                if (frames % 2 == 0)
                    frames++;
                if (frames <= 1)
                    return null;
                var weights = Enumerable.Range(1, frames).Select(k => N(Math.Pow(Math.Sin(Math.PI * k / (frames + 1)), 2)));
                return [$"tmix=frames={frames}:weights='{string.Join(" ", weights)}'"];
            })
        {
            GpuSlot = GpuShaderSlot.MatteSmooth, AlphaOnly = true, Description = "Averages the transparency over a few frames, so a keyed edge stops crawling.",
        });

        Register(new(Mask, "Mask", "Matte", EffectZone.KeyAndMatte,
            [
                new("edit", "Edit Mask...", ParamKind.Action) { Tip = "Draw a mask, choose a picture, or take one of the whole frame." },
                new("grow", "Grow", Min: -Layer.MaskGrowLimit, Max: Layer.MaskGrowLimit, Default: 0, Step: 1, Unit: "px") { Bind = nameof(Layer.MaskGrow) },
                new("invert", "Invert", ParamKind.Toggle) { Bind = nameof(Layer.MaskInvert) },
                new("offsetX", "Mask Offset X", Min: -Layer.MaskOffsetLimit, Max: Layer.MaskOffsetLimit, Default: 0, Step: 0.1, Unit: "%")
                    { Bind = nameof(Layer.MaskOffsetX), IsAdvanced = true, Tip = "Moves the mask picture left (negative) or right, in percent of the layer's width: a mask nudged to fit, not drawn again." },
                new("offsetY", "Mask Offset Y", Min: -Layer.MaskOffsetLimit, Max: Layer.MaskOffsetLimit, Default: 0, Step: 0.1, Unit: "%")
                    { Bind = nameof(Layer.MaskOffsetY), IsAdvanced = true, Tip = "Moves the mask picture up (negative) or down, in percent of the layer's height." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.LayerMask, Feature = nameof(Layer.CustomMask), Description = "A drawn, picture or whole-frame mask: the layer shows where it is white.",
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

        // ----- The blurred background's own (5.0-Release) -----
        // Not a card: the pinned first row of the background's stack in the Effects pane, its values the sequence's
        // (MainViewModel.BlurRadius, BackgroundZoom, BlurPasses, BackgroundDim), drawn by DrawBlurFill and the builder's [bg].
        Register(new(BackgroundBlur, "Background Blur", "Blur", EffectZone.Look,
            [
                new("radius", "Blur Radius", Min: 0, Max: BackgroundMostRadius, Default: 20, Step: 0.5, Unit: "px")
                    { Tip = "How far the blurred background is blurred, in pixels of the frame: 0 is sharp, and every value between is its own." },
                new("zoom", "Zoom", Min: 100, Max: 400, Default: 100, Step: 1, Unit: "%")
                    { Tip = "How much the background is enlarged about its middle: 100 % just covers the frame." },
                new("passes", "Blur Passes", Min: 1, Max: 4, Default: 2, Step: 1)
                    { IsAdvanced = true, Tip = "How many times its box blur is run: more passes fall off more smoothly, and blur further." },
                new("dim", "Dim", Min: 0, Max: 50, Default: 15, Step: 1, Unit: "%") { IsAdvanced = true, Tip = "How much darker than the video it is (eq's brightness)." },
            ], EffectCost.Amber)
        {
            Pinned = true, Description = "The blurred copy of the main video that fills the frame behind everything: its blur and zoom.",
        });

        // ----- Composite -----
        Register(new(Opacity, "Opacity", "Composite", EffectZone.Composite,
            [new("opacity", "Opacity", Min: 0, Max: 100, Default: 100, Step: 1, Unit: "%", Animation: ParamAnimation.Expression) { Bind = nameof(Layer.Opacity) }], EffectCost.Green)
        {
            GpuSlot = GpuShaderSlot.TransformComposite, Pinned = true, Description = "How solid the layer is; its keyframes are the layer's Opacity keyframes.",
        });

        Register(new(Shadow, "Drop Shadow", "Composite", EffectZone.Composite,
            [
                new("distance", "Distance", Min: 0, Max: 100, Default: LayerState.DefaultShadowDistance, Step: 1, Unit: "px") { Bind = nameof(Layer.ShadowDistance) },
                new("angle", "Angle", Min: 0, Max: 360, Default: LayerState.DefaultShadowAngle, Step: 1, Unit: "\u00B0") { Bind = nameof(Layer.ShadowAngle), IsAdvanced = true },
                new("softness", "Softness", Min: 0, Max: 50, Default: LayerState.DefaultShadowSoftness, Step: 1, Unit: "px") { Bind = nameof(Layer.ShadowSoftness), IsAdvanced = true },
                new("opacity", "Shadow Opacity", Min: 0, Max: 1, Default: 0.5) { Bind = nameof(Layer.ShadowOpacity), IsAdvanced = true },
                new("color", "Shadow Color", ParamKind.Color) { Bind = nameof(Layer.ShadowColor), TextDefault = "#000000", IsAdvanced = true },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.DropShadow, Feature = nameof(Layer.Shadow),
            Description = "The layer's outline in a colour under it: Distance and Angle say where it falls (135\u00B0 is lit from the upper left, so it falls right and down), Softness feathers its edge (0 is hard).",
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
            GpuSlot = GpuShaderSlot.BlendMode, Description = "How the layer's colours mix with the picture under it. Any mode but Normal works in RGB over the whole frame.",
        });
    }

    // ----- Blur and Vignette (5.0-Release: their Advanced values) -----

    /// <summary>The blurred background's widest blur, in pixels of the frame (5.0-Release: from 0, sharp, continuously).</summary>
    public const double BackgroundMostRadius = 64;

    /// <summary>
    /// Blur: a box blur run Passes times (never gblur: an FFmpeg built for a player crashed on it in a layer's chain), as wide
    /// as a Gaussian of half the radius (<see cref="BlurSizes"/>), then the Dim. The same box across and down is boxblur (the
    /// two passes it always was, by default); a different one avgblur, once a pass. PSBoxBlur draws the same boxes.
    /// </summary>
    private static IReadOnlyList<string>? CompileBlur(EffectCompileContext context, EffectInstance fx)
    {
        var filters = new List<string>();
        var radius = Math.Clamp(fx.Get("radius"), 0, 50);
        if (radius >= 0.05)
        {
            var passes = (int)Math.Round(Math.Clamp(fx.Get("passes"), 1, 4));
            var (across, down) = BlurSizes(radius, fx.Get("ratio"), passes, context.FrameWidth, context.Width, context.Height);
            if (across > 0 && down > 0)
            {
                filters.Add(across == down ? $"boxblur={across}:{passes}"
                    : string.Join(",", Enumerable.Repeat($"avgblur=sizeX={across}:sizeY={down}", passes)));
            }
        }

        var dim = Math.Clamp(fx.Get("dim"), 0, 50);
        if (dim >= 0.5)
            filters.Add($"eq=brightness={N(-dim / 100)}");
        return filters.Count > 0 ? filters : null;
    }

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

    /// <summary>
    /// Vignette: FFmpeg's vignette (cos(angle d)^4 by the distance d from the middle over the half-diagonal), its aspect from
    /// the Roundness (<see cref="VignetteAspect"/>). Feathered (below 100 %), the distance is first narrowed to the outer part
    /// of the way out ((d - (1 - feather)) / feather, held to 0..1), which vignette cannot do: geq draws the same factor then,
    /// on the luma and about the chroma's middle as vignette does (its angle, when animated, its keyframes' expression on
    /// geq's clock, T, as vignette's eval=frame follows them on t). PSVignette draws the same in every case.
    /// </summary>
    private static IReadOnlyList<string>? CompileVignette(EffectCompileContext context, EffectInstance fx)
    {
        var animated = fx.HasKeys("angle");
        if (fx.Get("angle") < 0.005 && !animated)
            return null;

        var aspect = VignetteAspect(fx.Get("round"), context.Width, context.Height);
        var feather = Math.Clamp(fx.Get("feather"), 5, 100) / 100;
        if (feather < 0.995)
        {
            // (The keyframes' expression names the clock t, as every filter's does, and nothing else is a lone t in it.)
            var angle = animated
                ? $"clip({Regex.Replace(context.KeyExpression(fx.GetKeys("angle"), context.ClipStart), @"\bt\b", "T")},0,{N(1.5708)})"
                : context.Initial(fx, "angle", 0, 1.5708);
            var d = $"hypot(X-W/2,(Y-H/2)*{N(aspect)})/hypot(W/2,H/2)";
            var factor = $"if(gt({d},1),0,pow(cos({angle}*clip(({d}-{N(1 - feather)})/{N(feather)},0,1)),4))";
            return ["format=yuva444p", $"geq=lum='p(X,Y)*{factor}':cb='(p(X,Y)-128)*{factor}+128':cr='(p(X,Y)-128)*{factor}+128':a='alpha(X,Y)'"];
        }

        var shape = Math.Abs(aspect - 1) < 1e-4 ? "" : $":aspect={N(aspect)}";
        return [$"vignette=angle={context.Value(fx, "angle", 0, 1.5708)}{shape}{(animated ? ":eval=frame" : "")}"];
    }

    /// <summary>
    /// The vignette's aspect for a roundness (0..100 %) on a picture: 1 at 100 % (a circle), the picture's own shape at 0. With
    /// square pixels vignette stretches the distance down by it (its yscale), so the darkening follows an ellipse that wide.
    /// </summary>
    public static double VignetteAspect(double round, int width, int height) =>
        Math.Pow((double)Math.Max(width, 1) / Math.Max(height, 1), 1 - Math.Clamp(double.IsFinite(round) ? round : 100, 0, 100) / 100);

    // Color Balance's values in the order of its ranges (shadows, midtones, highlights), red, green and blue in each.
    private static readonly string[] BalanceKeys = ["rs", "gs", "bs", "rm", "gm", "bm", "rh", "gh", "bh"];

    // The picture at half its values in 16-bit RGB, lifted 2/65535 off black, and back (see CompileBalance).
    private const string BalanceHalve = "lutrgb=r='val/2+2':g='val/2+2':b='val/2+2'", BalanceRestore = "lutrgb=r='(val-2)*2':g='(val-2)*2':b='(val-2)*2'";

    /// <summary>
    /// Color Balance (5.0-AudioAnd3D, the user's Q1): its three tonal ranges weighed by the colour's HSL lightness,
    /// (max + min) / 2, and Preserve Lightness as the new colour's hue and chroma at the original's lightness, with no colour
    /// turned grey. FFmpeg's colorbalance weighs by max + min (twice the lightness), and preserving greys out any colour
    /// with a channel at 0 or 1. Both are mended around the filter, which is used as it is:
    /// <list type="bullet">
    /// <item>it is given the picture at half its values (16-bit RGB, a lutrgb before it and one doubling them back after it),
    /// whose max + min is the whole picture's (max + min) / 2, and half of each value, so each push is the same;</item>
    /// <item>lifted 2/65535 off black there, so no channel is 0; and, preserving, each range's three values are moved together
    /// until the least is 0: an equal push on the three channels is all lightness, which is put back, so the picture is the
    /// same, and no push takes a channel below black, where preservel would grey it.</item>
    /// </list>
    /// The GPU's PSColorBalance (and CompositorReference) is the same math in closed form. Animated by commands: every value
    /// a keyframe touches (preserving, its range's three) is sent at each moment.
    /// </summary>
    private static IReadOnlyList<string>? CompileBalance(EffectCompileContext context, EffectInstance fx)
    {
        var (animated, any) = (false, false);
        foreach (var key in BalanceKeys)
        {
            animated |= fx.HasKeys(key);
            any |= fx.HasKeys(key) || Math.Abs(fx.Get(key)) >= 0.005;
        }

        if (!any)
            return null;

        var preserve = fx.IsOn("pl");
        var start = BalanceAt(fx, Math.Max(-context.ClipStart, 0), preserve);
        var options = string.Join(":", BalanceKeys.Select((key, i) => $"{key}={N(start[i])}"));
        var commands = animated ? BalanceCommands(context, fx, preserve) : null;
        var name = commands is null ? "colorbalance" : $"colorbalance@{context.InstanceName}";
        return commands is null
            ? ["format=gbrp16le", BalanceHalve, $"{name}={options}{(preserve ? ":pl=1" : "")}", BalanceRestore]
            : ["format=gbrp16le", BalanceHalve, commands, $"{name}={options}{(preserve ? ":pl=1" : "")}", BalanceRestore];
    }

    /// <summary>The nine values at a moment of the clip as the filter is given them: held to -1..1, preserving each range moved down to its least, halved.</summary>
    private static double[] BalanceAt(EffectInstance fx, double time, bool preserve)
    {
        var values = new double[BalanceKeys.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = Math.Clamp(fx.HasKeys(BalanceKeys[i]) ? EasingCurves.ValueAt(fx.GetKeys(BalanceKeys[i]), time) : fx.Get(BalanceKeys[i]), -1, 1);

        for (var range = 0; range < 3; range++)
        {
            var least = preserve ? Math.Min(values[3 * range], Math.Min(values[3 * range + 1], values[3 * range + 2])) : 0;
            for (var c = 3 * range; c < 3 * range + 3; c++)
                values[c] = (values[c] - least) / 2;
        }

        return values;
    }

    /// <summary>
    /// The sendcmd that animates Color Balance (as EffectCompileContext.Commands does: each animated value's keyframes and,
    /// between two that differ, so many a second along its easing), with every value of the moment worked out as
    /// <see cref="BalanceAt"/> gives it; null when nothing is sent.
    /// </summary>
    private static string? BalanceCommands(EffectCompileContext context, EffectInstance fx, bool preserve)
    {
        var times = new SortedSet<double>();
        var ranges = new bool[3];
        for (var i = 0; i < BalanceKeys.Length; i++)
        {
            if (fx.GetKeys(BalanceKeys[i]) is not { Count: > 0 } keys)
                continue;

            ranges[i / 3] = true;
            foreach (var key in keys)
                times.Add(key.Time);
            var step = Math.Max(1 / EffectCompileContext.CommandsPerSecond, (keys[^1].Time - keys[0].Time) / EffectCompileContext.MostCommands);
            for (var k = 1; k < keys.Count; k++)
            {
                if (keys[k - 1].Easing == EasingType.Hold || Math.Abs(keys[k].Value - keys[k - 1].Value) < 1e-9)
                    continue;
                for (var at = keys[k - 1].Time + step; at < keys[k].Time - step / 2; at += step)
                    times.Add(Math.Round(at, 3));
            }
        }

        var moments = new List<string>();
        foreach (var time in times)
        {
            var clock = Math.Round(context.ClipStart + time, 3);
            if (clock <= 0)
                continue;

            var values = BalanceAt(fx, time, preserve);
            var sent = new List<string>();
            for (var i = 0; i < BalanceKeys.Length; i++)
            {
                if (fx.HasKeys(BalanceKeys[i]) || (preserve && ranges[i / 3]))
                    sent.Add($"colorbalance@{context.InstanceName} {BalanceKeys[i]} {N(values[i])}");
            }

            moments.Add($"{N(clock)} {string.Join(",", sent)}");
        }

        return moments.Count == 0 ? null : $"sendcmd=c='{string.Join(";", moments)}'";
    }

    private static IReadOnlyList<string>? CompileGrade(EffectCompileContext context, EffectInstance fx)
    {
        var animated = fx.HasKeys("contrast") || fx.HasKeys("brightness") || fx.HasKeys("saturation") || fx.HasKeys("gamma");
        var (contrast, brightness) = (Math.Clamp(fx.Get("contrast"), 0, 2), Math.Clamp(fx.Get("brightness"), -1, 1));
        var (saturation, gamma) = (Math.Clamp(fx.Get("saturation"), 0, 3), Math.Clamp(fx.Get("gamma"), 0.1, 3));
        if (!animated && Math.Abs(contrast - 1) < 0.005 && Math.Abs(brightness) < 0.005 && Math.Abs(saturation - 1) < 0.005 && Math.Abs(gamma - 1) < 0.005)
            return null;

        return [$"eq=contrast={context.Value(fx, "contrast", 0, 2)}:brightness={context.Value(fx, "brightness", -1, 1)}"
                + $":saturation={context.Value(fx, "saturation", 0, 3)}:gamma={context.Value(fx, "gamma", 0.1, 3)}{(animated ? ":eval=frame" : "")}"];
    }

    private static IReadOnlyList<string>? CompileHue(EffectCompileContext context, EffectInstance fx)
    {
        var (hueAnimated, saturationAnimated) = (fx.HasKeys("h"), fx.HasKeys("s"));
        var turns = hueAnimated || Math.Abs(fx.Get("h")) >= 0.05;
        var saturates = saturationAnimated || Math.Abs(fx.Get("s") - 1) >= 0.005;
        if (!turns && !saturates)
            return null;

        var options = new List<string>();
        if (turns)
            options.Add($"h={context.Value(fx, "h", -180, 180)}");
        if (saturates)
            options.Add($"s={context.Value(fx, "s", 0, 10)}");
        return [$"hue={string.Join(":", options)}"];
    }

    [GeneratedRegex(@"^[a-z_]+$")]
    private static partial Regex CurvePresetName();

    [GeneratedRegex(@"^\s*\d*\.?\d+\s*/\s*\d*\.?\d+(\s+\d*\.?\d+\s*/\s*\d*\.?\d+)*\s*$")]
    private static partial Regex CurvePoints();
}
