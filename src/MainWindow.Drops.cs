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

// ----- File and link drops on the window -----
// DropRouter (src/Shell) owns payload parsing, routing and the empty-window drop overlay; this partial executes routes: place clips on
// the timeline, open files and projects, download links, squish files.
public partial class MainWindow
{
    // ----- Drop overlay target handlers (targets created and shown by DropRouter; wired in MainWindow.xaml) -----

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _drops.CanOpen(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropTarget_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 0.7;

        // Over a Game Preset target with an external (Explorer) drag: prewarm the file; internal Media Browser/Clip Library drags skip this.
        if (sender is FrameworkElement { DataContext: DropTargetItem { Target: StyleFile } } && _drops.Dragged(e.Data).File is { } carried && !DropRouter.IsInternalDrag(e.Data))
            _drops.PrewarmDrop(carried, carried: true);
    }

    private void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
    }

    // Timer from the last Game Preset drop (logged again when its Overview Scrubber sheet arrives).
    private System.Diagnostics.Stopwatch? _dropClock;

    /// <summary>Overlay target drop: open with that Game Preset, or squish with that preset.</summary>
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

        // File or link (downloaded en route): a Game Preset applies to the loaded video; the Squisher processes the download.
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

    /// <summary>Opens the first file with a Game Preset and appends the rest in line, each inheriting the preset's template.</summary>
    private async Task OpenWithStyleAsync(string first, string style, string[] rest)
    {
        await OpenNamingTabAsync(first, () => _viewModel.LoadFileWithStyleAsync(first, style));
        await FollowOpenedFileAsync(rest);
    }

    /// <summary>
    /// Opens a file into the active tab; an empty tab takes the file name immediately (<see cref="NameTabForOpen"/>), not after probing,
    /// and ends with the open's resulting name (the file's, or New Project on failure).
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

    /// <summary>Appends files end to end on the opened file's track after it (Clip 1, Clip 2, Clip 3).</summary>
    private async Task FollowOpenedFileAsync(string[] files)
    {
        if (files.Length > 0 && _viewModel.HasSource)
            await AddDroppedLayersAsync(files, _viewModel.DurationMs / 1000, _viewModel.OpenedFileView, inLine: true);
    }

    /// <summary>Logs Game Preset drop-to-open time (the Overview Scrubber sheet logs its own line).</summary>
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

        // Start hidden and go straight to the processing view (no open-then-shrink flash).
        OpenLaunchWindow(startHidden: true);
        var window = _launchWindow;
        await Dispatcher.BeginInvoke(() => window?.StartSquish(file, squish, instant: true), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ----- Window drag and drop -----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _drops.CanOpen(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // The "Drag & Drop" hint lives in the player's overlay window, outside the main window's drag routing, so it raises the drop overlay
    // itself. The overlay then hides the player (and hint), handing the rest of the drag to the main window.
    private void DropHint_DragOver(object sender, DragEventArgs e)
    {
        _drops.ShowDropOverlay(e);
        Window_DragOver(sender, e);
    }

    // File drags are accepted on the tunnelling (Preview) pass: TextBoxes (e.g. the Audio tab's number boxes) reject file drags on the
    // bubbling pass before the window sees them. Accepting here works anywhere on the window while text boxes keep text drag-and-drop.

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!_drops.CanOpen(e.Data))
            return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        WorkScheduler.HoldBackground(DropRouter.DragHold);

        // Over the timeline: show the landing indicator.
        ShowDropMark(e);
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        // Mid tab switch, the drop would land in the outgoing tab after its final write: reject and ask to retry.
        if (_switchingTab)
        {
            e.Handled = true;
            _viewModel.StatusText = "Wait for the project tab to finish switching, then drop it again.";
            return;
        }

        // Dropped on an overlay target: route it here, since this Preview handler consumes window drops before the targets' Drop fires.
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

        // Sub-clip from the Media Browser cutter: only its range goes on the timeline.
        var range = files.Length == 1 && e.Data.GetDataPresent(Views.MediaBrowserPane.SubClipFormat)
            ? Views.MediaBrowserPane.ParseSubClip(e.Data.GetData(Views.MediaBrowserPane.SubClipFormat))
            : null;

        // Link: download (yt-dlp) and place at the drop point or playhead; open as the video only on an empty Sequence (never replace an
        // open project).
        if (link is not null)
        {
            DropLink(link, route, place, playhead);
            return;
        }

        // No route (e.g. a Media Browser drag released over the Media Browser): release any prewarm.
        if (route == DropRoute.None)
        {
            _drops.UnPrewarm();
            return;
        }

        var file = files[0];
        switch (route)
        {
            // Timeline drop with media open: clips at the drop time by the target row (FindDropPlace); an audio row adds audio clips.
            // Multiple files go end to end on one track, or one track each at the same time on a header or with Alt (IsStackDrop).
            case DropRoute.TimelinePlace when place is { } on:
                // Alt with one file on a clip: swap its media in place (same place, length, look and effects).
                if (files.Length == 1 && range is null && !on.OnHeader && e.KeyStates.HasFlag(DragDropKeyStates.AltKey) && on.Row?.Data is Layer swapTrack
                    && ClipViewAt(swapTrack, on.Seconds) is { } swapped)
                {
                    DropLater("a media swap", () => SwapDroppedMediaAsync(swapped, file));
                    return;
                }

                var stack = IsStackDrop(e, on.OnHeader, files.Length);
                if (on.Row is { Kind: TimelineRowKind.Audio })
                    DropLater("a drop of sound", () => AddDroppedAudioAsync(files, on.Seconds, range));
                else
                    DropLater("a drop of clips", () => AddDroppedLayersAsync(files, on.Seconds, DropTrackOf(on.Row), stack, range: range));
                return;

            // Audio tab drop with media open: add the files' audio at the playhead as separate audio clips.
            case DropRoute.AudioTab:
                DropLater("a drop of sound", () => AddDroppedAudioAsync(files, playhead, range));
                return;

            // Elsewhere (Player, Canvas, empty space) with clips on the Sequence, or a Media Browser drag with media open: add at the
            // playhead (Track 1 or own track per the New Clips setting), never replacing what is open. Deferred out of the drop handler
            // (Explorer blocks until it returns); cutter sub-clips add only their range.
            case DropRoute.AddAtPlayhead:
                var media = DropRouter.PlayheadFiles(files, fromBrowser && _viewModel.HasSource);
                if (range is { } part && media.Length == 1)
                    AddSubClip(media[0], playhead, part, sound: false);
                else
                    Dispatcher.BeginInvoke(() => AddFilesToTimeline(media, playhead));
                return;

            // Media Browser pane drop (also with nothing open): add to Project Media (one Undo step), nothing on the timeline, and show
            // Project Media.
            case DropRoute.AddToProjectMedia:
                var pooled = DropRouter.PlayheadFiles(files, fromBrowser: false);
                Dispatcher.BeginInvoke(() =>
                {
                    _viewModel.AddToProjectMedia(pooled);
                    MediaBrowser.ShowProjectMedia();
                });
                return;

            // Project dropped while something is open or held (pooled files, undo history): open in its own Project Tab (or the active
            // tab if empty), never replacing content and never gated by a prompt.
            case DropRoute.OpenProject:
                DropLater("a dropped project", () => OpenProjectInTabAsync(file));
                return;

            // Multiple files with nothing open: open the first and append the rest in line.
            case DropRoute.OpenThenFollow:
                var rest = DropRouter.InLineAfter(files);
                DropLater("a drop of several files", async () =>
                {
                    await OpenNamingTabAsync(file, () => _viewModel.LoadFileAsync(file));
                    await FollowOpenedFileAsync(rest);
                });
                return;

            // Defer: a dialog inside the drop handler would block Explorer until it returns.
            default:
                Dispatcher.BeginInvoke(() => LoadDroppedFile(file));
                return;
        }
    }

    /// <summary>
    /// True if the drop point is inside a visible Media Browser in the same window as the drop (side windows route drops here too, so a
    /// pane in another window never counts).
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
    /// Link drop: on the timeline or Audio tab with media open, download (yt-dlp) and place like a file; elsewhere with clips, download
    /// and add at the playhead; on an empty Sequence, open as the source as if typed into the source box.
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
    /// Runs drop work after the drop handler returns (Explorer blocks until then); faults surface as a status line and log entry via
    /// TimelineFault, like timeline edits.
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
    /// Adds dropped files' audio (audio files or video audio) as audio clips. Background work is held until placed; file existence is
    /// checked off the UI thread.
    /// </summary>
    /// <param name="range">Source range in file seconds (Media Browser cutter sub-clip).</param>
    private async Task AddDroppedAudioAsync(string[] files, double seconds, (double In, double Out)? range = null)
    {
        // BeginAdd blocks tab switches until the last file is placed; the generation check aborts if the tab changed anyway.
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
    /// Adds dropped files as clips (images as image clips, others as video). Placement per MainViewModel.PlaceClip: own track above the
    /// row, an empty track, or the row's track when Settings allow and it fits. Multiple videos go end to end (Sequence Drop) or, with
    /// <paramref name="stack"/>, one track each at the same time (Stack Drop). Clips dropped on a templated track inherit it with their own
    /// Cutouts (<see cref="MainViewModel.InheritTemplate"/>). Background work is held until placed (video probes stay urgent, ahead of
    /// Media Browser posters); file existence is checked off the UI thread.
    /// </summary>
    /// <param name="inLine">Force the first file onto the row's track regardless of Settings.</param>
    /// <param name="range">Video source range in MediaOffset seconds (Media Browser cutter sub-clip).</param>
    private async Task AddDroppedLayersAsync(string[] files, double seconds, Layer? row, bool stack = false, bool inLine = false, (double In, double Out)? range = null)
    {
        // BeginAdd blocks tab switches until the last file is placed; the generation check aborts if the tab changed anyway.
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

            // Stack: the next video targets this clip's track at the same time (occupied, so it gets a new track above). Sequence: it
            // follows on the same track at this clip's end.
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
            // Prompt: save as project then load, load over, or cancel.
            var dialog = new ReplaceVideoDialog(_viewModel.SourcePath, file) { Owner = this };
            dialog.ShowDialog();
            if (dialog.Choice == ReplaceVideoChoice.Cancel)
                return;

            // Abort the load if the save failed, so nothing is lost.
            if (dialog.Choice == ReplaceVideoChoice.SaveAndLoad && !_viewModel.SaveProject(dialog.ProjectName).IsSaved)
            {
                return;
            }

            _viewModel.LoadFile(file);
            return;
        }

        // Nothing open: name the tab immediately and report faults like other drops.
        DropLater("opening a file", () => OpenNamingTabAsync(file, () => _viewModel.LoadFileAsync(file)));
    }
}
