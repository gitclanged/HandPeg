using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- A New Project from the studio (5.4A) -----
// The Project Manager's New Project mode hands back a NewProjectRequest and opens nothing itself. The window installs a fresh, empty
// project first (a tab of its own: MainWindow.ProjectTabs.cs), then makes it here: the Project Settings and Game Preset taken on
// (MainViewModel.TakeNewProject), then the files chosen put into Project Media (5.4C) and laid on the Sequence as a drop of them on an
// empty project lays them.
public partial class MainWindow
{
    /// <summary>
    /// Makes a New Project on the fresh, empty project already in place: its settings, its Game Preset and its files (the first opened,
    /// with the Game Preset when one was chosen; the other pictures after it on its track; the sounds at the start). With no file, a Game
    /// Preset's layout waits for the first one opened. The name is kept for the first save.
    /// </summary>
    internal async Task CreateProjectAsync(NewProjectRequest request)
    {
        if (!_viewModel.TakeNewProject(request))
            return;

        var files = request.Files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

        // Every file picked goes into Project Media first (5.4C), with no step of Undo (a project made is not an edit): one that then fails
        // to open, or is left off the Sequence, is still in the project's bin.
        _viewModel.AddToProjectMedia(files, undoable: false);
        if (files.Count == 0)
        {
            _viewModel.StatusText = $"New project \"{request.Name}\": {MainViewModel.Describe(_viewModel.CurrentProjectSettings)}. Drop a video on the player, or use Add Media, to start.";
            return;
        }

        // The first picture opens the project (a sound only when there is no picture); the other pictures follow it in line, and the
        // sounds go on audio tracks at the start.
        var first = files.FirstOrDefault(f => MediaBrowserItem.KindOf(f) is MediaBrowserKind.Video or MediaBrowserKind.Image) ?? files[0];
        var rest = files.Where(f => f != first && MediaBrowserItem.KindOf(f) != MediaBrowserKind.Audio).ToArray();
        var sounds = files.Where(f => f != first && MediaBrowserItem.KindOf(f) == MediaBrowserKind.Audio).ToArray();
        try
        {
            if (request.StylePath is { Length: > 0 } style)
            {
                await OpenWithStyleAsync(first, style, rest);
            }
            else
            {
                await _viewModel.LoadFileAsync(first);
                await FollowOpenedFileAsync(rest);
            }

            if (sounds.Length > 0 && _viewModel.HasSource)
                await AddDroppedAudioAsync(sounds, 0);
            _viewModel.KeepNewProjectFrame(request);
        }
        catch (Exception ex)
        {
            AppLog.Write($"New project \"{request.Name}\": its files could not all be opened", ex);
            _viewModel.StatusText = $"The new project's files could not all be opened: {ex.Message}";
        }
    }
}
