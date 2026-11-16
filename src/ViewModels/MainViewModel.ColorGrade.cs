using System.Collections.ObjectModel;
using HandPegApp.Models;
using HandPegApp.Models.Effects;

namespace HandPegApp.ViewModels;

// ----- Color Grade card (EffectRegistry.ColorStudio): shared access for Color Studio, Effects pane and Color Match -----
// Stateless (no fields; the tab-perf field audit lists every MainViewModel field). Writes go through Layer.Echo like Effects pane
// edits, each preceded by one undo Checkpoint.
public partial class MainViewModel
{
    /// <summary>Effect stack holding the target's Color Grade card (clip, blur fill or Sequence Output); null when none.</summary>
    public ObservableCollection<EffectInstance>? ColorGradeStack(ColorGradeTarget target) =>
        target.Layer is { } layer ? layer.Effects : target.Sequence ? SequenceEffects : null;

    /// <summary>Whether the target accepts a Color Grade card (Look-capable clip, blur fill, Sequence Output).</summary>
    public bool CanColorGrade(ColorGradeTarget target) =>
        target.Sequence || target.Layer is { } layer && EffectRegistry.Find(EffectRegistry.ColorStudio) is { } definition && layer.CanTake(definition, ignorePresent: true);

    /// <summary>
    /// Target's Color Grade card, or null. Allocation-free (no boxed enumerator) since Color Match queries it on every pointer move.
    /// </summary>
    public EffectInstance? FindColorGrade(ColorGradeTarget target)
    {
        if (ColorGradeStack(target) is not { } stack)
            return null;

        for (var i = 0; i < stack.Count; i++)
        {
            if (stack[i].Type == EffectRegistry.ColorStudio)
                return stack[i];
        }

        return null;
    }

    /// <summary>
    /// Gets or adds the target's Color Grade card (add = one undo step named <paramref name="label"/>, appended to the Look zone with
    /// defaults); null when the target cannot hold one.
    /// </summary>
    public EffectInstance? EnsureColorGrade(ColorGradeTarget target, string label)
    {
        if (FindColorGrade(target) is { } present)
            return present;
        if (!CanColorGrade(target) || ColorGradeStack(target) is not { } stack || EffectRegistry.Find(EffectRegistry.ColorStudio) is not { } definition)
            return null;

        Checkpoint(label);
        using (OwnLookScope())
        {
            var card = definition.NewEffect();
            stack.Insert(Layer.InsertionIndex(stack, card), card);
            return card;
        }
    }

    /// <summary>
    /// Writes a full value set into the Color Grade card (Color Match, Reset) in one undo step named <paramref name="label"/>, adding the
    /// card if needed. Keyframed parameters get static values (keys still override). False when the target cannot hold a card.
    /// </summary>
    public bool ApplyColorGrade(ColorGradeTarget target, in ColorGradeValues values, string label)
    {
        if (!CanColorGrade(target) || ColorGradeStack(target) is not { } stack || EffectRegistry.Find(EffectRegistry.ColorStudio) is not { } definition)
            return false;

        Checkpoint(label);
        using (OwnLookScope())
        {
            var card = FindColorGrade(target);
            if (card is null)
            {
                card = definition.NewEffect();
                stack.Insert(Layer.InsertionIndex(stack, card), card);
            }

            values.WriteTo(card);
        }

        return true;
    }
}
