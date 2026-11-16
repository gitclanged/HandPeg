using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Shell;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

// ----- Drops: files and links dropped on the window -----
// What a drag carries, where its drop goes and the drop overlay of an empty window are DropRouter's (src/Shell); this side runs the
// route: clips placed on the timeline, files and projects opened, a link downloaded, a file squished.
public partial class MainWindow
{
    // ----- The drop overlay's targets (DropRouter makes and shows them; these are their handlers in MainWindow.xaml) -----

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _drops.CanOpen(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropTarget_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 0.7;

        // Over a Game Preset: its drop is near (a drag from Explorer; never one out of HandPeg's own Media Browser or Clip Library, 5.4.4).
        if (sender is FrameworkElement { DataContext: DropTargetItem { Target: StyleFile } } && _drops.Dragged(e.Data).File is { } carried && !DropRouter.IsInternalDrag(e.Data))
            _drops.PrewarmDrop(carried, carried: true);
    }

    private void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
    }

    // When the last drop on a Game Preset was let go (the timing line written when its Sequence Overview Scrubber sheet comes).
    private System.Diagnostics.Stopwatch? _dropClock;

    /// <summary>A file let go on a target: opened with that Game Preset, opened and given that encoding preset, or squished for that preset.</summary>
    private void DropTarget_Drop(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: DropTargetItem item })
            RouteDrop(item, e);
    }

    private void RouteDrop(DropTargetItem item, DragEventArgs e)
    {
        _drops.HideForTarget();

        // A file, or a link, which is downloaded on the way: a Game Preset is given to the video the link loads, and the
        // Squisher squishes the download.
        var (dropped, link) = _drops.Dragged(e.Data);
        var source = dropped is not null && File.Exists(dropped) ? dropped : link;
        if (source is null)
            return;

        AppLog.Write($"Dropped {(link is null ? Path.GetFileName(source) : "a link")} on the overlay target {item.Name}.");
        WorkScheduler.HoldBackground(DropRouter.DropHold);
        switch (item.Target)
        {
            case StyleFile style:
                _dropClock = System.Diagnostics.Stopwatch.StartNew();
                _ = TimeStyleDropAsync(OpenWithStyleAsync(source, style.Path, link is null ? DropRouter.InLineAfterFirst(e.Data) : []), _dropClock);
                break;

            case SquishPreset squish:
                _ = SquishDroppedAsync(source, squish);
                break;
        }
    }

    /// <summary>The first file opened with a Game Preset, and those dropped with it after it, in line, each with the Game Preset's template (5.1-DogfoodPolish).</summary>
    private async Task OpenWithStyleAsync(string first, string style, string[] rest)
    {
        await OpenNamingTabAsync(first, () => _viewModel.LoadFileWithStyleAsync(first, style));
        await FollowOpenedFileAsync(rest);
    }

    /// <summary>
    /// A file opened into the tab in front (5.4D-TabPerf): with nothing open in it, the tab takes the file's name as the open begins
    /// (<see cref="NameTabForOpen"/>), not once the file is probed and open, and holds the name the open leaves as it ends, whichever way
    /// (its file's; New Project again when it failed).
    /// </summary>
    private async Task OpenNamingTabAsync(string file, Func<Task> open)
    {
        var named = NameTabForOpen(file);
        try
        {
            await open();
        }
        finally
        {
            if (named)
                EndTabOpening();
        }
    }

    /// <summary>Files put after the file just opened, one after another on its clip's track: Clip 1, Clip 2, Clip 3.</summary>
    private async Task FollowOpenedFileAsync(string[] files)
    {
        if (files.Length > 0 && _viewModel.HasSource)
            await AddDroppedLayersAsync(files, _viewModel.DurationMs / 1000, _viewModel.OpenedFileView, inLine: true);
    }

    /// <summary>How long a drop on a Game Preset took to open its file with it (the log; the Sequence Overview Scrubber's sheet adds its own line).</summary>
    private static async Task TimeStyleDropAsync(Task loading, System.Diagnostics.Stopwatch clock)
    {
        try
        {
            await loading;
            AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Drop on a Game Preset: opened with the Game Preset in {clock.ElapsedMilliseconds} ms."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Drop on a Game Preset", ex);
        }
    }

    private async Task SquishDroppedAsync(string source, SquishPreset squish)
    {
        var file = MainViewModel.IsWebAddress(source) ? await _viewModel.DownloadLinkAsync(source) : source;
        if (file is null)
            return;

        // Straight to the processing square: the window is not shown opening and then shrinking.
        OpenLaunchWindow(startHidden: true);
        var window = _launchWindow;
        await Dispatcher.BeginInvoke(() => window?.StartSquish(file, squish, instant: true), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ----- Drag and drop -----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _drops.CanOpen(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // The "Drag & Drop" hint lies over the player in a window of its own, which the main window's drag handlers do
    // not hear: a file dragged onto it brings the overlay up from here. (The overlay hides the player, and the hint
    // with it, so the rest of the drag is the main window's.)
    private void DropHint_DragOver(object sender, DragEventArgs e)
    {
        _drops.ShowDropOverlay(e);
        Window_DragOver(sender, e);
    }

    // Files from Explorer are answered on the way down to whatever they are over, not on the way back up.
    // The rows of the Audio tab are full of number boxes, and a text box answers a drag itself
    // ("no" to files) before the window is asked: over those, a drop was refused. Seen first, here, a file is
    // accepted wherever on the window it is, and the boxes keep their own dragging of text.

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!_drops.CanOpen(e.Data))
            return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        WorkScheduler.HoldBackground(DropRouter.DragHold);

        // Over the time bars: where the file would land is marked.
        ShowDropMark(e);
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        // A project tab switching (5.4A): what was dropped would land in the outgoing tab after its last write, so it waits.
        if (_switchingTab)
        {
            e.Handled = true;
            _viewModel.StatusText = "Wait for the project tab to finish switching, then drop it again.";
            return;
        }

        // Let go on one of the overlay's targets: that target has it. (This handler takes every file dropped on
        // the window before any element sees it, which is why the targets' own Drop was never reached.)
        if (_drops.OverlayShown && (e.OriginalSource as FrameworkElement)?.DataContext is DropTargetItem item)
        {
            e.Handled = true;
            RouteDrop(item, e);
            return;
        }

        if (_drops.CanOpen(e.Data))
            Window_Drop(sender, e);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var (dropped, link) = _drops.Dragged(e.Data);
        if (dropped is null && link is null)
            return;

        e.Handled = true;
        ClearDropMark();
        WorkScheduler.HoldBackground(DropRouter.DropHold);

        string[] files = link is not null ? [] : e.Data.GetData(DataFormats.FileDrop) as string[] ?? [dropped!];
        var place = FindDropPlace(e);
        var onAudioTab = place is null && _viewModel.HasSource && AudioPanel is { IsVisible: true } panel
            && e.GetPosition(panel) is var over && over.X >= 0 && over.Y >= 0 && over.X <= panel.ActualWidth && over.Y <= panel.ActualHeight;
        var hasClips = _viewModel.Document.Tracks.Exists(t => t.Clips.Count > 0);
        var fromBrowser = e.Data.GetDataPresent(Views.MediaBrowserPane.DragFormat);
        var onMediaBin = place is null && IsOverMediaBrowser(sender, e);
        var route = DropRouter.RouteOf(hasClips, _viewModel.HasSource, _viewModel.IsBusy, place is not null, onAudioTab, fromBrowser, link is not null, files, onMediaBin,
            holdsSomething: !_viewModel.HoldsNothingForTab);
        var playhead = _viewModel.PositionMs / 1000;

        // A sub-clip dragged out of the Media Browser's cutter (5.4C): only its part of the file goes on the timeline.
        var range = files.Length == 1 && e.Data.GetDataPresent(Views.MediaBrowserPane.SubClipFormat)
            ? Views.MediaBrowserPane.ParseSubClip(e.Data.GetData(Views.MediaBrowserPane.SubClipFormat))
            : null;

        // A link: downloaded (yt-dlp) and put where it was let go, or at the playhead; opened as the video only with nothing on
        // the Sequence (5.4A: never over an open project).
        if (link is not null)
        {
            DropLink(link, route, place, playhead);
            return;
        }

        // Nothing to do (5.4.4: a Media Browser drag let go over the Media Browser among it): the drag is over, and whatever it made ready let go.
        if (route == DropRoute.None)
        {
            _drops.UnPrewarm();
            return;
        }

        var file = files[0];
        switch (route)
        {
            // Dropped on the timeline, with a video open: the files become clips at the moment they were dropped on, by the
            // row they were dropped on (see FindDropPlace); on a sound's row, their sound becomes sound clips. Several at once
            // follow one another on one track, or, let go on a row's header or with Alt held, each go on a track of their
            // own at the same moment (IsStackDrop).
            case DropRoute.TimelinePlace when place is { } on:
                var stack = IsStackDrop(e, on.OnHeader, files.Length);
                if (on.Row is { Kind: TimelineRowKind.Audio })
                    DropLater("a drop of sound", () => AddDroppedAudioAsync(files, on.Seconds, range));
                else
                    DropLater("a drop of clips", () => AddDroppedLayersAsync(files, on.Seconds, DropTrackOf(on.Row), stack, range: range));
                return;

            // Dropped on the Audio tab (the sound's settings), with a video open: the files' sound goes onto the timeline, at
            // the playhead, as sound clips of their own.
            case DropRoute.AudioTab:
                DropLater("a drop of sound", () => AddDroppedAudioAsync(files, playhead, range));
                return;

            // Media dropped anywhere else while the Sequence has clips (5.4A, the user's standing rule: the Player, the Canvas,
            // empty space), or dragged from the media browser with a video or project open (4.1.1-B): the files go onto the timeline
            // at the playhead (Track 1 or a track of their own, as Settings' New Clips say), never over what is open. Never inside
            // the drop itself: Explorer waits, frozen, until the drop handler returns. A sub-clip from the cutter: its part.
            case DropRoute.AddAtPlayhead:
                var media = DropRouter.PlayheadFiles(files, fromBrowser && _viewModel.HasSource);
                if (range is { } part && media.Length == 1)
                    AddSubClip(media[0], playhead, part, sound: false);
                else
                    Dispatcher.BeginInvoke(() => AddFilesToTimeline(media, playhead));
                return;

            // Media dropped onto the Media Browser pane (5.4C, the user's standing rule; 5.4D, Q4 b: with nothing open too): the files go
            // into Project Media (the project's bin, one step of Undo), nothing onto the timeline; the pane shows Project Media with them in it.
            case DropRoute.AddToProjectMedia:
                var pooled = DropRouter.PlayheadFiles(files, fromBrowser: false);
                Dispatcher.BeginInvoke(() =>
                {
                    _viewModel.AddToProjectMedia(pooled);
                    MediaBrowser.ShowProjectMedia();
                });
                return;

            // A project dropped while one is open (5.4A), or while the blank window holds something (5.4D review: pooled files, an
            // undo step): it opens in a Project Tab of its own (or the open tab, while that is empty), never in place of what is open
            // and never behind a prompt.
            case DropRoute.OpenProject:
                DropLater("a dropped project", () => OpenProjectInTabAsync(file));
                return;

            // Several files with nothing open: the first is opened and the others follow it, in line.
            case DropRoute.OpenThenFollow:
                var rest = DropRouter.InLineAfter(files);
                DropLater("a drop of several files", async () =>
                {
                    await OpenNamingTabAsync(file, () => _viewModel.LoadFileAsync(file));
                    await FollowOpenedFileAsync(rest);
                });
                return;

            // The dialog must not open inside the drop itself: Explorer waits, frozen, until the drop handler returns.
            default:
                Dispatcher.BeginInvoke(() => LoadDroppedFile(file));
                return;
        }
    }

    /// <summary>
    /// Whether a drop was let go over the Media Browser pane (5.4C): the pane shown, in the same window as the drop (a side window passes
    /// its drops to these handlers too, so a pane in another window never counts), the point inside it.
    /// </summary>
    private bool IsOverMediaBrowser(object sender, DragEventArgs e)
    {
        if (!MediaBrowser.IsVisible || sender is not Visual target
            || !ReferenceEquals(PresentationSource.FromVisual(target), PresentationSource.FromVisual(MediaBrowser)))
            return false;

        var over = e.GetPosition(MediaBrowser);
        return over.X >= 0 && over.Y >= 0 && over.X <= MediaBrowser.ActualWidth && over.Y <= MediaBrowser.ActualHeight;
    }

    /// <summary>
    /// A link dropped on the window: on the timeline (or the Audio tab), with a video open, it is downloaded (yt-dlp) and put there
    /// as a dropped file would be; elsewhere, with clips on the Sequence, downloaded and put at the playhead (5.4A); with nothing on
    /// the Sequence, opened as the video, as if typed into the source box.
    /// </summary>
    private void DropLink(string link, DropRoute route, (TimelineRow? Row, double Seconds, bool OnHeader)? place, double playhead)
    {
        if (route is not (DropRoute.TimelinePlace or DropRoute.AudioTab or DropRoute.AddAtPlayhead))
        {
            Dispatcher.BeginInvoke(() => LoadDroppedFile(link));
            return;
        }

        var audio = route == DropRoute.AudioTab || place is { Row.Kind: TimelineRowKind.Audio };
        var track = route == DropRoute.TimelinePlace && place is { } on ? DropTrackOf(on.Row) : null;
        var seconds = route == DropRoute.TimelinePlace && place is { } at ? at.Seconds : playhead;
        DropLater("a dropped link", async () =>
        {
            if (await _viewModel.DownloadLinkAsync(link) is not { } file)
                return;
            await (audio ? AddDroppedAudioAsync([file], seconds) : AddDroppedLayersAsync([file], seconds, track));
        });
    }

    /// <summary>
    /// Runs what a drop adds once the drop itself is over (Explorer waits, frozen, until the drop handler returns), and
    /// keeps a fault in it from going unseen (4.4 BugHunt): it becomes a status line and a note in the log, as a timeline
    /// edit's does (TimelineFault).
    /// </summary>
    private void DropLater(string what, Func<Task> add) => Dispatcher.BeginInvoke(async () =>
    {
        try
        {
            await add();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TimelineFault(what, ex);
        }
    });

    /// <summary>
    /// Adds the sound of dropped files to the timeline: audio files, or the audio of videos. Background work is held back
    /// until they are on it (4.3E), and whether the files are there is asked away from the interface.
    /// </summary>
    /// <param name="range">Only this part of the file (5.4C, a sub-clip from the Media Browser's cutter), in seconds of the file.</param>
    private async Task AddDroppedAudioAsync(string[] files, double seconds, (double In, double Out)? range = null)
    {
        // An add under way from the start (5.4D-TabPerf review, P1: the tabs do not change until the last file is in), and none placed
        // once the project tab it was dropped on has gone behind all the same.
        using var adding = _viewModel.BeginAdd();
        var generation = _viewModel.ProjectGeneration;
        using var hold = WorkScheduler.HoldBackground();
        Activate();
        var added = 0;
        foreach (var file in await Task.Run(() => files.Where(File.Exists).ToArray()))
        {
            if (generation != _viewModel.ProjectGeneration)
                return;
            if (await _viewModel.AddAudioLayerAsync(file, seconds, sourceRange: range))
                added++;
        }

        if (generation != _viewModel.ProjectGeneration)
            return;

        if (added > 1)
            _viewModel.StatusText = $"Added {added} audio clips. Each is mixed into the first audio track; drag a block to move it.";
        ScheduleClipRedraw();
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>
    /// Adds dropped files as clips: pictures as image clips, anything else as video clips. Each goes on a track of
    /// its own just above the row (or fills an empty track, or joins the row's track when Settings allow and there is
    /// room: MainViewModel.PlaceClip). Several videos dropped
    /// at once follow each other on one track, end to end, from the moment dropped on (a Sequence Drop); stacked
    /// (<paramref name="stack"/>, 4.4: a Stack Drop), each goes on a track of its own just above the one before, all at that
    /// moment. A clip dropped onto a track with a template takes that template, with Cutouts of its own over its own span
    /// (5.1-DogfoodPolish, <see cref="MainViewModel.InheritTemplate"/>). Background work is held back until they are on it and filled
    /// in (4.3E: each video's probe is urgent, never behind the media browser's posters and strips), and whether the files are there
    /// is asked away from the interface.
    /// </summary>
    /// <param name="inLine">The first goes onto the row's track too, whatever Settings say.</param>
    /// <param name="range">Only this part of a video (5.4C, a sub-clip from the Media Browser's cutter), in seconds of its MediaOffset clock.</param>
    private async Task AddDroppedLayersAsync(string[] files, double seconds, Layer? row, bool stack = false, bool inLine = false, (double In, double Out)? range = null)
    {
        // An add under way from the start (5.4D-TabPerf review, P1: the tabs do not change until the last file is in), and none placed
        // once the project tab it was dropped on has gone behind all the same.
        using var adding = _viewModel.BeginAdd();
        var generation = _viewModel.ProjectGeneration;
        using var hold = WorkScheduler.HoldBackground();
        Activate();
        if (!_viewModel.IsVideoReencoded)
        {
            _viewModel.StatusText = "Clips on other tracks need the video to be encoded: choose a video codec other than Copy on the Export tab, then drop the file again.";
            return;
        }

        var existing = await Task.Run(() => files.Where(File.Exists).ToArray());
        var (added, at, videos) = (0, seconds, existing.Count(f => !ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
        var follows = inLine;
        var droppedOn = row;
        foreach (var file in existing)
        {
            if (generation != _viewModel.ProjectGeneration)
                return;
            if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                if (_viewModel.AddImageLayer(file, at, row))
                    added++;
                continue;
            }

            if (await _viewModel.AddVideoClipAsync(file, at, row, playOnce: videos > 1 || inLine, follows: follows, sourceRange: range) is not { } clip)
                continue;

            added++;
            _viewModel.InheritTemplate(clip, droppedOn, at);

            // Stacked, the next video goes beside this one's track, at the same moment: there is no room on its track, so
            // it gets a track of its own just above. Otherwise it goes on the same track, where this one ends.
            if (stack)
                row = clip;
            else if (clip.Duration > 0.001)
                (row, at, follows) = (clip, clip.StartTime + clip.Duration, true);
        }

        if (generation != _viewModel.ProjectGeneration)
            return;
        if (added > 1)
        {
            _viewModel.StatusText = stack
                ? $"Stacked {added} clips at {TimeDisplay.Format(seconds)}, each on a track of its own."
                : $"Added {added} clips from {TimeDisplay.Format(seconds)}.";
        }

        ScheduleClipRedraw();
    }

    private void LoadDroppedFile(string file)
    {
        Activate();

        if (_viewModel.IsBusy)
        {
            _viewModel.StatusText = "Wait for the running operation to finish, or cancel it, before dropping another video.";
            return;
        }

        if (_viewModel.HasSource)
        {
            // Save what is open as a project first, load over it, or leave things as they are.
            var dialog = new ReplaceVideoDialog(_viewModel.SourcePath, file) { Owner = this };
            dialog.ShowDialog();
            if (dialog.Choice == ReplaceVideoChoice.Cancel)
                return;

            // A project that could not be saved must not be followed by losing what it was meant to keep.
            if (dialog.Choice == ReplaceVideoChoice.SaveAndLoad && !_viewModel.SaveProject(dialog.ProjectName).IsSaved)
            {
                return;
            }

            _viewModel.LoadFile(file);
            return;
        }

        // Nothing open: the tab takes the file's name at once (5.4D-TabPerf), and the open's faults are said as a drop's are.
        DropLater("opening a file", () => OpenNamingTabAsync(file, () => _viewModel.LoadFileAsync(file)));
    }
}
