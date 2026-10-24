using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Effect stacks: the Sequence Output's, adjustment layers, and the Effects pane's target -----
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

    /// <summary>The Effects pane: on in Editor Mode, beside the Keyframes pane. Per mode.</summary>
    [ObservableProperty] private bool _showEffectsPane = AppSettings.Current.ShowEffectsPane;

    partial void OnShowEffectsPaneChanged(bool value) => SaveView(s => s.ShowEffectsPane = value);

    /// <summary>
    /// Adds an adjustment layer at the playhead: a clip with no picture whose effects work on everything under it
    /// while it lasts, over the whole frame (narrow its rectangle in the layout pane to limit it to a part).
    /// </summary>
    [RelayCommand]
    private void AddAdjustmentLayer()
    {
        var sequence = SequenceSeconds;
        var start = Math.Clamp(PositionMs / 1000, 0, Math.Max(sequence - 1, 0));
        var element = new Layer
        {
            Kind = LayerKind.Adjustment,
            Name = $"Adjustment {Layers.Count(l => l.IsAdjustment) + 1}",
            StartTime = start,
            Duration = sequence > 0 ? Math.Min(5, Math.Max(sequence - start, 1)) : 5,
            PositionX = 0,
            PositionY = 0,
            SizeWidth = 1,
            SizeHeight = 1,
            LockAspectRatio = false,
        };

        // Something to see at once: a grade, which the Effects pane then shows to be changed.
        element.AddEffect(new EffectInstance { Type = EffectRegistry.Grade });
        Checkpoint("add an adjustment layer");
        AttachLayer(element);
        KeyLayer = element;
        ShowEffectsPane = true;
        Log($"Added {element.Name}: its effects (Effects pane) work on everything under it from {start:0.##} s for {element.Duration:0.##} s");
    }

    /// <summary>Gives every clip on a clip's track a copy of a stack (Paste to Track), with the values its cards carry.</summary>
    public void PasteEffectsToTrack(Layer on, IReadOnlyList<EffectState> cards)
    {
        Checkpoint($"paste effects to the track of {on.Name}");
        using var whole = DeferCommand();
        foreach (var clip in GetTrackClips(on))
        {
            using (Layer.Echo())
                clip.SetEffects([.. cards.Select(clip.AdoptCard)]);
        }
    }
}
