namespace HandPegApp.ViewModels;

/// <summary>
/// Undo grouping for looks, in one place: the single run rule (<see cref="UndoRun"/>, <see cref="MergeWindow"/>) that view looks, clip
/// fields typed into boxes, crop typing and the editing panes' writes all use; one step per editing gesture (<see cref="StepForLook"/>
/// with a gesture identity from a <see cref="Controls.GestureWriter{TKey}"/>); and the scope in which a pane or the view model writes
/// looks that keep their own steps (<see cref="OwnLookScope"/>). The view model reads no WPF input state: the window injects
/// <see cref="IsPointerHeld"/> and <see cref="IsTypingInTextBox"/>, which the goldens can drive.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Changes of the same field of the same target closer than this, with no other step taken between, are one undo step: a slider's
    /// arrow keys held, a box dragged, the wheel turned, values typed one after another.
    /// </summary>
    internal const long MergeWindow = 1500;

    /// <summary>The same for a name or words typed letter by letter: one step for the whole of it, as a marker's rename has.</summary>
    private const long TypingWindow = 30_000;

    /// <summary>A change this soon after a step taken by hand (Checkpoint) belongs to that step (a card put on, then its first value).</summary>
    private const long HandStepGrace = 500;

    private readonly GestureState _gesture = new();

    /// <summary>
    /// Whether the left mouse button is down in one of HandPeg's windows (set by the window; false without one). A view look changed
    /// while it is held takes one step per press.
    /// </summary>
    public Func<bool> IsPointerHeld { get; set; } = static () => false;

    /// <summary>Whether the keyboard focus is in a text box (set by the window; false without one): a name typed there is the user's.</summary>
    public Func<bool> IsTypingInTextBox { get; set; } = static () => false;

    /// <summary>Whether Alt is held (set by the window; false without one): an Alt-click on a track's Solo plays it alone.</summary>
    public Func<bool> IsAltHeld { get; set; } = static () => false;

    /// <summary>
    /// Writes made inside this scope are the writer's own (an editing pane's, or the view model's): it takes their undo steps itself
    /// (<see cref="StepForLook"/>, Checkpoint), so the views' looks take none of their own (<see cref="LookStepsShut"/>).
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

    /// <summary>An editing gesture's identity: one undo step per gesture (see <see cref="StepForLook"/>).</summary>
    internal sealed class EditGesture(long stepsAtStart)
    {
        /// <summary>How many undo steps had been taken when the gesture began.</summary>
        public long StepsAtStart { get; } = stepsAtStart;
    }

    /// <summary>A new gesture's identity (a <see cref="Controls.GestureWriter{TKey}"/> makes one as its outermost gesture begins).</summary>
    internal EditGesture NewGesture() => new(_gesture.Steps);

    /// <summary>
    /// The undo step for a look write about to be made by an editing pane. Inside a gesture (<paramref name="gesture"/> not null) its
    /// first write takes the step, or adopts one taken during the gesture before it (a card put on first). A lone write takes a step
    /// unless it continues a run (the same <paramref name="field"/> of the same <paramref name="target"/> within <see cref="MergeWindow"/>,
    /// no other step between) or follows a step taken by hand within <see cref="HandStepGrace"/>. <paramref name="startRun"/> (a switch,
    /// a key added) always takes a step of its own outside a gesture.
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

    /// <summary>Ends every run and the hand-step grace, as if the last step were long ago (tests whose edits come within the windows).</summary>
    internal void ForgetUndoRuns()
    {
        _gesture.Looks.Reset();
        _gesture.Fields.Reset();
        _gesture.Crop.Reset();
        _checkpointedAt = 0;
    }

    /// <summary>Every undo step pushed so far (monotonic; the undo list itself is capped).</summary>
    internal long UndoStepsTaken => _gesture.Steps;

    /// <summary>The newest undo step's packed state (which step is newest, for the run rule), or null with none.</summary>
    private byte[]? NewestStep => _undo.Count > 0 ? _undo[^1].State : null;

    /// <summary>
    /// The one undo-coalescing rule: a change continues the run when it is the same field of the same target as the run's last change,
    /// within the window, and (when steps are tracked) no other step was taken since.
    /// </summary>
    internal struct UndoRun
    {
        private object? _target;
        private string? _field;
        private long _at;
        private object? _newest;

        /// <summary>Whether this change continues the run; it becomes the run's last change either way.</summary>
        public bool Continues(object target, string field, long now, long window, object? newest)
        {
            var again = ReferenceEquals(_target, target) && _field == field && now - _at < window && ReferenceEquals(_newest, newest);
            (_target, _field, _at, _newest) = (target, field, now, newest);
            return again;
        }

        /// <summary>A step was just taken for the run's last change: later changes compare with it.</summary>
        public void Restamp(object? newest) => _newest = newest;

        public void Reset() => this = default;
    }

    /// <summary>The undo-grouping state, one object rather than loose view-model fields.</summary>
    private sealed class GestureState
    {
        /// <summary>How deep <see cref="OwnLookScope"/> scopes are open.</summary>
        public int OwnedLooks;

        /// <summary>The gesture whose step was taken last (one step per gesture identity).</summary>
        public EditGesture? Stepped;

        /// <summary>Every undo step pushed so far (a gesture adopts a step taken after it began).</summary>
        public long Steps;

        /// <summary>The press during which the last step was taken by hand: one press of the button held down is one step at most.</summary>
        public int StepPress = -1;

        /// <summary>The runs: looks (views, the sequence, the output audio tracks, the panes' writes), clip fields, crop typing.</summary>
        public UndoRun Looks, Fields, Crop;
    }
}
