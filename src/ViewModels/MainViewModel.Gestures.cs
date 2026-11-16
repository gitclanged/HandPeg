namespace HandPegApp.ViewModels;

/// <summary>
/// Centralized look undo grouping: one run rule (<see cref="UndoRun"/>, <see cref="MergeWindow"/>) shared by view looks, typed clip
/// fields, crop typing and pane writes; one step per editing gesture (<see cref="StepForLook"/> with a
/// <see cref="Controls.GestureWriter{TKey}"/> identity); and <see cref="OwnLookScope"/> for writers managing their own steps. No WPF
/// input reads here: the window injects <see cref="IsPointerHeld"/> and <see cref="IsTypingInTextBox"/> (golden-drivable).
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Coalescing window: same-field, same-target changes within this interval and with no intervening step merge into one undo step
    /// (held arrow keys, box drags, wheel, successive typed values).
    /// </summary>
    internal const long MergeWindow = 1500;

    /// <summary>Coalescing window for per-keystroke text (names, words): one step per whole entry, as for marker renames.</summary>
    private const long TypingWindow = 30_000;

    /// <summary>Grace after a manual Checkpoint: changes within it join that step (e.g. adding a card, then its first value).</summary>
    private const long HandStepGrace = 500;

    private readonly GestureState _gesture = new();

    /// <summary>Left button down in a HandPeg window (window-injected; false headless); view look changes take one step per press.</summary>
    public Func<bool> IsPointerHeld { get; set; } = static () => false;

    /// <summary>Whether keyboard focus is in a text box (window-injected; false headless): names typed there are explicit input.</summary>
    public Func<bool> IsTypingInTextBox { get; set; } = static () => false;

    /// <summary>Whether Alt is held (window-injected; false headless): Alt-click on Solo makes it exclusive.</summary>
    public Func<bool> IsAltHeld { get; set; } = static () => false;

    /// <summary>
    /// Scope for writers (panes or the VM) that take their own undo steps (<see cref="StepForLook"/>, Checkpoint), suppressing view look
    /// auto-steps (<see cref="LookStepsShut"/>).
    /// </summary>
    internal OwnedLooks OwnLookScope()
    {
        _gesture.OwnedLooks++;
        return new OwnedLooks(this);
    }

    internal readonly struct OwnedLooks(MainViewModel owner) : IDisposable
    {
        public void Dispose() => owner._gesture.OwnedLooks--;
    }

    /// <summary>Editing gesture identity; one undo step per gesture (<see cref="StepForLook"/>).</summary>
    internal sealed class EditGesture(long stepsAtStart)
    {
        /// <summary>Total undo steps pushed at gesture start.</summary>
        public long StepsAtStart { get; } = stepsAtStart;
    }

    /// <summary>Creates a gesture identity (called by <see cref="Controls.GestureWriter{TKey}"/> when its outermost gesture begins).</summary>
    internal EditGesture NewGesture() => new(_gesture.Steps);

    /// <summary>
    /// Takes the undo step before a pane's look write. In a gesture (<paramref name="gesture"/> non-null) the first write takes the step
    /// or adopts one the gesture already pushed (e.g. card added first). A lone write takes a step unless it continues a run (same
    /// <paramref name="field"/>/<paramref name="target"/> within <see cref="MergeWindow"/>, no intervening step) or falls within
    /// <see cref="HandStepGrace"/>. <paramref name="startRun"/> (toggle, key added) always takes its own step outside a gesture.
    /// </summary>
    internal void StepForLook(object target, string field, string label, EditGesture? gesture = null, bool startRun = false)
    {
        if (_isBackgroundWorker || IsRestoringTimeline)
            return;

        if (gesture is not null)
        {
            if (ReferenceEquals(gesture, _gesture.Stepped))
                return;
            _gesture.Stepped = gesture;
            if (_gesture.Steps == gesture.StepsAtStart)
                Checkpoint(label);
            _gesture.Looks.Reset();
            return;
        }

        var now = Environment.TickCount64;

        var again = _gesture.Looks.Continues(target, field, now, MergeWindow, NewestStep);
        if (!startRun && (again || now - _checkpointedAt <= HandStepGrace))
            return;

        Checkpoint(label);
        _gesture.Looks.Restamp(NewestStep);
    }

    /// <summary>Expires all runs and the hand-step grace (for tests whose edits fall within the windows).</summary>
    internal void ForgetUndoRuns()
    {
        _gesture.Looks.Reset();
        _gesture.Fields.Reset();
        _gesture.Crop.Reset();
        _checkpointedAt = 0;
    }

    /// <summary>Monotonic count of undo steps pushed (the undo list itself is capped).</summary>
    internal long UndoStepsTaken => _gesture.Steps;

    /// <summary>Newest undo step's packed state (identity for the run rule); null when empty.</summary>
    private byte[]? NewestStep => _undo.Count > 0 ? _undo[^1].State : null;

    /// <summary>
    /// Undo coalescing rule: a change continues the run if it hits the same field and target as the last change, within the window,
    /// and (when tracked) with no intervening step.
    /// </summary>
    internal struct UndoRun
    {
        private object? _target;
        private string? _field;
        private long _at;
        private object? _newest;

        /// <summary>Whether this change continues the run; always records it as the last change.</summary>
        public bool Continues(object target, string field, long now, long window, object? newest)
        {
            var again = ReferenceEquals(_target, target) && _field == field && now - _at < window && ReferenceEquals(_newest, newest);
            (_target, _field, _at, _newest) = (target, field, now, newest);
            return again;
        }

        /// <summary>Records that a step was just taken for the last change; later changes compare against it.</summary>
        public void Restamp(object? newest) => _newest = newest;

        public void Reset() => this = default;
    }

    /// <summary>Undo grouping state, grouped in one object instead of loose VM fields.</summary>
    private sealed class GestureState
    {
        /// <summary>Nesting depth of open <see cref="OwnLookScope"/>s.</summary>
        public int OwnedLooks;

        /// <summary>Gesture that took the last step (one step per gesture identity).</summary>
        public EditGesture? Stepped;

        /// <summary>Monotonic pushed-step count (lets a gesture adopt a step taken after it began).</summary>
        public long Steps;

        /// <summary>Press during which the last manual step was taken (at most one step per held press).</summary>
        public int StepPress = -1;

        /// <summary>Runs for looks (views, sequence, output audio tracks, pane writes), clip fields and crop typing.</summary>
        public UndoRun Looks, Fields, Crop;
    }
}
