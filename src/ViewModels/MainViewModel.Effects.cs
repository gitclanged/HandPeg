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
    /// <summary>The Sequence Output's effects: applied to the finished picture, after the Filters tab's colour. Kept in the project (and in presets).</summary>
    public ObservableCollection<EffectInstance> SequenceEffects => _sequenceEffects ??= WatchSequenceEffects();

    private ObservableCollection<EffectInstance>? _sequenceEffects;
    private EffectStackWatcher? _sequenceEffectWatcher;

    /// <summary>The Sequence Output's effects set aside at once (Bypass All, with the pane on Sequence Output).</summary>
    [ObservableProperty] private bool _sequenceEffectsBypassed;

    partial void OnSequenceEffectsBypassedChanged(bool value) => GenerateCommand();

    private ObservableCollection<EffectInstance> WatchSequenceEffects()
    {
        var stack = new ObservableCollection<EffectInstance>();
        _sequenceEffectWatcher = new EffectStackWatcher(stack, (_, _) => GenerateCommand());
        return stack;
    }

    /// <summary>Replaces the Sequence Output's stack (a project, a preset, Undo).</summary>
    private void SetSequenceEffects(IEnumerable<EffectState>? states, bool bypassed)
    {
        using var whole = DeferCommand();
        SequenceEffects.Clear();
        foreach (var state in states ?? [])
            SequenceEffects.Add(EffectInstance.FromState(state));
        SequenceEffectsBypassed = bypassed;
    }

    private List<EffectState> CaptureSequenceEffects() => [.. SequenceEffects.Select(e => e.ToState())];

    // ----- The blur fill's own stack (5.0-Release) -----

    /// <summary>The blur fill's settings are what the Effects pane works on: its row or block was selected on the timeline.</summary>
    [ObservableProperty] private bool _backgroundSelected;

    /// <summary>Replaces the blur fill's effects (a project, a Preset, Undo): Look effects only, one notice.</summary>
    private void SetBackgroundEffects(IEnumerable<EffectState>? states, bool bypassed)
    {
        var cards = (states ?? []).Select(EffectInstance.FromState).Where(c => c.Definition is null or { Zone: EffectZone.Look, Pinned: false }).ToList();
        if (!bypassed && _backgroundRow.EffectsBypassed == bypassed && cards.Count == 0 && _backgroundRow.Effects.Count == 0)
            return;

        using var whole = DeferCommand();
        _backgroundRow.SetEffects(cards, bypassed);
    }

    /// <summary>The blur fill's effects as a project, a Preset and Undo keep them; null for none.</summary>
    private List<EffectState>? CaptureBackgroundEffects() =>
        _backgroundRow.Effects.Count == 0 ? null : [.. _backgroundRow.Effects.Select(e => e.ToState())];

    /// <summary>
    /// An effect put on a clip or on the blur fill with its visible starting values (EffectDefinition.NewEffect; the
    /// user's Q1, 5.0-Release): the Effects pane's Add Effect and a track's Add Effect menu both come here. The clip's own
    /// (Layer.Echo, as the pane's edits are), one step of Undo; an effect a clip has once at most (a key, a shadow, the edges,
    /// the blend) is switched on as Layer Properties would, its instance the one there. Returns the effect; null where it cannot go.
    /// </summary>
    public EffectInstance? AddEffect(Layer layer, EffectDefinition definition)
    {
        if (definition.Pinned)
            return null;

        Checkpoint($"add {definition.Name}");
        EffectInstance card;
        using (Layer.Echo())
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

        ShowEffectsPane = true;
        StatusText = $"{definition.Name} added to {layer.Name}. Ctrl+Z takes it off.";
        Log($"Effect added: {definition.Name} on {layer.Name}");
        return card;
    }

    /// <summary>The Effects pane: on to start with, beside the Keyframes pane.</summary>
    [ObservableProperty] private bool _showEffectsPane = AppSettings.Current.ShowEffectsPane;

    partial void OnShowEffectsPaneChanged(bool value) => SaveView(s => s.ShowEffectsPane = value);

    /// <summary>
    /// Adds an adjustment clip at the playhead: a clip with no picture whose effects work on everything under it
    /// while it lasts, over the whole frame (narrow its rectangle in the layout pane to limit it to a part).
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

        // Something to see at once: a grade, which the Effects pane then shows to be changed.
        element.AddEffect(EffectRegistry.Find(EffectRegistry.Grade)?.NewEffect() ?? new EffectInstance { Type = EffectRegistry.Grade });
        if (!PlaceLayer(element, start, start + length, "add an adjustment clip"))
            return;

        KeyLayer = element;
        ShowEffectsPane = true;
        Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Added {element.Name}: its effects (Effects pane) work on everything under it from {element.StartTime:0.##} s for {element.Duration:0.##} s"));
    }

    /// <summary>Gives every clip on a clip's track a copy of a stack (Paste Effects with Shift, or Paste Selected Effects...), with the values its effects carry.</summary>
    /// <param name="append">Added to each clip's stack (Paste Selected Effects..., 4.1.1-B), an effect a clip has once at most taking the
    /// place of the one there; otherwise each clip's stack is replaced (Paste Effects).</param>
    public void PasteEffectsToTrack(Layer on, IReadOnlyList<EffectState> cards, bool append = false)
    {
        Checkpoint($"paste effects to the track of {on.Name}");
        using var whole = DeferCommand();
        foreach (var clip in GetTrackClips(on))
        {
            using (Layer.Echo())
            {
                var pasted = cards.Select(clip.AdoptEffect).ToList();
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
