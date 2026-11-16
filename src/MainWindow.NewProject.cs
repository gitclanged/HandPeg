using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- New Project from the Project Manager -----
// The Project Manager's New Project mode returns a NewProjectRequest without opening anything. The window first installs a fresh empty
// project in its own tab (MainWindow.ProjectTabs.cs), then builds it here: applies Project Settings and the Game Preset
// (MainViewModel.TakeNewProject), adds the chosen files to Project Media, and lays them on the Sequence as a drop onto an empty
// project would.
public partial class MainWindow
{
    /// <summary>
    /// Populates the already-installed empty project: settings, Game Preset and files (first visual opened with the preset if chosen,
    /// other visuals appended on its track, audio at the start). With no files, the preset layout applies to the first file opened later.
    /// The name is kept for the first save.
    /// </summary>
    internal async Task CreateProjectAsync(NewProjectRequest request)
    {
        if (!_viewModel.TakeNewProject(request))
            return;

        var files = request.Files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

        // Add every chosen file to Project Media first, without an Undo step (project creation is not an edit), so files that fail to
        // open or are left off the Sequence remain in the bin.
        _viewModel.AddToProjectMedia(files, undoable: false);
        if (files.Count == 0)
        {
            _viewModel.StatusText = $"New project \"{request.Name}\": {MainViewModel.Describe(_viewModel.CurrentProjectSettings)}. Drop a video on the player, or use Add Media, to start.";
            return;
        }

        // First video/image opens the project (audio only when no visual exists); other visuals follow in sequence; audio files go on
        // audio tracks at time 0.
        var first = files.FirstOrDefault(f => MediaFiles.KindOf(f) is MediaBrowserKind.Video or MediaBrowserKind.Image) ?? files[0];
        var rest = files.Where(f => f != first && MediaFiles.KindOf(f) != MediaBrowserKind.Audio).ToArray();
        var sounds = files.Where(f => f != first && MediaFiles.KindOf(f) == MediaBrowserKind.Audio).ToArray();
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
