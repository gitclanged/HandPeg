using System.IO;
using HandPegApp.Models;

namespace HandPegApp;

// Media Browser wiring to the main window: file opening, Project Media listing and add/remove (TimelineDocument.MediaPool), and
// placing whole files or cutter sub-clips on the Timeline at the playhead.
public partial class MainWindow
{
    private void InitializeMediaBrowser()
    {
        MediaBrowser.HasVideo = () => _viewModel.HasSource;
        MediaBrowser.SuggestFolder = () => _viewModel.HasSource ? Path.GetDirectoryName(_viewModel.LocalMediaPath) : null;
        MediaBrowser.LoadRequested += path => LoadDroppedFile(path);
        MediaBrowser.ProjectFiles = _viewModel.ProjectMediaFiles;
        MediaBrowser.IsFileInUse = _viewModel.IsFileInUse;

        // Re-list Project Media (debounced) when its files may have changed: clip add/delete, bin add/remove (TimelineChanged), or a
        // different project or video. Only an actual file-set change triggers a new listing.
        _viewModel.Layers.CollectionChanged += (_, _) => MediaBrowser.ProjectChanged();
        _viewModel.MediaLoaded += _ => MediaBrowser.ProjectChanged();
        _viewModel.TimelineChanged += MediaBrowser.ProjectChanged;
        MediaBrowser.ProjectMediaAddRequested += files => _viewModel.AddToProjectMedia(files);
        MediaBrowser.ProjectMediaRemoveRequested += files => _viewModel.RemoveFromProjectMedia(files);

        // Destructive confirmations (e.g. removing a used file deletes its clips) default to Cancel on Enter.
        _viewModel.ConfirmWarning = (title, message, confirm) => new ConfirmDialog(title, message, confirm) { Owner = this, DefaultsToCancel = true }.ShowDialog() == true;
        MediaBrowser.AddRequested += (files, sound) =>
        {
            var at = _viewModel.PositionMs / 1000;
            _ = sound ? AddDroppedAudioAsync([.. files], at) : AddDroppedLayersAsync([.. files], at, null);
        };

        // Cutter Insert (or Enter): place the marked range at the playhead.
        MediaBrowser.AddRangeRequested += (file, start, end, sound) =>
            AddSubClip(file, _viewModel.PositionMs / 1000, double.IsNaN(start) ? null : (start, end), sound);
    }

    /// <summary>Places a file range (MediaOffset seconds; null = whole file) on the Timeline at <paramref name="at"/>, as a visual clip or
    /// an audio clip.</summary>
    private void AddSubClip(string file, double at, (double In, double Out)? range, bool sound) =>
        DropLater("adding a sub-clip", () => sound || MediaFiles.KindOf(file) == MediaBrowserKind.Audio
            ? AddDroppedAudioAsync([file], at, range)
            : AddDroppedLayersAsync([file], at, null, range: range));
}
