using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models.Effects;

/// <summary>
/// One card in an effect stack: which effect it is, whether it is on, its values, the keyframes of the values that
/// are animated, and optionally the stretch of the clip it works in. What the effect does with them is its
/// definition's (<see cref="EffectRegistry"/>); a card whose type this HandPeg does not know is kept as it was read
/// (<see cref="Unknown"/>) and written back unchanged, so a project made with a later plugin survives a round trip.
/// </summary>
public sealed partial class EffectInstance : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The effect, by its namespaced name: "builtin.color.eq", "builtin.blur.box".</summary>
    public string Type { get; init; } = "";

    [ObservableProperty] private bool _enabled = true;

    /// <summary>The numbers and switches (1 is on), by the parameter's key.</summary>
    public Dictionary<string, double> Values { get; } = [];

    /// <summary>The words: a file, a colour, a choice, a curve.</summary>
    public Dictionary<string, string> Texts { get; } = [];

    /// <summary>The keyframes of an animated value, by the parameter's key; times are seconds from where the clip begins.</summary>
    public Dictionary<string, List<Keyframe>> Keys { get; } = [];

    /// <summary>The stretch it works in, in seconds of the clip (of the sequence, for the Sequence Output); null for all of it.</summary>
    public (double Start, double End)? Range { get; set; }

    /// <summary>For a type this HandPeg does not know: the card as it was read. It is shown as missing, does nothing, and is saved as it came.</summary>
    public EffectState? Unknown { get; init; }

    public EffectDefinition? Definition => EffectRegistry.Find(Type);

    public bool IsMissing => Definition is null;

    public string Title => Definition?.Name ?? $"Missing effect ({Type})";

    /// <summary>A value, a text, the keyframes or the switch changed: the key of what changed (<see cref="EnabledKey"/> for the switch).</summary>
    public event Action<EffectInstance, string>? ValueChanged;

    public const string EnabledKey = "$enabled";

    public double Get(string key) => Values.TryGetValue(key, out var value) ? value : Definition?.Param(key)?.Default ?? 0;

    public bool IsOn(string key) => Get(key) >= 0.5;

    public string GetText(string key) => Texts.TryGetValue(key, out var text) ? text : Definition?.Param(key)?.TextDefault ?? "";

    public bool HasKeys(string key) => Keys.TryGetValue(key, out var keys) && keys.Count > 0;

    public IReadOnlyList<Keyframe> GetKeys(string key) => Keys.TryGetValue(key, out var keys) ? keys : [];

    public void Set(string key, double value)
    {
        if (!double.IsFinite(value) || Values.TryGetValue(key, out var old) && old == value)
            return;

        Values[key] = value;
        ValueChanged?.Invoke(this, key);
    }

    public void SetText(string key, string value)
    {
        if (Texts.TryGetValue(key, out var old) && old == value)
            return;

        Texts[key] = value;
        ValueChanged?.Invoke(this, key);
    }

    /// <summary>What a change of the range is announced under (ValueChanged).</summary>
    public const string RangeKey = "$range";

    /// <summary>Sets the stretch of the clip the effect works in (its own seconds); null for all of it. Announced.</summary>
    public void SetRange((double Start, double End)? range)
    {
        if (range is { } window && (!double.IsFinite(window.Start) || !double.IsFinite(window.End) || window.End - window.Start < 0.01))
            return;
        if (Range == range)
            return;

        Range = range;
        ValueChanged?.Invoke(this, RangeKey);
    }

    /// <summary>Replaces a value's keyframes (sorted by time); null or none leaves it still.</summary>
    public void SetKeys(string key, IEnumerable<Keyframe>? keys)
    {
        var list = keys?.OrderBy(k => k.Time).ToList();
        if (list is { Count: > 0 })
            Keys[key] = list;
        else if (!Keys.Remove(key))
            return;

        ValueChanged?.Invoke(this, key);
    }

    partial void OnEnabledChanged(bool value) => ValueChanged?.Invoke(this, EnabledKey);

    public EffectState ToState() => Unknown ?? new EffectState
    {
        Type = Type,
        Id = Id,
        On = Enabled,
        Values = Values.Count + Texts.Count == 0 ? null
            : Values.Select(v => (v.Key, Value: JsonSerializer.SerializeToElement(v.Value)))
                .Concat(Texts.Select(t => (t.Key, Value: JsonSerializer.SerializeToElement(t.Value))))
                .ToDictionary(p => p.Key, p => p.Value),
        Keys = Keys.Count == 0 ? null : Keys.ToDictionary(k => k.Key, k => k.Value.ToList()),
        Range = Range is { } range ? [range.Start, range.End] : null,
    };

    public static EffectInstance FromState(EffectState state)
    {
        if (EffectRegistry.Find(state.Type) is null)
            return new EffectInstance { Type = state.Type, Id = state.Id == Guid.Empty ? Guid.NewGuid() : state.Id, Enabled = false, Unknown = state };

        var effect = new EffectInstance { Type = state.Type, Id = state.Id == Guid.Empty ? Guid.NewGuid() : state.Id, Enabled = state.On };
        foreach (var (key, value) in state.Values ?? [])
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number):
                    effect.Values[key] = number;
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    effect.Values[key] = value.GetBoolean() ? 1 : 0;
                    break;
                case JsonValueKind.String:
                    effect.Texts[key] = value.GetString() ?? "";
                    break;
            }
        }

        foreach (var (key, keys) in state.Keys ?? [])
        {
            if (keys is { Count: > 0 })
                effect.Keys[key] = keys.Where(k => double.IsFinite(k.Time) && double.IsFinite(k.Value)).OrderBy(k => k.Time).ToList();
        }

        if (state.Range is [var start, var end] && double.IsFinite(start) && double.IsFinite(end) && end > start)
            effect.Range = (start, end);
        return effect;
    }

    /// <summary>A copy with a new id: for Paste, presets and the clips of a track.</summary>
    /// <remarks>A missing effect is copied as it was read, id and all: it is not this HandPeg's to change.</remarks>
    public EffectInstance Clone()
    {
        if (Unknown is not null)
            return new EffectInstance { Type = Type, Id = Id, Enabled = false, Unknown = Unknown };

        var copy = new EffectInstance { Type = Type, Enabled = Enabled, Range = Range };
        foreach (var (key, value) in Values)
            copy.Values[key] = value;
        foreach (var (key, value) in Texts)
            copy.Texts[key] = value;
        foreach (var (key, value) in Keys)
            copy.Keys[key] = [.. value];
        return copy;
    }
}

/// <summary>
/// A card as projects, presets and Undo keep it: <c>{ "type", "id", "on", "values", "keys", "range" }</c>. Anything
/// else a later version writes is kept in <see cref="Extra"/> and written back.
/// </summary>
public sealed class EffectState
{
    public string Type { get; set; } = "";
    public Guid Id { get; set; }
    public bool On { get; set; } = true;
    public Dictionary<string, JsonElement>? Values { get; set; }
    public Dictionary<string, List<Keyframe>>? Keys { get; set; }
    public double[]? Range { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Listens to every card of a stack, as cards come and go: one callback for whatever changed in it. Used by a layer
/// for its own stack, and by the sequence for the Sequence Output's.
/// </summary>
public sealed class EffectStackWatcher
{
    private readonly ObservableCollection<EffectInstance> _stack;
    private readonly Action<EffectInstance?, string> _changed;
    private readonly List<EffectInstance> _hooked = [];

    /// <param name="changed">The card and the key that changed; a null card and "" when the stack itself changed.</param>
    public EffectStackWatcher(ObservableCollection<EffectInstance> stack, Action<EffectInstance?, string> changed)
    {
        (_stack, _changed) = (stack, changed);
        stack.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var old in _hooked.Except(_stack).ToList())
        {
            old.ValueChanged -= OnValueChanged;
            _hooked.Remove(old);
        }

        foreach (var added in _stack.Except(_hooked).ToList())
        {
            added.ValueChanged += OnValueChanged;
            _hooked.Add(added);
        }

        _changed(null, "");
    }

    private void OnValueChanged(EffectInstance effect, string key) => _changed(effect, key);
}
