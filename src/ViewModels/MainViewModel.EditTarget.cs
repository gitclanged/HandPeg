using HandPegApp.Models.Effects;

namespace HandPegApp.ViewModels;

/// <summary>
/// Shared edit target for the Effects pane and Color Studio: a clip (or blur fill row), the Sequence Output, or none. VM-owned so the
/// panes stay decoupled: the Effects pane applies selection rules (audio clip, blur fill row, key layer) and publishes via
/// <see cref="SetEditTarget"/>; Color Studio follows <see cref="EditTargetChanged"/>; either toggles <see cref="EditSequenceOutput"/>.
/// </summary>
public partial class MainViewModel
{
    private readonly EditTargetState _editTarget = new();

    /// <summary>Raised after <see cref="EditTarget"/> or <see cref="EditSequenceOutput"/> changes.</summary>
    public event Action? EditTargetChanged;

    /// <summary>Current edit target.</summary>
    public ColorGradeTarget EditTarget => _editTarget.Target;

    /// <summary>
    /// Panes target the Sequence Output instead of a clip. Toggled from either pane; switches the target immediately (to none when
    /// off), after which the Effects pane republishes its clip.
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

    /// <summary>Publishes the Effects pane's target on retarget (no event when unchanged).</summary>
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
