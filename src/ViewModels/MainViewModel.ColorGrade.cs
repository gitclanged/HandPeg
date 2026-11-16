using System.Collections.ObjectModel;
using HandPegApp.Models;
using HandPegApp.Models.Effects;

namespace HandPegApp.ViewModels;

// ----- 5.6.0: the Color Grade card (EffectRegistry.ColorStudio), as the Color Studio, the Effects pane's card and Color Match edit it -----
// Stateless (no fields: the tab-perf field audit lists every MainViewModel field). A clip's card is the clip's own (Layer.Echo, as the
// Effects pane's edits are); every write is one step of Undo (Checkpoint), taken before anything changes.
public partial class MainViewModel
{
    /// <summary>The stack a target's Color Grade card lives in: the clip's (or the blur fill's), the Sequence Output's; null for none.</summary>
    public ObservableCollection<EffectInstance>? ColorGradeStack(ColorGradeTarget target) =>
        target.Layer is { } layer ? layer.Effects : target.Sequence ? SequenceEffects : null;

    /// <summary>Whether the target can hold a Color Grade card (a clip that takes Look effects, the blur fill, the Sequence Output).</summary>
    public bool CanColorGrade(ColorGradeTarget target) =>
        target.Sequence || target.Layer is { } layer && EffectRegistry.Find(EffectRegistry.ColorStudio) is { } definition && layer.CanTake(definition, ignorePresent: true);

    /// <summary>
    /// The target's Color Grade card; null when it has none. Allocates nothing (5.6.0: Color Match asks on every pointer move while
    /// picking, so no boxed enumerator).
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
    /// The target's Color Grade card, put on it first when it has none (one step of Undo named <paramref name="label"/>, at the bottom of
    /// its Look zone, a new card's starting values); null when the target cannot take one.
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
    /// Writes a whole set of values into the target's Color Grade card (Color Match, Reset): one step of Undo named <paramref name="label"/>,
    /// the card put on first when there is none. Values a keyframed parameter holds are written as its static value (its keys still win).
    /// Returns false when the target cannot take a card.
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
