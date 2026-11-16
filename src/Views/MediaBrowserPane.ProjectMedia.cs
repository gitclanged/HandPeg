using System.Windows.Controls;
using System.Windows.Input;
using HandPegApp.Models;

namespace HandPegApp.Views;

// ----- Project Media (the project bin) -----
// Lists the project's bin (TimelineDocument.MediaPool via ProjectFiles); manually added files persist until removed. The pane only
// requests changes: the window adds files (pane drop, Add to Project Media from Folder Browser or Clip Library) and removes them
// (Remove from Project Media, Delete in the list), each one Undo step. A file used by Sequence clips is removed only together with
// those clips, after confirmation.
public partial class MediaBrowserPane : IOwnsKeys
{
    /// <summary>Request to add files to Project Media (from Folder Browser or Clip Library).</summary>
    public event Action<IReadOnlyList<string>>? ProjectMediaAddRequested;

    /// <summary>Request to remove files from Project Media (context menu or Delete in its list).</summary>
    public event Action<IReadOnlyList<string>>? ProjectMediaRemoveRequested;

    /// <summary>True when a Sequence clip uses the file (removal then also removes those clips, after confirmation).</summary>
    public Func<string, bool>? IsFileInUse { get; set; }

    /// <summary>Switches to Project Media (e.g. after a drop added files); re-lists immediately if already shown.</summary>
    public void ShowProjectMedia()
    {
        if (PickerMode)
            return;
        if (ProjectSide.IsChecked == true)
        {
            if (_started)
                _ = ListProjectAsync(always: false);
            return;
        }

        ProjectSide.IsChecked = true;
    }

    /// <summary>
    /// Claims Delete while the Project Media list has focus (MainWindow.KeyBelongsToFocus), removing the selected files from the bin.
    /// Elsewhere in the pane and in other lists, Delete stays with the Timeline.
    /// </summary>
    public bool OwnsKey(Key key, ModifierKeys modifiers) =>
        key == Key.Delete && modifiers == ModifierKeys.None && _projectMode && !PickerMode && Items.IsKeyboardFocusWithin;

    /// <summary>Requests removal of the selected Project Media files.</summary>
    private void RemoveSelectedFromProject()
    {
        var files = Selected().Where(i => i.IsFile).Select(i => i.Path).ToList();
        if (files.Count > 0)
            ProjectMediaRemoveRequested?.Invoke(files);
    }

    /// <summary>Adds the Project Media menu item: Remove in Project Media (used files too, with their clips); Add elsewhere.</summary>
    private void AddProjectMediaItems(ContextMenu menu, IReadOnlyList<MediaBrowserItem> files)
    {
        if (PickerMode || files.Count == 0)
            return;

        var paths = files.Select(f => f.Path).ToList();
        MenuItem entry;
        if (_projectMode)
        {
            // Enabled for used files too: their clips are removed with them after confirmation (MainViewModel.RemoveFromProjectMedia).
            var used = paths.Count(p => IsFileInUse?.Invoke(p) == true);
            entry = new MenuItem
            {
                Header = paths.Count > 1 ? $"Remove {paths.Count} Files from Project Media" : "Remove from Project Media",
                InputGestureText = "Delete",
                ToolTip = used == 0
                    ? "Takes the file out of the project's bin (the file on disk stays). Undo puts it back."
                    : used == paths.Count && paths.Count == 1
                        ? "A clip on the Sequence plays it: HandPeg asks first, then takes it out with its clips, in one step Undo puts back."
                        : $"Clips on the Sequence play {used} of them: HandPeg asks first, then takes them out with their clips, in one step Undo puts back.",
            };
            entry.Click += (_, _) => ProjectMediaRemoveRequested?.Invoke(paths);
        }
        else
        {
            // Allowed with nothing open: a blank window keeps the bin for the project its first file creates.
            entry = new MenuItem
            {
                Header = paths.Count > 1 ? $"Add {paths.Count} Files to Project Media" : "Add to Project Media",
                ToolTip = "Keeps the file in the project's bin (Project Media), without putting it on the timeline.",
            };
            entry.Click += (_, _) => ProjectMediaAddRequested?.Invoke(paths);
        }

        menu.Items.Add(entry);
    }
}
