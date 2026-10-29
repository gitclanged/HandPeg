namespace HandPegApp.Models.Effects;

/// <summary>
/// A property of the layer that an effect's parameter keeps its value in (<see cref="EffectParam.Bind"/>), read and
/// written without reflection (Pass 4.3-0B): a number (a switch as 0 or 1, a whole number as itself) or a text. A
/// parameter finds its binding once, when it is made, so the compositor's per-frame packets
/// (<see cref="EffectInstance.EvaluateGpuPacket"/>) only call a delegate. A plugin's parameter can be bound only to a
/// property listed here; one bound to another name is not bound and keeps its value on the card.
/// </summary>
public sealed class LayerBinding
{
    private LayerBinding(string name, Func<Layer, double>? number, Action<Layer, double>? setNumber, Func<Layer, string?>? text, Action<Layer, string>? setText) =>
        (Name, Number, SetNumber, Text, SetText) = (name, number, setNumber, text, setText);

    /// <summary>The property's name on <see cref="Layer"/>.</summary>
    public string Name { get; }

    /// <summary>The value of a number, switch or whole-number property; null for a text property.</summary>
    public Func<Layer, double>? Number { get; }

    /// <summary>Sets a number property: a switch is on from 0.5, a whole number is rounded.</summary>
    public Action<Layer, double>? SetNumber { get; }

    /// <summary>The value of a text property (a colour, a file, the mask's shapes); null for a number property.</summary>
    public Func<Layer, string?>? Text { get; }

    public Action<Layer, string>? SetText { get; }

    /// <summary>The binding of a property by its name; null for a name that is not a bindable property.</summary>
    public static LayerBinding? Of(string? name) => name switch
    {
        nameof(Layer.ChromaColor) => ChromaColor,
        nameof(Layer.ChromaSimilarity) => ChromaSimilarity,
        nameof(Layer.ChromaBlend) => ChromaBlend,
        nameof(Layer.MaskGrow) => MaskGrow,
        nameof(Layer.MaskInvert) => MaskInvert,
        nameof(Layer.MaskOffsetX) => MaskOffsetX,
        nameof(Layer.MaskOffsetY) => MaskOffsetY,
        nameof(Layer.MaskPath) => MaskPath,
        nameof(Layer.MaskShapes) => MaskShapes,
        nameof(Layer.MaskFrame) => MaskFrame,
        nameof(Layer.CornerRadius) => CornerRadius,
        nameof(Layer.Feather) => Feather,
        nameof(Layer.FeatherRadius) => FeatherRadius,
        nameof(Layer.Opacity) => Opacity,
        nameof(Layer.ShadowDistance) => ShadowDistance,
        nameof(Layer.ShadowAngle) => ShadowAngle,
        nameof(Layer.ShadowSoftness) => ShadowSoftness,
        nameof(Layer.ShadowOpacity) => ShadowOpacity,
        nameof(Layer.ShadowColor) => ShadowColor,
        _ => null,
    };

    private static LayerBinding Numeric(string name, Func<Layer, double> get, Action<Layer, double> set) => new(name, get, set, null, null);

    private static LayerBinding Words(string name, Func<Layer, string?> get, Action<Layer, string> set) => new(name, null, null, get, set);

    // The key, the mask (with the three values a captured Mask card carries besides its parameters), the edges, the opacity and the shadow.
    private static readonly LayerBinding
        ChromaColor = Words(nameof(Layer.ChromaColor), static l => l.ChromaColor, static (l, v) => l.ChromaColor = v),
        ChromaSimilarity = Numeric(nameof(Layer.ChromaSimilarity), static l => l.ChromaSimilarity, static (l, v) => l.ChromaSimilarity = v),
        ChromaBlend = Numeric(nameof(Layer.ChromaBlend), static l => l.ChromaBlend, static (l, v) => l.ChromaBlend = v),
        MaskGrow = Numeric(nameof(Layer.MaskGrow), static l => l.MaskGrow, static (l, v) => l.MaskGrow = (int)Math.Round(v)),
        MaskInvert = Numeric(nameof(Layer.MaskInvert), static l => l.MaskInvert ? 1 : 0, static (l, v) => l.MaskInvert = v >= 0.5),
        MaskOffsetX = Numeric(nameof(Layer.MaskOffsetX), static l => l.MaskOffsetX, static (l, v) => l.MaskOffsetX = v),
        MaskOffsetY = Numeric(nameof(Layer.MaskOffsetY), static l => l.MaskOffsetY, static (l, v) => l.MaskOffsetY = v),
        MaskPath = Words(nameof(Layer.MaskPath), static l => l.MaskPath, static (l, v) => l.MaskPath = v),
        MaskShapes = Words(nameof(Layer.MaskShapes), static l => l.MaskShapes, static (l, v) => l.MaskShapes = v),
        MaskFrame = Numeric(nameof(Layer.MaskFrame), static l => l.MaskFrame ? 1 : 0, static (l, v) => l.MaskFrame = v >= 0.5),
        CornerRadius = Numeric(nameof(Layer.CornerRadius), static l => l.CornerRadius, static (l, v) => l.CornerRadius = (int)Math.Round(v)),
        Feather = Numeric(nameof(Layer.Feather), static l => l.Feather ? 1 : 0, static (l, v) => l.Feather = v >= 0.5),
        FeatherRadius = Numeric(nameof(Layer.FeatherRadius), static l => l.FeatherRadius, static (l, v) => l.FeatherRadius = (int)Math.Round(v)),
        Opacity = Numeric(nameof(Layer.Opacity), static l => l.Opacity, static (l, v) => l.Opacity = (int)Math.Round(v)),
        ShadowDistance = Numeric(nameof(Layer.ShadowDistance), static l => l.ShadowDistance, static (l, v) => l.ShadowDistance = (int)Math.Round(v)),
        ShadowAngle = Numeric(nameof(Layer.ShadowAngle), static l => l.ShadowAngle, static (l, v) => l.ShadowAngle = v),
        ShadowSoftness = Numeric(nameof(Layer.ShadowSoftness), static l => l.ShadowSoftness, static (l, v) => l.ShadowSoftness = (int)Math.Round(v)),
        ShadowOpacity = Numeric(nameof(Layer.ShadowOpacity), static l => l.ShadowOpacity, static (l, v) => l.ShadowOpacity = v),
        ShadowColor = Words(nameof(Layer.ShadowColor), static l => l.ShadowColor, static (l, v) => l.ShadowColor = v);
}
