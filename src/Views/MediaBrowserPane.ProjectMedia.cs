using System.Windows.Controls;
using System.Windows.Input;
using HandPegApp.Models;

namespace HandPegApp.Views;

// ----- Project Media, the project's bin (5.4C) -----
// Project Media lists the project's own bin (TimelineDocument.MediaPool, through ProjectFiles): files put in by hand stay until taken out.
// The pane only asks: the window puts files in (a drop on the pane, Add to Project Media from the Folder Browser or the Clip Library) and
// takes them out (Remove from Project Media, Delete in the list), each a step of Undo; a file a clip on the Sequence plays goes only
// with its clips, once the user says so (5.4D, Q5 b).
public partial class MediaBrowserPane : IOwnsKeys
{
    /// <summary>Files were asked to be put into Project Media (from the Folder Browser or the Clip Library).</summary>
    public event Action<IReadOnlyList<string>>? ProjectMediaAddRequested;

    /// <summary>Files were asked to be taken out of Project Media (its menu, or Delete in its list).</summary>
    public event Action<IReadOnlyList<string>>? ProjectMediaRemoveRequested;

    /// <summary>Whether a clip on the Sequence plays a file (such a file cannot be taken out of Project Media).</summary>
    public Func<string, bool>? IsFileInUse { get; set; }

    /// <summary>Shows Project Media (after a drop onto the pane put files into it); listed again at once when it was shown already.</summary>
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
    /// Delete is Project Media's own while the keyboard is in its list (5.4C, MainWindow.KeyBelongsToFocus): it takes the files chosen out
    /// of the bin. Anywhere else in the pane, and in the other lists, Delete stays the timeline's.
    /// </summary>
    public bool OwnsKey(Key key, ModifierKeys modifiers) =>
        key == Key.Delete && modifiers == ModifierKeys.None && _projectMode && !PickerMode && Items.IsKeyboardFocusWithin;

    /// <summary>The files chosen in Project Media, asked to be taken out of it.</summary>
    private void RemoveSelectedFromProject()
    {
        var files = Selected().Where(i => i.IsFile).Select(i => i.Path).ToList();
        if (files.Count > 0)
            ProjectMediaRemoveRequested?.Invoke(files);
    }

    /// <summary>The menu's Project Media lines: in Project Media, Remove (5.4D: used files too, with their clips); elsewhere, Add.</summary>
    private void AddProjectMediaItems(ContextMenu menu, IReadOnlyList<MediaBrowserItem> files)
    {
        if (PickerMode || files.Count == 0)
            return;

        var paths = files.Select(f => f.Path).ToList();
        MenuItem entry;
        if (_projectMode)
        {
            // 5.4D (Q5 b): enabled for used files too: their clips go with them, after a question (MainViewModel.RemoveFromProjectMedia).
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
            // 5.4D (Q4 b): with nothing open too: a blank window keeps the bin for the project its first file starts.
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
