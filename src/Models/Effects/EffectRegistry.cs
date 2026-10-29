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
        Opacity = "builtin.composite.opacity", Shadow = "builtin.composite.shadow", BlendMode = "builtin.composite.blend";

    private static readonly Dictionary<string, EffectDefinition> Definitions = new(StringComparer.Ordinal);

    /// <summary>In the order the Add Effect menu lists them.</summary>
    public static IReadOnlyList<EffectDefinition> All => [.. Definitions.Values];

    public static EffectDefinition? Find(string? type) => type is not null && Definitions.TryGetValue(type, out var definition) ? definition : null;

    public static void Register(EffectDefinition definition) => Definitions[definition.Type] = definition;

    private static string N(double value) => EffectCompileContext.Number(value);

    static EffectRegistry()
    {
        // ----- Look -----
        Register(new(Flip, "Flip", "Transform", EffectZone.Look,
            [new("h", "Flip Horizontally", ParamKind.Toggle), new("v", "Flip Vertically", ParamKind.Toggle)], EffectCost.Green,
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
                new("contrast", "Contrast", Min: 0, Max: 2, Default: 1, Animation: ParamAnimation.Expression),
                new("brightness", "Brightness", Min: -1, Max: 1, Default: 0, Animation: ParamAnimation.Expression),
                new("saturation", "Saturation", Min: 0, Max: 3, Default: 1, Animation: ParamAnimation.Expression),
                new("gamma", "Gamma", Min: 0.1, Max: 3, Default: 1, Animation: ParamAnimation.Expression),
            ], EffectCost.Green, CompileGrade)
        {
            GpuSlot = GpuShaderSlot.ColorGrade, Description = "Contrast, brightness, saturation and gamma.",
        });

        Register(new(Hue, "Hue", "Color", EffectZone.Look,
            [
                new("h", "Hue", Min: -180, Max: 180, Default: 0, Step: 1, Unit: "°", Animation: ParamAnimation.Expression),
                new("s", "Saturation", Min: 0, Max: 10, Default: 1, Animation: ParamAnimation.Expression),
            ], EffectCost.Green, CompileHue)
        {
            GpuSlot = GpuShaderSlot.HueSaturation, Description = "Turns every colour round the colour wheel.",
        });

        string[] balanceKeys = ["rs", "gs", "bs", "rm", "gm", "bm", "rh", "gh", "bh"];
        Register(new(Balance, "Color Balance", "Color", EffectZone.Look,
            [
                new("rs", "Shadows Red", Min: -1, Max: 1, Animation: ParamAnimation.Command), new("gs", "Shadows Green", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("bs", "Shadows Blue", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("rm", "Midtones Red", Min: -1, Max: 1, Animation: ParamAnimation.Command), new("gm", "Midtones Green", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("bm", "Midtones Blue", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("rh", "Highlights Red", Min: -1, Max: 1, Animation: ParamAnimation.Command), new("gh", "Highlights Green", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("bh", "Highlights Blue", Min: -1, Max: 1, Animation: ParamAnimation.Command),
                new("pl", "Preserve Lightness", ParamKind.Toggle),
            ], EffectCost.Green,
            (context, fx) =>
            {
                // Animated by commands: every value the keyframes touch is written, from where they start, and sent on.
                var animated = balanceKeys.Where(fx.HasKeys).ToList();
                var set = balanceKeys.Where(k => animated.Contains(k) || Math.Abs(fx.Get(k)) >= 0.005).Select(k => $"{k}={context.Initial(fx, k, -1, 1)}").ToList();
                if (set.Count == 0)
                    return null;
                if (fx.IsOn("pl"))
                    set.Add("pl=1");
                if (animated.Count == 0)
                    return [$"colorbalance={string.Join(":", set)}"];

                var commands = context.Commands(fx, "colorbalance", [.. animated.Select(k => (k, k, -1.0, 1.0))]);
                return commands is null
                    ? [$"colorbalance={string.Join(":", set)}"]
                    : [commands, $"colorbalance@{context.InstanceName}={string.Join(":", set)}"];
            })
        {
            GpuSlot = GpuShaderSlot.ColorBalance, Description = "Pushes the shadows, midtones and highlights towards red, green or blue.",
        });

        Register(new(Temperature, "Color Temperature", "Color", EffectZone.Look,
            [
                new("temperature", "Temperature", Min: 1000, Max: 40000, Default: 6500, Step: 50, Unit: "K", Animation: ParamAnimation.Command),
                new("mix", "Mix", Default: 1, Animation: ParamAnimation.Command),
                new("pl", "Preserve Lightness", Default: 0),
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
                    TextDefault = "none",
                    Choices =
                    [
                        ("none", "None"), ("lighter", "Lighter"), ("darker", "Darker"), ("increase_contrast", "Increase Contrast"),
                        ("linear_contrast", "Linear Contrast"), ("medium_contrast", "Medium Contrast"), ("strong_contrast", "Strong Contrast"),
                        ("cross_process", "Cross Process"), ("vintage", "Vintage"), ("color_negative", "Color Negative"), ("negative", "Negative"),
                    ],
                },
                new("master", "Master Curve", ParamKind.Text) { Tip = "Points of the curve as input/output pairs from 0 to 1, e.g. 0/0 0.5/0.6 1/1. Applied after the preset." },
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
            [new("radius", "Radius", Min: 0, Max: 50, Default: 0, Step: 0.5, Unit: "px") { Tip = "In pixels of a 1080-wide frame, scaled to the frame being made." }], EffectCost.Amber,
            (context, fx) =>
            {
                var radius = Math.Clamp(fx.Get("radius"), 0, 50);
                if (radius < 0.05)
                    return null;

                // A box blur run twice, as wide as a Gaussian of half the radius (never gblur: libmpv's FFmpeg crashes on it
                // in a layer's chain), held to what the colour planes of a picture this size allow.
                var sigma = Math.Max(radius * context.FrameWidth / 1080.0 / 2, 0.2);
                var box = (int)Math.Round((Math.Sqrt(6 * sigma * sigma + 1) - 1) / 2);
                box = Math.Min(Math.Max(box, 1), Math.Max(Math.Min(context.Width, context.Height) / 4 - 1, 0));
                return box > 0 ? [$"boxblur={box}:2"] : null;
            })
        {
            GpuSlot = GpuShaderSlot.BoxBlur, Description = "Softens the picture (a box blur run twice).",
        });

        Register(new(Sharpen, "Sharpen", "Detail", EffectZone.Look,
            [new("strength", "Strength", Min: 0, Max: 1)], EffectCost.Amber,
            (_, fx) => fx.Get("strength") < 0.005 ? null : [$"cas=strength={N(Math.Clamp(fx.Get("strength"), 0, 1))}"])
        {
            GpuSlot = GpuShaderSlot.Sharpen, Description = "Contrast-adaptive sharpening: crisper edges without halos.",
        });

        Register(new(Vignette, "Vignette", "Look", EffectZone.Look,
            [new("angle", "Strength", Min: 0, Max: 1.5, Default: Math.PI / 5, Animation: ParamAnimation.Expression) { Tip = "The lens angle: larger darkens more of the corners." }], EffectCost.Green,
            (context, fx) => fx.Get("angle") < 0.005 && !fx.HasKeys("angle") ? null
                : [$"vignette=angle={context.Value(fx, "angle", 0, 1.5708)}{(fx.HasKeys("angle") ? ":eval=frame" : "")}"])
        {
            GpuSlot = GpuShaderSlot.Vignette, Description = "Darkens the corners, as an old lens does.",
        });

        // ----- Key & Matte -----
        Register(new(ChromaKey, "Chroma Key", "Key", EffectZone.KeyAndMatte,
            [
                new("color", "Key Color", ParamKind.Color) { Bind = nameof(Layer.ChromaColor), TextDefault = "#00FF00" },
                new("similarity", "Similarity", Min: 0.01, Max: 1, Default: 0.15, Animation: ParamAnimation.Command) { Bind = nameof(Layer.ChromaSimilarity) },
                new("blend", "Blend", Min: 0, Max: 1, Default: 0.05, Animation: ParamAnimation.Command) { Bind = nameof(Layer.ChromaBlend) },
                new("despill", "Despill", Min: 0, Max: 1, Default: 0) { Tip = "Takes the key colour's spill off the edges that remain (a linear despill)." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.ChromaKey, Feature = nameof(Layer.ChromaKey), Description = "Makes one colour transparent: a green or blue screen.",
        });

        Register(new(Choke, "Matte Choke", "Matte", EffectZone.KeyAndMatte,
            [new("amount", "Amount", Min: -20, Max: 20, Default: 0, Step: 1, Unit: "px") { Tip = "Above 0 shrinks what shows (erosion); below 0 spreads it (dilation)." }], EffectCost.Amber,
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
                    { Bind = nameof(Layer.MaskOffsetX), Tip = "Moves the mask picture left (negative) or right, in percent of the layer's width: a mask nudged to fit, not drawn again." },
                new("offsetY", "Mask Offset Y", Min: -Layer.MaskOffsetLimit, Max: Layer.MaskOffsetLimit, Default: 0, Step: 0.1, Unit: "%")
                    { Bind = nameof(Layer.MaskOffsetY), Tip = "Moves the mask picture up (negative) or down, in percent of the layer's height." },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.LayerMask, Feature = nameof(Layer.CustomMask), Description = "A drawn, picture or whole-frame mask: the layer shows where it is white.",
        });

        Register(new(Edges, "Rounded & Feathered Edges", "Matte", EffectZone.KeyAndMatte,
            [
                new("radius", "Corner Radius", Min: 0, Max: 50, Default: 0, Step: 1, Unit: "%") { Bind = nameof(Layer.CornerRadius) },
                new("feather", "Soft Edges", ParamKind.Toggle) { Bind = nameof(Layer.Feather) },
                new("featherRadius", "Feather Radius", Min: 1, Max: 100, Default: 12, Step: 1, Unit: "px") { Bind = nameof(Layer.FeatherRadius) },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.CornerFeather, Feature = "Edges", Description = "Rounds the corners, and lets the outline fade out.",
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
                new("angle", "Angle", Min: 0, Max: 360, Default: LayerState.DefaultShadowAngle, Step: 1, Unit: "\u00B0") { Bind = nameof(Layer.ShadowAngle) },
                new("softness", "Softness", Min: 0, Max: 50, Default: LayerState.DefaultShadowSoftness, Step: 1, Unit: "px") { Bind = nameof(Layer.ShadowSoftness) },
                new("opacity", "Shadow Opacity", Min: 0, Max: 1, Default: 0.5) { Bind = nameof(Layer.ShadowOpacity) },
                new("color", "Shadow Color", ParamKind.Color) { Bind = nameof(Layer.ShadowColor), TextDefault = "#000000" },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.DropShadow, Feature = nameof(Layer.Shadow),
            Description = "The layer's outline in a colour under it: Distance and Angle say where it falls (135\u00B0 is lit from the upper left, so it falls right and down), Softness feathers its edge (0 is hard).",
        });

        Register(new(BlendMode, "Blend Mode", "Composite", EffectZone.Composite,
            [
                new("mode", "Mode", ParamKind.Choice)
                {
                    TextDefault = "normal",
                    Choices = [("normal", "Normal"), ("multiply", "Multiply"), ("screen", "Screen"), ("addition", "Add"), ("overlay", "Overlay")],
                },
            ], EffectCost.Amber)
        {
            GpuSlot = GpuShaderSlot.BlendMode, Description = "How the layer's colours mix with the picture under it. Any mode but Normal works in RGB over the whole frame.",
        });
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
