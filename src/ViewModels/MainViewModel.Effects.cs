using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Effect stacks: the Sequence Output's, adjustment clips, and the Effects pane's target -----
public partial class MainViewModel
{
    /// <summary>Sequence Output effects, applied to the final picture after Filters colour. Persisted in projects and presets.</summary>
    public ObservableCollection<EffectInstance> SequenceEffects => _sequenceEffects ??= WatchSequenceEffects();

    private ObservableCollection<EffectInstance>? _sequenceEffects;
    private EffectStackWatcher? _sequenceEffectWatcher;

    /// <summary>Bypass All for Sequence Output effects (pane on Sequence Output).</summary>
    [ObservableProperty] private bool _sequenceEffectsBypassed;

    partial void OnSequenceEffectsBypassedChanged(bool value) => GenerateCommand();

    private ObservableCollection<EffectInstance> WatchSequenceEffects()
    {
        var stack = new ObservableCollection<EffectInstance>();
        _sequenceEffectWatcher = new EffectStackWatcher(stack, (_, _) => GenerateCommand());
        return stack;
    }

    /// <summary>Replaces the Sequence Output stack (project load, preset, Undo).</summary>
    private void SetSequenceEffects(IEnumerable<EffectState>? states, bool bypassed)
    {
        using var whole = DeferCommand();
        SequenceEffects.Clear();
        foreach (var state in states ?? [])
            SequenceEffects.Add(EffectInstance.FromState(state));
        SequenceEffectsBypassed = bypassed;
    }

    private List<EffectState> CaptureSequenceEffects() => [.. SequenceEffects.Select(e => e.ToState())];

    // ----- Blur fill effect stack -----

    /// <summary>Effects pane targets the blur fill (its row or block selected on the timeline).</summary>
    [ObservableProperty] private bool _backgroundSelected;

    /// <summary>Replaces blur fill effects (project, Preset, Undo); Look effects only, single change notification.</summary>
    private void SetBackgroundEffects(IEnumerable<EffectState>? states, bool bypassed)
    {
        var cards = (states ?? []).Select(EffectInstance.FromState).Where(c => c.Definition is null or { Zone: EffectZone.Look, Pinned: false }).ToList();
        if (!bypassed && _backgroundRow.EffectsBypassed == bypassed && cards.Count == 0 && _backgroundRow.Effects.Count == 0)
            return;

        using var whole = DeferCommand();
        _backgroundRow.SetEffects(cards, bypassed);
    }

    /// <summary>Blur fill effects for persistence (project, Preset, Undo); null when empty.</summary>
    private List<EffectState>? CaptureBackgroundEffects() =>
        _backgroundRow.Effects.Count == 0 ? null : [.. _backgroundRow.Effects.Select(e => e.ToState())];

    /// <summary>
    /// Adds an effect with visible defaults (EffectDefinition.NewEffect) to a clip or the blur fill; shared by the Effects pane and the
    /// track Add Effect menu. Writes via Layer.Echo in one undo step; single-instance effects (key, shadow, edges, blend) are enabled as
    /// Layer Properties does, reusing the existing instance. Returns the effect, or null when not applicable.
    /// </summary>
    public EffectInstance? AddEffect(Layer layer, EffectDefinition definition)
    {
        if (definition.Pinned)
            return null;

        Checkpoint($"add {definition.Name}");
        var card = PutEffectOn(layer, definition);
        ShowEffectsPane = true;
        StatusText = $"{definition.Name} added to {layer.Name}. Ctrl+Z takes it off.";
        Log($"Effect added: {definition.Name} on {layer.Name}");
        return card;
    }

    /// <summary>As <see cref="AddEffect"/> but within a caller's undo step (Creator Hotbar, multi-clip).</summary>
    private EffectInstance PutEffectOn(Layer layer, EffectDefinition definition)
    {
        EffectInstance card;
        using (OwnLookScope())
        {
            switch (definition.Feature)
            {
                case nameof(Layer.ChromaKey): layer.ChromaKey = true; break;
                case nameof(Layer.Shadow): layer.Shadow = true; break;
                case "Edges" when layer.CornerRadius == 0 && !layer.Feather: layer.CornerRadius = 8; break;
            }

            card = layer.Find(definition.Type) is { } present && (definition.Feature is not null || definition.Type == EffectRegistry.BlendMode)
                ? present
                : layer.AddEffect(definition.NewEffect());
        }

        return card;
    }

    /// <summary>Effects pane visibility (default on, beside Keyframes).</summary>
    [ObservableProperty] private bool _showEffectsPane = AppSettings.Current.ShowEffectsPane;

    partial void OnShowEffectsPaneChanged(bool value) => SaveView(s => s.ShowEffectsPane = value);

    /// <summary>
    /// Adds an adjustment clip at the playhead: no picture of its own, its effects apply to everything beneath over the full frame
    /// (shrink its rectangle in the layout pane to limit the area).
    /// </summary>
    [RelayCommand]
    private void AddAdjustmentLayer()
    {
        var sequence = SequenceSeconds;
        var start = Math.Clamp(PositionMs / 1000, 0, Math.Max(sequence - 1, 0));
        var length = sequence > 0 ? Math.Min(5, Math.Max(sequence - start, 1)) : 5;
        var element = new Layer
        {
            Kind = LayerKind.Adjustment,
            Name = $"Adjustment {Layers.Count(l => l.IsAdjustment) + 1}",
            PositionX = 0,
            PositionY = 0,
            SizeWidth = 1,
            SizeHeight = 1,
            LockAspectRatio = false,
        };

        // Start with a visible grade so the result is immediate and editable in the Effects pane.
        element.AddEffect(EffectRegistry.Find(EffectRegistry.Grade)?.NewEffect() ?? new EffectInstance { Type = EffectRegistry.Grade });
        if (!PlaceLayer(element, start, start + length, "add an adjustment clip"))
            return;

        KeyLayer = element;
        ShowEffectsPane = true;
        Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Added {element.Name}: its effects (Effects pane) work on everything under it from {element.StartTime:0.##} s for {element.Duration:0.##} s"));
    }

    /// <summary>
    /// Filters copied cards for a clip like the pane's paste (ReplaceWith, Fits): unknown effects pass (as AdoptEffect keeps them);
    /// kind-incompatible ones (picture card on an audio clip) are dropped before AdoptEffect enables features (chroma key, shadow).
    /// </summary>
    internal static IEnumerable<EffectState> TakenBy(Layer clip, IReadOnlyList<EffectState> cards) =>
        cards.Where(s => EffectRegistry.Find(s.Type) is not { } definition || clip.CanTake(definition, ignorePresent: true));

    /// <summary>Copies a stack with its values to every clip on a clip's track (Shift+Paste Effects, Paste Selected Effects...).</summary>
    /// <param name="append">Append to each stack (Paste Selected Effects...; single-instance effects replace the existing one);
    /// otherwise replace the stack (Paste Effects).</param>
    public void PasteEffectsToTrack(Layer on, IReadOnlyList<EffectState> cards, bool append = false)
    {
        Checkpoint($"paste effects to the track of {on.Name}");
        using var whole = DeferCommand();
        foreach (var clip in GetTrackClips(on))
        {
            using (OwnLookScope())
            {
                var pasted = TakenBy(clip, cards).Select(clip.AdoptEffect).ToList();
                if (!append)
                {
                    clip.SetEffects(pasted);
                    continue;
                }

                var once = pasted.Where(c => c.Definition is { Feature: not null }).Select(c => c.Type).ToHashSet();
                clip.SetEffects(clip.Effects.Where(c => !once.Contains(c.Type)).Concat(pasted).OrderBy(c => (int)(c.Definition?.Zone ?? EffectZone.Look)).ToList());
            }
        }
    }
}
