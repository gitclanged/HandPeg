using HandPegApp.Controls;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

// ----- The Color Studio's writes into its Color Grade card -----
// Through the same GestureWriter as the Effects pane: a gesture (a wheel's, the curves', a slider's drag) is one undo step, its values
// written at most Pacing.GestureWritesPerSecond times a second (the scene plan is made again on every write) and the last as it ends; a
// lone change (a typed value, a reset row) takes a step unless it continues a run. Values are stored as the Effects pane stores them
// (EffectParam.Quantize, KeyEdit.Upsert). The first write on a target without a card puts one on: that step
// (MainViewModel.EnsureColorGrade's) is the gesture's, or the lone change's.
public partial class ColorStudioPane
{
    private GestureWriter<(bool Text, string Key)>? _writes;
    private bool _writing;
    private long _lastOwnWrite;

    private GestureWriter<(bool Text, string Key)> Writes => _writes ??= MakeWriter();

    /// <summary>Whether a gesture (a drag) is under way.</summary>
    private bool InGesture => _writes?.InGesture == true;

    private GestureWriter<(bool Text, string Key)> MakeWriter()
    {
        var writer = new GestureWriter<(bool Text, string Key)>(() => _vm, around: AroundWrites);
        writer.Ended += () =>
        {
            QueueRefresh();
            if (_wanted)
                Want();
        };
        return writer;
    }

    private void BeginGesture() => Writes.Begin();

    private void EndGesture() => _writes?.End();

    private void QueueNumber(string key, double value)
    {
        if (double.IsFinite(value))
            Writes.Queue((false, key), () => WriteToCard(key, card => WriteNumber(card, key, value)));
    }

    private void QueueText(string key, string text)
    {
        text ??= "";
        Writes.Queue((true, key), () => WriteToCard(key, card =>
        {
            if (card.GetText(key) != text)
                card.SetText(key, text);
        }));
    }

    /// <summary>What is waiting written into the card now (the card put on first when there is none).</summary>
    internal void FlushWrites() => _writes?.Flush();

    // Each batch: its echo skipped while writing (Card_ValueChanged), and a lone write shown at once (no gesture's end refreshes it).
    private void AroundWrites(Action batch)
    {
        _writing = true;
        try
        {
            batch();
        }
        finally
        {
            _writing = false;
            _lastOwnWrite = Environment.TickCount64;
            if (!InGesture)
                QueueRefresh();
        }
    }

    // One queued value: the card found (or put on), the write's undo step, the value.
    private void WriteToCard(string key, Action<EffectInstance> write)
    {
        if (_vm is null || EnsureCard() is not { } card)
            return;
        _vm.StepForLook(card, key, "change Color Grade", Writes.Gesture);
        write(card);
    }

    /// <summary>
    /// The target's card, put on it first when it has none (that step of Undo counts as the gesture's, or the lone change's). None for a
    /// target that cannot be graded (a sound clip), whatever its stack holds: never a card put on it.
    /// </summary>
    private EffectInstance? EnsureCard()
    {
        if (!_vm!.CanColorGrade(_target))
            return null;
        if (_card is { } card && _stack?.Contains(card) == true)
            return card;

        var found = _vm!.FindColorGrade(_target) ?? _vm.EnsureColorGrade(_target, "add Color Grade");
        if (found is null)
            return null;

        Adopt(found);
        return found;
    }

    /// <summary>A number written as the Effects pane writes it: quantized to its step and range; a key at the playhead when the value is keyed.</summary>
    private void WriteNumber(EffectInstance card, string key, double value)
    {
        if (card.Definition?.Param(key) is not { } param)
            return;

        value = param.Quantize(value);
        if (card.HasKeys(key))
            card.SetKeys(key, KeyEdit.Upsert(card.GetKeys(key), ClipTime, value));
        else if (card.Get(key) != value)
            card.Set(key, value);
    }

    /// <summary>What the pane writes outside its paced batches is its own look too: the view model takes no step for it.</summary>
    private MainViewModel.OwnedLooks? OwnLooks() => _vm?.OwnLookScope();

    /// <summary>The whole grade back to a new card's (its keys too, its curves and its LUT), one step of Undo.</summary>
    private void ResetGrade()
    {
        FlushWrites();
        if (_vm is null || _card is not { Definition: { } definition } card || !_vm.CanColorGrade(_target))
            return;

        _vm.Checkpoint("reset Color Grade");
        _writing = true;
        try
        {
            using (_vm.BatchLookWrites())
            using (OwnLooks())
            {
                foreach (var param in definition.Params)
                {
                    if (card.HasKeys(param.Key))
                        card.SetKeys(param.Key, null);
                    if (param.Kind == ParamKind.Text || param.Kind == ParamKind.File)
                        card.SetText(param.Key, "");
                }

                ColorGradeValues.Neutral.WriteTo(card);
            }
        }
        finally
        {
            _writing = false;
        }

        RefreshControls();
    }
}
