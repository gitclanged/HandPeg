using HandPegApp.Controls;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

// ----- Color Studio writes into the Color Grade card -----
// Uses the same GestureWriter as the Effects pane: a gesture (wheel, curves or slider drag) is one Undo step, written at most
// Pacing.GestureWritesPerSecond times per second (each write rebuilds the scene plan) with the final value flushed at the end; a lone
// change (typed value, row reset) records a step unless it continues a run. Values are stored like the Effects pane does
// (EffectParam.Quantize, KeyEdit.Upsert). The first write on a card-less target adds a card, and that step
// (MainViewModel.EnsureColorGrade) belongs to the gesture or lone change.
public partial class ColorStudioPane
{
    private GestureWriter<(bool Text, string Key)>? _writes;
    private bool _writing;
    private long _lastOwnWrite;

    private GestureWriter<(bool Text, string Key)> Writes => _writes ??= MakeWriter();

    /// <summary>True while a gesture (drag) is in progress.</summary>
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

    /// <summary>Flushes pending writes to the card now (adding the card first if needed).</summary>
    internal void FlushWrites() => _writes?.Flush();

    // Batch wrapper: suppresses self-echo during writes (Card_ValueChanged) and refreshes immediately for lone writes (no gesture end to do it).
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

    // Writes one queued value: resolve (or add) the card, record the undo step, then apply.
    private void WriteToCard(string key, Action<EffectInstance> write)
    {
        if (_vm is null || EnsureCard() is not { } card)
            return;
        _vm.StepForLook(card, key, "change Color Grade", Writes.Gesture);
        write(card);
    }

    /// <summary>
    /// Returns the target's card, adding one if missing (that Undo step belongs to the gesture or lone change). Returns null for
    /// non-gradable targets (e.g. audio clips) regardless of their stack; never adds a card to them.
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

    /// <summary>Writes a number like the Effects pane: quantized to step and range; upserts a key at the playhead when keyed.</summary>
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

    /// <summary>Ownership scope for writes outside paced batches, so the view model records no look step of its own.</summary>
    private MainViewModel.OwnedLooks? OwnLooks() => _vm?.OwnLookScope();

    /// <summary>Resets the whole grade to new-card defaults (including keys, curves and LUT) as one Undo step.</summary>
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
