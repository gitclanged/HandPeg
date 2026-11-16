using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models;

// ----- Effect stack -----
// Effects are one ordered list (within zones: EffectZone). Legacy per-clip filters (flips, LUT, denoise, colour, hue, blur, sharpen)
// are view properties over the stack used by Clip Properties and the Filters tools. Key, mask, edges and shadow keep their values on
// the clip (layout pane, mask tools); their effect sets stack position and enabled state.
public sealed partial class Layer
{
    /// <summary>Raised (as a property name) when anything in the stack changed: an effect, its values, its order.</summary>
    public const string EffectsChanged = "Effects";

    /// <summary>The clip's effects, in the order they are applied.</summary>
    public ObservableCollection<EffectInstance> Effects => _effects ??= MakeStack();

    private ObservableCollection<EffectInstance>? _effects;
    private EffectStackWatcher? _effectWatcher;

    /// <summary>Bypass All: the stack is not compiled but preserved.</summary>
    [ObservableProperty] private bool _effectsBypassed;

    partial void OnEffectsBypassedChanged(bool value) => OnPropertyChanged(EffectsChanged);

    private ObservableCollection<EffectInstance> MakeStack()
    {
        var stack = new ObservableCollection<EffectInstance>();
        _effectWatcher = new EffectStackWatcher(stack, OnEffectChanged);
        return stack;
    }

    // ----- Effect changes the layer announces -----

    /// <summary>
    /// True while raising notifications for a direct stack change (card value, switch or range) not made via a view property; the view
    /// model skips look-undo steps for them.
    /// </summary>
    internal bool RaisingEffectChange => _raisingEffect > 0;

    private int _raisingEffect;

    // Set while a view property writes its card: the change is the track's, and is raised as such.
    private int _settingView;

    // Set while a whole stack is loaded: one notice at the end instead of one for every card.
    private bool _loadingEffects;

    // ----- The views onto the cards -----

    // Legacy view properties, each backed by a card value (FilterDenoise = card present).
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

    /// <summary>
    /// True while raising notifications for a parameter value, text or keyframe change (not add/remove/toggle/range); treated as
    /// look-only (<see cref="ViewModels.PreviewChange.Look"/>), with no Timeline row redraw.
    /// </summary>
    internal bool RaisingEffectValue => _raisingEffectValue > 0;

    private int _raisingEffectValue;

    private void OnEffectChanged(EffectInstance? effect, string key)
    {
        if (_loadingEffects)
            return;

        var echo = _settingView == 0;
        if (echo)
            _raisingEffect++;
        var valueOnly = effect is not null && key != EffectInstance.EnabledKey && key != EffectInstance.RangeKey;
        if (valueOnly)
            _raisingEffectValue++;
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
            if (valueOnly)
                _raisingEffectValue--;
            if (echo)
                _raisingEffect--;
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

    /// <summary>True when the card is enabled (or absent for cardless properties) and the stack is not bypassed.</summary>
    public bool FxOn(string type) => !EffectsBypassed && (Find(type)?.Enabled ?? true);

    /// <summary>Blend mode name (FFmpeg blend naming); "normal" for a plain overlay.</summary>
    public string BlendModeName => FxOn(EffectRegistry.BlendMode) && Find(EffectRegistry.BlendMode) is { } blend ? blend.GetText("mode") : "normal";

    /// <summary>
    /// True when the effect may be added (Add Effect on clip or track): adjustment layers and blur fill accept Look effects; others per
    /// kind, with single-instance effects (key, mask, edges, shadow, blend) only if absent unless <paramref name="ignorePresent"/>.
    /// </summary>
    public bool CanTake(EffectDefinition definition, bool ignorePresent = false)
    {
        if (definition.Pinned)
            return false;
        if (IsAdjustment || IsBackground)
            return definition.ForWholePicture;

        var allowed = definition.Zone switch
        {
            EffectZone.Look => HasFilters,
            EffectZone.KeyAndMatte => definition.Type == EffectRegistry.ChromaKey ? HasKeying : HasMask && !IsCaptions,
            EffectZone.Composite => HasStyle && !IsCaptions,
            _ => false,
        };
        return allowed && (ignorePresent || definition.Feature is null && definition.Type != EffectRegistry.BlendMode && !definition.Once || Find(definition.Type) is null);
    }

    /// <summary>
    /// True for video-only companion layers (blur fill, HUD Cutouts, mask pieces); their menus omit audio items.
    /// </summary>
    public bool IsVideoOnlyChild => IsBackground || IsVideo;

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
    /// Removes a card; feature-bound cards (key, mask, edges, shadow) also disable their feature, matching Layer Properties.
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

    /// <summary>Ensures a just-enabled feature has its effect, appending at the end of its zone if missing.</summary>
    private void EnsureEffect(string type)
    {
        if (_effects is not null && !_loadingEffects && Find(type) is null)
            AddEffect(new EffectInstance { Type = type });
    }

    partial void OnChromaKeyChanged(bool value)
    {
        if (value)
            EnsureEffect(EffectRegistry.ChromaKey);
    }

    partial void OnCustomMaskChanged(bool value)
    {
        if (value)
            EnsureEffect(EffectRegistry.Mask);
    }

    partial void OnCornerRadiusChanged(int value)
    {
        if (value > 0)
            EnsureEffect(EffectRegistry.Edges);
    }

    partial void OnFeatherChanged(bool value)
    {
        if (value)
            EnsureEffect(EffectRegistry.Edges);
    }

    partial void OnShadowChanged(bool value)
    {
        if (value)
            EnsureEffect(EffectRegistry.Shadow);
    }

    /// <summary>True when the feature named by <see cref="EffectDefinition.Feature"/> is enabled on the clip.</summary>
    private bool FeatureIsOn(string? feature) => feature switch
    {
        nameof(ChromaKey) => ChromaKey,
        nameof(CustomMask) => CustomMask,
        nameof(Shadow) => Shadow,
        "Edges" => CornerRadius > 0 || Feather,
        _ => false,
    };

    /// <summary>Replaces the stack with copies of these effects (Paste, a preset, Undo): one notice for the lot.</summary>
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
            AddMissingFeatureEffects();
        }
        finally
        {
            _loadingEffects = false;
        }

        OnEffectChanged(null, "");
    }

    private void AddMissingFeatureEffects()
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
    /// Loads a saved stack, or builds one from legacy properties in their original order: flips, LUT, denoise, colour, hue, blur,
    /// sharpen, key, mask, edges, shadow.
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

    // ----- Effects that carry their clip's values (copy, paste, presets) -----

    private static readonly LayerBinding[] MaskExtras = [.. new[] { nameof(MaskPath), nameof(MaskShapes), nameof(MaskFrame) }.Select(LayerBinding.Of).OfType<LayerBinding>()];

    /// <summary>
    /// Captures an effect for Copy Stack and presets, embedding bound clip values (key colour, mask file and shapes) so pasting
    /// reproduces the look. Uses <see cref="EffectParam.Binding"/>, no reflection.
    /// </summary>
    public EffectState CaptureEffect(EffectInstance effect)
    {
        var copy = effect.Clone();
        if (effect.Definition is { } definition)
        {
            foreach (var binding in definition.Params.Select(p => p.Binding).OfType<LayerBinding>().Concat(definition.Type == EffectRegistry.Mask ? MaskExtras : []))
            {
                if (binding.Text is { } text)
                {
                    if (text(this) is { } words)
                        copy.Texts["@" + binding.Name] = words;
                }
                else if (binding.Number is { } number)
                {
                    copy.Values["@" + binding.Name] = number(this);
                }
            }
        }

        return copy.ToState();
    }

    /// <summary>
    /// Applies a captured effect: sets its carried clip values and enables its feature. Only <see cref="LayerBinding"/> properties are
    /// applied; unknown names are dropped.
    /// </summary>
    public EffectInstance AdoptEffect(EffectState state)
    {
        var card = EffectInstance.FromState(state);
        if (card.Unknown is not null)
            return card;

        foreach (var (key, value) in card.Values.Where(v => v.Key.StartsWith('@')).ToList())
        {
            card.Values.Remove(key);
            LayerBinding.Of(key[1..])?.SetNumber?.Invoke(this, value);
        }

        foreach (var (key, value) in card.Texts.Where(v => v.Key.StartsWith('@')).ToList())
        {
            card.Texts.Remove(key);
            LayerBinding.Of(key[1..])?.SetText?.Invoke(this, value);
        }

        switch (card.Definition?.Feature)
        {
            case nameof(ChromaKey): ChromaKey = true; break;
            case nameof(CustomMask): CustomMask = MaskPath.Trim().Length > 0 || MaskShapes.Length > 0; break;
            case nameof(Shadow): Shadow = true; break;
        }

        return card;
    }
}
