using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models;

// ----- The effect stack -----
// A clip's effects are cards in one list, in the order they are applied (within their zone: see EffectZone). The
// filters that were the layer's own before stacks (flips, LUT, denoise, colour, hue, blur, sharpen) are now views onto
// the cards: Layer Properties and the Filters tools read and write the stack through them. The key, the mask, the
// edges and the shadow keep their values on the layer (the layout pane and the mask tools use them); their card says
// where in the stack they work and whether they are on.
public sealed partial class Layer
{
    /// <summary>Raised (as a property name) when anything in the stack changed: a card, its values, its order.</summary>
    public const string EffectsChanged = "Effects";

    /// <summary>The clip's effects, in the order they are applied.</summary>
    public ObservableCollection<EffectInstance> Effects => _effects ??= MakeStack();

    private ObservableCollection<EffectInstance>? _effects;
    private EffectStackWatcher? _effectWatcher;

    /// <summary>Every effect set aside at once (Bypass All on the Effects pane): nothing of the stack is compiled, and nothing is lost.</summary>
    [ObservableProperty] private bool _effectsBypassed;

    partial void OnEffectsBypassedChanged(bool value) => OnPropertyChanged(EffectsChanged);

    private ObservableCollection<EffectInstance> MakeStack()
    {
        var stack = new ObservableCollection<EffectInstance>();
        _effectWatcher = new EffectStackWatcher(stack, OnEffectChanged);
        return stack;
    }

    // ----- Per clip, or the whole track -----
    // What the Layer Properties and Filters tools change is the track's (every clip on it follows, as it always did);
    // what the Effects pane changes is the clip's own. A change made from the pane is raised while IsEchoing is set,
    // and the view model does not copy it to the track.

    [ThreadStatic] private static int _echo;

    /// <summary>A change being raised belongs to this clip alone: the track's other clips do not follow it.</summary>
    public static bool IsEchoing => _echo > 0;

    /// <summary>Makes what is changed inside it the clip's own (the Effects pane's edits).</summary>
    public static EchoScope Echo()
    {
        _echo++;
        return default;
    }

    public readonly struct EchoScope : IDisposable
    {
        public void Dispose() => _echo--;
    }

    // Set while a view property writes its card: the change is the track's, and is raised as such.
    private int _settingView;

    // Set while a whole stack is loaded: one notice at the end instead of one for every card.
    private bool _loadingEffects;

    // ----- The views onto the cards -----

    // The properties that were the layer's own before stacks, each the value of a card. FilterDenoise is the card being there.
    private static readonly (string Property, string Type, string Key)[] Views =
    [
        (nameof(FlipHorizontal), EffectRegistry.Flip, "h"), (nameof(FlipVertical), EffectRegistry.Flip, "v"),
        (nameof(FilterLut), EffectRegistry.Lut, "file"), (nameof(FilterDenoise), EffectRegistry.Denoise, ""),
        (nameof(FilterContrast), EffectRegistry.Grade, "contrast"), (nameof(FilterBrightness), EffectRegistry.Grade, "brightness"),
        (nameof(FilterSaturation), EffectRegistry.Grade, "saturation"), (nameof(FilterGamma), EffectRegistry.Grade, "gamma"),
        (nameof(FilterHue), EffectRegistry.Hue, "h"), (nameof(FilterBlur), EffectRegistry.Blur, "radius"), (nameof(FilterSharpen), EffectRegistry.Sharpen, "strength"),
    ];

    public bool FlipHorizontal { get => Find(EffectRegistry.Flip)?.IsOn("h") == true; set => SetView(EffectRegistry.Flip, "h", value ? 1 : 0); }

    public bool FlipVertical { get => Find(EffectRegistry.Flip)?.IsOn("v") == true; set => SetView(EffectRegistry.Flip, "v", value ? 1 : 0); }

    public double FilterContrast { get => ViewValue(EffectRegistry.Grade, "contrast"); set => SetView(EffectRegistry.Grade, "contrast", value); }

    public double FilterBrightness { get => ViewValue(EffectRegistry.Grade, "brightness"); set => SetView(EffectRegistry.Grade, "brightness", value); }

    public double FilterSaturation { get => ViewValue(EffectRegistry.Grade, "saturation"); set => SetView(EffectRegistry.Grade, "saturation", value); }

    public double FilterGamma { get => ViewValue(EffectRegistry.Grade, "gamma"); set => SetView(EffectRegistry.Grade, "gamma", value); }

    public double FilterHue { get => ViewValue(EffectRegistry.Hue, "h"); set => SetView(EffectRegistry.Hue, "h", value); }

    public double FilterSharpen { get => ViewValue(EffectRegistry.Sharpen, "strength"); set => SetView(EffectRegistry.Sharpen, "strength", value); }

    /// <summary>How far the picture is blurred, in pixels of a 1080-wide frame; 0 for not at all.</summary>
    public double FilterBlur { get => ViewValue(EffectRegistry.Blur, "radius"); set => SetView(EffectRegistry.Blur, "radius", value); }

    public bool FilterDenoise
    {
        get => Find(EffectRegistry.Denoise) is not null;
        set
        {
            if (value == FilterDenoise)
                return;

            _settingView++;
            try
            {
                if (value)
                    AddEffect(new EffectInstance { Type = EffectRegistry.Denoise });
                else
                    Effects.Remove(Find(EffectRegistry.Denoise)!);
            }
            finally
            {
                _settingView--;
            }
        }
    }

    /// <summary>A .cube file whose look is given to this layer alone.</summary>
    public string FilterLut
    {
        get => Find(EffectRegistry.Lut)?.GetText("file") ?? "";
        set
        {
            value ??= "";
            if (Find(EffectRegistry.Lut) is not { } card)
            {
                if (value.Trim().Length == 0)
                    return;
                card = new EffectInstance { Type = EffectRegistry.Lut };
                card.Texts["file"] = value;
                Write(() => AddEffect(card));
                return;
            }

            Write(() => card.SetText("file", value));
        }
    }

    /// <summary>A Look card that is none of the old filters, switched on: it counts as the layer having filters.</summary>
    private bool HasNewLook
    {
        get
        {
            foreach (var effect in Effects)
            {
                if (effect is { Enabled: true, Definition: { Zone: EffectZone.Look } definition }
                    && definition.Type is not (EffectRegistry.Flip or EffectRegistry.Lut or EffectRegistry.Denoise or EffectRegistry.Grade or EffectRegistry.Hue or EffectRegistry.Blur or EffectRegistry.Sharpen))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private double ViewValue(string type, string key) => Find(type)?.Get(key) ?? EffectRegistry.Find(type)?.Param(key)?.Default ?? 0;

    private void SetView(string type, string key, double value)
    {
        if (Find(type) is { } card)
        {
            Write(() => card.Set(key, value));
            return;
        }

        // A filter left at its default needs no card.
        if (Math.Abs(value - (EffectRegistry.Find(type)?.Param(key)?.Default ?? 0)) < 1e-9)
            return;

        card = new EffectInstance { Type = type };
        card.Values[key] = value;
        Write(() => AddEffect(card));
    }

    private void Write(Action change)
    {
        _settingView++;
        try
        {
            change();
        }
        finally
        {
            _settingView--;
        }
    }

    private void OnEffectChanged(EffectInstance? effect, string key)
    {
        if (_loadingEffects)
            return;

        var echo = _settingView == 0;
        if (echo)
            _echo++;
        try
        {
            if (key != EffectInstance.EnabledKey)
            {
                foreach (var view in Views)
                {
                    if (effect is null || view.Type == effect.Type && (view.Key == key || view.Key.Length == 0))
                        OnPropertyChanged(view.Property);
                }
            }

            OnPropertyChanged(nameof(HasFilterChanges));
            OnPropertyChanged(EffectsChanged);
        }
        finally
        {
            if (echo)
                _echo--;
        }
    }

    // ----- The cards -----

    /// <summary>The first card of a type, or null.</summary>
    public EffectInstance? Find(string type)
    {
        foreach (var effect in Effects)
        {
            if (effect.Type == type)
                return effect;
        }

        return null;
    }

    /// <summary>Whether a card's work is done: it is switched on (or there is no card, for a property that has none) and the stack is not bypassed.</summary>
    public bool FxOn(string type) => !EffectsBypassed && (Find(type)?.Enabled ?? true);

    /// <summary>The blend mode the layer is laid on with, as FFmpeg's blend names it; "normal" for an ordinary overlay.</summary>
    public string BlendModeName => FxOn(EffectRegistry.BlendMode) && Find(EffectRegistry.BlendMode) is { } blend ? blend.GetText("mode") : "normal";

    /// <summary>Puts a card at the bottom of its zone (a missing effect at the very end), and returns it.</summary>
    public EffectInstance AddEffect(EffectInstance effect)
    {
        Effects.Insert(InsertionIndex(Effects, effect), effect);
        return effect;
    }

    /// <summary>Where a new card goes in a stack: after every card of its zone and of the zones before it.</summary>
    public static int InsertionIndex(IList<EffectInstance> stack, EffectInstance effect)
    {
        if (effect.Definition is not { } definition)
            return stack.Count;

        for (var i = 0; i < stack.Count; i++)
        {
            if (stack[i].Definition is { } other && other.Zone > definition.Zone)
                return i;
        }

        return stack.Count;
    }

    /// <summary>
    /// Takes a card off. A card that is a switch of the layer's (the key, the mask, the edges, the shadow) switches it
    /// off as well, as Layer Properties would.
    /// </summary>
    public void RemoveEffect(EffectInstance effect)
    {
        switch (effect.Definition?.Feature)
        {
            case nameof(ChromaKey): ChromaKey = false; break;
            case nameof(CustomMask): CustomMask = false; break;
            case nameof(Shadow): Shadow = false; break;
            case "Edges": (CornerRadius, Feather) = (0, false); break;
        }

        Effects.Remove(effect);
    }

    /// <summary>The card of a switch of the layer's that has just been switched on, at the bottom of its zone if there is none yet.</summary>
    private void EnsureCard(string type)
    {
        if (_effects is not null && !_loadingEffects && Find(type) is null)
            AddEffect(new EffectInstance { Type = type });
    }

    partial void OnChromaKeyChanged(bool value)
    {
        if (value)
            EnsureCard(EffectRegistry.ChromaKey);
    }

    partial void OnCustomMaskChanged(bool value)
    {
        if (value)
            EnsureCard(EffectRegistry.Mask);
    }

    partial void OnCornerRadiusChanged(int value)
    {
        if (value > 0)
            EnsureCard(EffectRegistry.Edges);
    }

    partial void OnFeatherChanged(bool value)
    {
        if (value)
            EnsureCard(EffectRegistry.Edges);
    }

    partial void OnShadowChanged(bool value)
    {
        if (value)
            EnsureCard(EffectRegistry.Shadow);
    }

    /// <summary>Whether a switch of the layer's is on: the property a bound card's <see cref="EffectDefinition.Feature"/> names.</summary>
    private bool FeatureIsOn(string? feature) => feature switch
    {
        nameof(ChromaKey) => ChromaKey,
        nameof(CustomMask) => CustomMask,
        nameof(Shadow) => Shadow,
        "Edges" => CornerRadius > 0 || Feather,
        _ => false,
    };

    /// <summary>Replaces the stack with copies of these cards (Paste, a preset, Undo): one notice for the lot.</summary>
    public void SetEffects(IEnumerable<EffectInstance> effects, bool? bypassed = null)
    {
        _loadingEffects = true;
        try
        {
            Effects.Clear();
            foreach (var effect in effects)
                Effects.Add(effect);
            if (bypassed is { } value)
                EffectsBypassed = value;
            AddMissingFeatureCards();
        }
        finally
        {
            _loadingEffects = false;
        }

        OnEffectChanged(null, "");
    }

    private void AddMissingFeatureCards()
    {
        foreach (var definition in EffectRegistry.All)
        {
            if (definition.Feature is { } feature && FeatureIsOn(feature) && Find(definition.Type) is null)
            {
                var card = new EffectInstance { Type = definition.Type };
                Effects.Insert(InsertionIndex(Effects, card), card);
            }
        }
    }

    /// <summary>
    /// Takes up a saved stack; one saved before stacks is made from the old properties, in the order they were
    /// applied: flips, LUT, denoise, colour, hue, blur, sharpen, then the key, the mask, the edges and the shadow.
    /// </summary>
    private void LoadEffects(LayerState state)
    {
        var cards = state.Effects is { } saved ? saved.Select(EffectInstance.FromState).ToList() : Migrate(state);
        SetEffects(cards, state.EffectsBypassed);
    }

    private static List<EffectInstance> Migrate(LayerState state)
    {
        var cards = new List<EffectInstance>();
        EffectInstance Card(string type)
        {
            var card = new EffectInstance { Type = type };
            cards.Add(card);
            return card;
        }

        if (state.FlipHorizontal || state.FlipVertical)
        {
            var flip = Card(EffectRegistry.Flip);
            (flip.Values["h"], flip.Values["v"]) = (state.FlipHorizontal ? 1 : 0, state.FlipVertical ? 1 : 0);
        }

        if ((state.FilterLut ?? "").Trim().Length > 0)
            Card(EffectRegistry.Lut).Texts["file"] = state.FilterLut!;
        if (state.FilterDenoise)
            Card(EffectRegistry.Denoise);

        var (contrast, brightness) = (Math.Clamp(state.FilterContrast, 0, 2), Math.Clamp(state.FilterBrightness, -1, 1));
        var (saturation, gamma) = (Math.Clamp(state.FilterSaturation, 0, 3), Math.Clamp(state.FilterGamma, 0.1, 3));
        if (Math.Abs(contrast - 1) >= 0.005 || Math.Abs(brightness) >= 0.005 || Math.Abs(saturation - 1) >= 0.005 || Math.Abs(gamma - 1) >= 0.005)
        {
            var grade = Card(EffectRegistry.Grade);
            (grade.Values["contrast"], grade.Values["brightness"], grade.Values["saturation"], grade.Values["gamma"]) = (contrast, brightness, saturation, gamma);
        }

        if (Math.Abs(state.FilterHue) >= 0.05)
            Card(EffectRegistry.Hue).Values["h"] = Math.Clamp(state.FilterHue, -180, 180);
        if (state.FilterBlur >= 0.05)
            Card(EffectRegistry.Blur).Values["radius"] = Math.Clamp(state.FilterBlur, 0, 50);
        if (state.FilterSharpen >= 0.005)
            Card(EffectRegistry.Sharpen).Values["strength"] = Math.Clamp(state.FilterSharpen, 0, 1);

        if (state.ChromaKey)
            Card(EffectRegistry.ChromaKey);
        if (state.CustomMask)
            Card(EffectRegistry.Mask);
        if (state.CornerRadius > 0 || state.Feather)
            Card(EffectRegistry.Edges);
        if (state.Shadow)
            Card(EffectRegistry.Shadow);
        return cards;
    }

    // ----- Cards that carry their layer's values (copy, paste, presets) -----

    private static readonly string[] MaskExtras = [nameof(MaskPath), nameof(MaskShapes), nameof(MaskFrame)];

    /// <summary>
    /// A card as Copy Stack and presets keep it: a bound card's values (the key colour, the mask's file and shapes)
    /// are written into it, so that pasting it gives another clip the same look.
    /// </summary>
    public EffectState CaptureCard(EffectInstance effect)
    {
        var copy = effect.Clone();
        if (effect.Definition is { } definition)
        {
            foreach (var bind in definition.Params.Select(p => p.Bind).OfType<string>().Concat(definition.Type == EffectRegistry.Mask ? MaskExtras : []))
            {
                switch (typeof(Layer).GetProperty(bind)?.GetValue(this))
                {
                    case string text: copy.Texts["@" + bind] = text; break;
                    case bool on: copy.Values["@" + bind] = on ? 1 : 0; break;
                    case int whole: copy.Values["@" + bind] = whole; break;
                    case double number: copy.Values["@" + bind] = number; break;
                }
            }
        }

        return copy.ToState();
    }

    /// <summary>A captured card made into one of this layer's: the values it carries for the layer are set on it, and its switch is turned on.</summary>
    public EffectInstance AdoptCard(EffectState state)
    {
        var card = EffectInstance.FromState(state);
        if (card.Unknown is not null)
            return card;

        foreach (var (key, value) in card.Values.Where(v => v.Key.StartsWith('@')).ToList())
        {
            card.Values.Remove(key);
            var property = typeof(Layer).GetProperty(key[1..]);
            if (property?.PropertyType == typeof(bool))
                property.SetValue(this, value >= 0.5);
            else if (property?.PropertyType == typeof(int))
                property.SetValue(this, (int)Math.Round(value));
            else if (property?.PropertyType == typeof(double))
                property.SetValue(this, value);
        }

        foreach (var (key, value) in card.Texts.Where(v => v.Key.StartsWith('@')).ToList())
        {
            card.Texts.Remove(key);
            if (typeof(Layer).GetProperty(key[1..]) is { PropertyType: var type } property && type == typeof(string))
                property.SetValue(this, value);
        }

        switch (card.Definition?.Feature)
        {
            case nameof(ChromaKey): ChromaKey = true; break;
            case nameof(CustomMask): CustomMask = MaskPath.Trim().Length > 0; break;
            case nameof(Shadow): Shadow = true; break;
        }

        return card;
    }
}
