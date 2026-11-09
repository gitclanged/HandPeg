using System.IO;
using HandPegApp.Models;

namespace HandPegApp;

// The media browser's hookup to the main window (5.4C, moved out of the constructor): what it opens, the files it lists as Project
// Media (the project's bin, TimelineDocument.MediaPool) and puts into it or takes out of it, and what it puts on the timeline at the
// playhead (whole files, or a sub-clip from its cutter).
public partial class MainWindow
{
    private void InitializeMediaBrowser()
    {
        MediaBrowser.HasVideo = () => _viewModel.HasSource;
        MediaBrowser.SuggestFolder = () => _viewModel.HasSource ? Path.GetDirectoryName(_viewModel.LocalMediaPath) : null;
        MediaBrowser.LoadRequested += path => LoadDroppedFile(path);
        MediaBrowser.ProjectFiles = _viewModel.ProjectMediaFiles;
        MediaBrowser.IsFileInUse = _viewModel.IsFileInUse;

        // Project Media is listed again once things settle when its files may have changed: a clip added or deleted, a file put into the
        // bin or taken out (an edit of the document: TimelineChanged), another project or video. Only a change of the files lists anew.
        _viewModel.Layers.CollectionChanged += (_, _) => MediaBrowser.ProjectChanged();
        _viewModel.MediaLoaded += _ => MediaBrowser.ProjectChanged();
        _viewModel.TimelineChanged += MediaBrowser.ProjectChanged;
        MediaBrowser.ProjectMediaAddRequested += files => _viewModel.AddToProjectMedia(files);
        MediaBrowser.ProjectMediaRemoveRequested += files => _viewModel.RemoveFromProjectMedia(files);

        // A question whose yes deletes clips (5.4D, Q5 b: a used file out of Project Media with its clips): Enter presses Cancel.
        _viewModel.ConfirmWarning = (title, message, confirm) => new ConfirmDialog(title, message, confirm) { Owner = this, DefaultsToCancel = true }.ShowDialog() == true;
        MediaBrowser.AddRequested += (files, sound) =>
        {
            var at = _viewModel.PositionMs / 1000;
            _ = sound ? AddDroppedAudioAsync([.. files], at) : AddDroppedLayersAsync([.. files], at, null);
        };

        // The cutter's Insert (and Enter): the part marked, at the playhead.
        MediaBrowser.AddRangeRequested += (file, start, end, sound) =>
            AddSubClip(file, _viewModel.PositionMs / 1000, double.IsNaN(start) ? null : (start, end), sound);
    }

    /// <summary>A sub-clip of a file (5.4C: seconds of its MediaOffset clock; null: the whole file) put on the timeline at a moment: as a
    /// clip, or a sound clip of its own.</summary>
    private void AddSubClip(string file, double at, (double In, double Out)? range, bool sound) =>
        DropLater("adding a sub-clip", () => sound || MediaBrowserItem.KindOf(file) == MediaBrowserKind.Audio
            ? AddDroppedAudioAsync([file], at, range)
            : AddDroppedLayersAsync([file], at, null, range: range));
}
