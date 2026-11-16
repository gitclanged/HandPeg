using HandPegApp.Models.Effects;

namespace HandPegApp.ViewModels;

/// <summary>
/// What the editing panes work on (the Effects pane and the Color Studio): a clip (or the blur fill's row), the Sequence Output, or
/// nothing. The view model owns it, so neither pane reaches into the other: the Effects pane applies its selection rules (a sound clip
/// selected, the blur fill row, the key layer) and publishes the result (<see cref="SetEditTarget"/>); the Color Studio follows
/// <see cref="EditTargetChanged"/>; either switches the Sequence Output through <see cref="EditSequenceOutput"/>.
/// </summary>
public partial class MainViewModel
{
    private readonly EditTargetState _editTarget = new();

    /// <summary>Raised after <see cref="EditTarget"/> or <see cref="EditSequenceOutput"/> changed.</summary>
    public event Action? EditTargetChanged;

    /// <summary>What the editing panes work on now.</summary>
    public ColorGradeTarget EditTarget => _editTarget.Target;

    /// <summary>
    /// Whether the panes edit the Sequence Output rather than a clip. Set from either pane's toggle; the target becomes the Sequence Output
    /// at once (or nothing, switched off), and the Effects pane then publishes the clip it works on.
    /// </summary>
    public bool EditSequenceOutput
    {
        get => _editTarget.Sequence;
        set
        {
            if (_editTarget.Sequence == value)
                return;
            _editTarget.Sequence = value;
            _editTarget.Target = value ? ColorGradeTarget.ForSequence : ColorGradeTarget.None;
            EditTargetChanged?.Invoke();
        }
    }

    /// <summary>The Effects pane's target, published as it retargets (nothing raised when it is the same).</summary>
    internal void SetEditTarget(ColorGradeTarget target)
    {
        if (_editTarget.Target == target)
            return;
        _editTarget.Target = target;
        EditTargetChanged?.Invoke();
    }

    private sealed class EditTargetState
    {
        public bool Sequence;
        public ColorGradeTarget Target;
    }
}
