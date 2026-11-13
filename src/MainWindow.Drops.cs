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
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

// ----- Drops: the drop overlay's targets, and files and links dropped on the window -----
// A file dragged in shows the drop overlay (Game Presets, the Social Squisher's presets); one dropped on the
// window (or a link, or a tile from the Media pane) is routed to the timeline, the Canvas or the player.
public partial class MainWindow
{
    // ----- The drop overlay: targets for a file that is being dragged in -----

    private bool _dropTargetsBuilt;
    private string _dropTargetsMode = "";

    /// <summary>
    /// Makes the overlay's targets: the Game Presets and the Social Squisher's presets. Their background is the
    /// accent colour, toned down, so that a whole screen of them does not shout.
    /// </summary>
    private void BuildDropTargets()
    {
        var settings = AppSettings.Current;
        var presets = StyleFile.ForLaunch().Select(style => new DropTargetItem(style.Name, DropTargetItem.StyleIcon, style)).ToList();
        var squish = settings.ShowSquisher
            ? settings.SquisherPresets.Where(p => p.TargetSizeMb > 0).Take(Math.Clamp(settings.SquisherPresetCount, 1, 12)).Select(p => new DropTargetItem(p.Name, p.IconData, p)).ToList()
            : [];
        (DropPresetList.ItemsSource, DropSquishList.ItemsSource) = (presets, squish);
        DropPresetTitle.Text = "Open with a Game Preset";
        DropPresetSection.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DropSquishSection.Visibility = squish.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var accent = (FindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.SteelBlue;
        var grey = (byte)((accent.R + accent.G + accent.B) / 3);
        byte Mute(byte channel) => (byte)(channel * 0.55 + grey * 0.30 + 20);
        var muted = new SolidColorBrush(Color.FromRgb(Mute(accent.R), Mute(accent.G), Mute(accent.B)));
        muted.Freeze();
        Resources["DropTargetBrush"] = muted;
        (_dropTargetsBuilt, _dropTargetsMode) = (true, DropTargetsSignature());
    }

    /// <summary>
    /// Makes the overlay's targets ahead of time, once the window is up and idle, and lays the overlay out while it
    /// is still hidden (Hidden, not Collapsed, keeps it laid out), so that the first drag shows it at once.
    /// </summary>
    private void PrepareDropTargets()
    {
        BuildDropTargets();
        if (DropOverlay.Visibility == Visibility.Collapsed)
            DropOverlay.Visibility = Visibility.Hidden;
    }

    /// <summary>What the overlay's targets are made from: when this changes, they are made again.</summary>
    private string DropTargetsSignature()
    {
        var settings = AppSettings.Current;
        long styles;
        try
        {
            styles = Directory.Exists(AppPaths.Styles) ? Directory.GetLastWriteTimeUtc(AppPaths.Styles).Ticks : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            styles = 0;
        }

        return string.Join("|", styles, string.Join(",", settings.SplashStylePresets), settings.SplashPresetCount,
            settings.ShowSquisher, settings.SquisherPresetCount, string.Join(",", settings.SquisherPresets.Select(p => $"{p.Name}:{p.TargetSizeMb}")),
            (FindResource("AccentBrush") as SolidColorBrush)?.Color.ToString() ?? "");
    }


    // A drag "leaves" every element it passes out of, on its way to the next one inside the window, and where
    // the pointer is said to be at that moment cannot be relied on at the window's edge. So leaving only
    // starts a short wait: the drag is still in the window if it is heard from again before the wait is out.
    private readonly System.Windows.Threading.DispatcherTimer _dropLeave = new() { Interval = TimeSpan.FromMilliseconds(140) };

    private void ShowDropOverlay(DragEventArgs e)
    {
        _dropLeave.Stop();
        if (DropOverlay.Visibility == Visibility.Visible || !CanOpen(e.Data))
            return;

        // Only into an empty window. With a video or a project open, a file dragged in is on its way to the
        // timeline, the Canvas or the Audio tab, and nothing is put in its way.
        if (_viewModel.HasSource || _viewModel.IsBusy)
            return;

        // A file on its way to a Game Preset: what its drop will need is made ready while it is carried (on a fast drive). Not one dragged
        // out of HandPeg's own Media Browser or Clip Library (5.4.4: an internal drag is on its way to the timeline).
        if (Dragged(e.Data).File is { } carried && !IsInternalDrag(e.Data))
            PrewarmDrop(carried, carried: true);

        // Made at launch, once the window was idle (see PrepareDropTargets): only made again when the mode or the
        // presets have changed since.
        if (DropTargetsSignature() != _dropTargetsMode)
            BuildDropTargets();
        DropOverlay.Visibility = Visibility.Visible;

        // The player is a native window, which WPF cannot draw over: it would stay bright in the middle of the
        // dimmed window. It is hidden for as long as the overlay is up.
        VideoView.Visibility = Visibility.Hidden;
    }

    // A file that leaves the window takes the overlay with it, a moment later (see above).
    private void LeaveDropOverlay(DragEventArgs e)
    {
        _dropLeave.Stop();
        _dropLeave.Start();
    }

    private void HideDropOverlay()
    {
        // Carried away, not dropped on a target: the player lets go of what it opened for it.
        if (!_routingDrop)
            UnPrewarm();
        _dropLeave.Stop();
        ClearDropMark();
        DropOverlay.Visibility = _dropTargetsBuilt ? Visibility.Hidden : Visibility.Collapsed;
        VideoView.Visibility = Visibility.Visible;
    }

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanOpen(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropTarget_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 0.7;

        // Over a Game Preset: its drop is near (a drag from Explorer; never one out of HandPeg's own Media Browser or Clip Library, 5.4.4).
        if (sender is FrameworkElement { DataContext: DropTargetItem { Target: StyleFile } } && Dragged(e.Data).File is { } carried && !IsInternalDrag(e.Data))
            PrewarmDrop(carried, carried: true);
    }

    /// <summary>A drag that began in HandPeg's own Media Browser or Clip Library (MediaBrowserPane.DragFormat), not in Explorer.</summary>
    private static bool IsInternalDrag(IDataObject data) => data.GetDataPresent(Views.MediaBrowserPane.DragFormat);

    private void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
    }

    // ----- Ahead of a drop (Pass 5.0-Release, the user's directive 5) -----
    // A video likely to be dropped (its Media tile rested on, a drag of it begun, carried over the drop overlay's Game Presets):
    // on a fast local drive (DriveKind), what its drop will ask for first is done now. Its inspection (MediaProbe keeps
    // it by FileState) and its key frames (kept by the seek index); the Sequence Overview Scrubber's worker started and the file opened
    // in it; the player's worker opening it and decoding its first frames. A drop is urgent and never waits behind any of
    // it; when no drop follows, the player lets go of it (UnPrewarm) and the Sequence Overview Scrubber's worker ends as ever when idle.

    // The file made ready last, and when (Environment.TickCount64): asked again within this long, nothing is done again.
    private string? _prewarmed;
    private long _prewarmedAt;
    private bool _prewarmedCarried;
    private const int PrewarmAgainMs = 10_000;

    // The inspection asked for the file made ready last (cancelled when another is made ready, or it is let go of).
    private CancellationTokenSource? _prewarmProbe;

    // When the last drop on a Game Preset was let go (the timing line written when its Sequence Overview Scrubber sheet comes), and whether a
    // drop on a target is being routed (the overlay is put away for it, and what was made ready is kept).
    private System.Diagnostics.Stopwatch? _dropClock;
    private bool _routingDrop;

    /// <summary>
    /// A video likely to be dropped: made ready ahead on a fast local drive. <paramref name="carried"/>: it is being dragged
    /// (its key frames are listed too: a long recording's listing reads the whole file, worth it only for a drag).
    /// </summary>
    private void PrewarmDrop(string path, bool carried)
    {
        HandPegApp.Services.GpuNativeBreadcrumbs.Mark("PrewarmDrop", (carried ? "carried " : "") + Path.GetFileName(path));
        if (string.IsNullOrWhiteSpace(path) || MainViewModel.IsWebAddress(path))
            return;

        // Learnt off the interface's thread, once per drive; until it is known, nothing (made ready when it is).
        if (!DriveKind.IsFast(path))
        {
            _ = LearnDriveThenPrewarmAsync(path, carried);
            return;
        }

        var now = Environment.TickCount64;
        if (string.Equals(path, _prewarmed, StringComparison.OrdinalIgnoreCase) && now - _prewarmedAt < PrewarmAgainMs && (_prewarmedCarried || !carried))
            return;
        var again = string.Equals(path, _prewarmed, StringComparison.OrdinalIgnoreCase);
        (_prewarmed, _prewarmedAt, _prewarmedCarried) = (path, now, carried);

        // Its inspection, as the drop asks for it: background work (held while a file is dragged, and never ahead of a
        // drop's or a project's own loading on the interactive lane: the drop's urgent probe covers it), the one asked for
        // the file made ready before let go of (5.0-Release review: tiles passed over queued a probe each ahead of the drop).
        if (!again || _prewarmProbe is null)
        {
            _prewarmProbe?.Cancel();
            _prewarmProbe = new CancellationTokenSource();
            _ = MediaProbe.ProbeAsync(path, _prewarmProbe.Token, WorkLane.Background);
        }

        // Its key frames, for a drag: off the interface's thread (what is known of them is read from the cache's disk first).
        if (carried)
            _ = Task.Run(() => FfmpegRunner.GetIFramesAsync(path, CancellationToken.None));

        // Opened in the player's worker. 5.4.4: no longer in the Sequence Overview Scrubber's worker as well (GpuProgramStrip, started ahead of the
        // drop when it was not running): on the Desktop that worker was on the Arc A310 and its start under a drag over the Game Presets
        // ended in 0x116 VIDEO_TDR_FAILURE. It opens the file after the drop, as it always did for a drop on the timeline.
        _gpu?.Prewarm(path);
    }

    private async Task LearnDriveThenPrewarmAsync(string path, bool carried)
    {
        try
        {
            if (await DriveKind.Learn(path))
                PrewarmDrop(path, carried);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Ahead of a drop", ex);
        }
    }

    /// <summary>No drop of the file made ready came (the drag left, or something else opened): the player lets go of it.</summary>
    private void UnPrewarm()
    {
        _gpu?.Prewarm(null);
        _prewarmed = null;
        _prewarmProbe?.Cancel();
        _prewarmProbe = null;
    }

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
        _routingDrop = true;
        HideDropOverlay();
        _routingDrop = false;

        // A file, or a link, which is downloaded on the way: a Game Preset is given to the video the link loads, and the
        // Squisher squishes the download.
        var (dropped, link) = Dragged(e.Data);
        var source = dropped is not null && File.Exists(dropped) ? dropped : link;
        if (source is null)
            return;

        AppLog.Write($"Dropped {(link is null ? Path.GetFileName(source) : "a link")} on the overlay target {item.Name}.");
        WorkScheduler.HoldBackground(DropHold);
        switch (item.Target)
        {
            case StyleFile style:
                _dropClock = System.Diagnostics.Stopwatch.StartNew();
                _ = TimeStyleDropAsync(OpenWithStyleAsync(source, style.Path, link is null ? InLineAfterFirst(e.Data) : []), _dropClock);
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

    /// <summary>The pictures a drag carries after its first file: what follows it in line when it is opened. Sounds and projects are no such thing.</summary>
    private static string[] InLineAfterFirst(IDataObject data) => data.GetData(DataFormats.FileDrop) is string[] files ? InLineAfter(files) : [];

    /// <summary>The same of a drop's files: those after the first that are neither a sound nor a project.</summary>
    internal static string[] InLineAfter(IReadOnlyList<string> files) =>
        files.Count > 1
            ? files.Skip(1).Where(f => !f.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase) && MediaBrowserItem.KindOf(f) != MediaBrowserKind.Audio).ToArray()
            : [];

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
        e.Effects = CanOpen(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // The "Drag & Drop" hint lies over the player in a window of its own, which the main window's drag handlers do
    // not hear: a file dragged onto it brings the overlay up from here. (The overlay hides the player, and the hint
    // with it, so the rest of the drag is the main window's.)
    private void DropHint_DragOver(object sender, DragEventArgs e)
    {
        ShowDropOverlay(e);
        Window_DragOver(sender, e);
    }

    // The last drag asked about, and what it carries: a drag is asked about at every move.
    private IDataObject? _draggedData;
    private (string? File, string? Link) _dragged;

    /// <summary>
    /// What a drag carries that HandPeg can open: files (from Explorer, or the media browser), or a link (from a web
    /// browser's address bar or page: any address yt-dlp may download, not only YouTube's).
    /// </summary>
    private (string? File, string? Link) Dragged(IDataObject data)
    {
        if (ReferenceEquals(data, _draggedData))
            return _dragged;

        _draggedData = data;
        _dragged = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            ? (files[0], null)
            : (null, DraggedLink(data));
        return _dragged;
    }

    private bool CanOpen(IDataObject data) => Dragged(data) is { File: not null } or { Link: not null };

    /// <summary>A web address a drag carries, in any of the forms browsers give one; null for none.</summary>
    private static string? DraggedLink(IDataObject data)
    {
        foreach (var format in new[] { "UniformResourceLocatorW", "UniformResourceLocator", "text/x-moz-url", DataFormats.UnicodeText, DataFormats.Text })
        {
            string? text;
            try
            {
                if (!data.GetDataPresent(format))
                    continue;

                text = data.GetData(format) switch
                {
                    string value => value,
                    MemoryStream stream when format is "UniformResourceLocatorW" or "text/x-moz-url" => System.Text.Encoding.Unicode.GetString(stream.ToArray()),
                    MemoryStream stream => System.Text.Encoding.Default.GetString(stream.ToArray()),
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException or InvalidOperationException)
            {
                continue;
            }

            // The address is the first line (Firefox puts the page's title on the next one).
            var first = text?.Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (first is not null && MainViewModel.IsWebAddress(first))
                return first;
        }

        return null;
    }

    // Files from Explorer are answered on the way down to whatever they are over, not on the way back up.
    // The rows of the Audio tab are full of number boxes, and a text box answers a drag itself
    // ("no" to files) before the window is asked: over those, a drop was refused. Seen first, here, a file is
    // accepted wherever on the window it is, and the boxes keep their own dragging of text.

    // A file dragged in holds background work back (WorkScheduler.HoldBackground, 4.3E) until a moment after its last move
    // (each move holds it again: a drag from another program has no end HandPeg is told of for sure), and a drop holds it
    // a moment more: until its own work, which holds it for as long as it takes, has begun (clips put on the timeline), or
    // while a video it opens is probed (its own I-frames, waveforms and proxy are background work, and are not held long).
    private static readonly TimeSpan DragHold = TimeSpan.FromMilliseconds(500), DropHold = TimeSpan.FromMilliseconds(400);

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!CanOpen(e.Data))
            return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        WorkScheduler.HoldBackground(DragHold);

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
        if (DropOverlay.Visibility == Visibility.Visible && (e.OriginalSource as FrameworkElement)?.DataContext is DropTargetItem item)
        {
            e.Handled = true;
            RouteDrop(item, e);
            return;
        }

        if (CanOpen(e.Data))
            Window_Drop(sender, e);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var (dropped, link) = Dragged(e.Data);
        if (dropped is null && link is null)
            return;

        e.Handled = true;
        ClearDropMark();
        WorkScheduler.HoldBackground(DropHold);

        string[] files = link is not null ? [] : e.Data.GetData(DataFormats.FileDrop) as string[] ?? [dropped!];
        var place = FindDropPlace(e);
        var onAudioTab = place is null && _viewModel.HasSource && AudioPanel is { IsVisible: true } panel
            && e.GetPosition(panel) is var over && over.X >= 0 && over.Y >= 0 && over.X <= panel.ActualWidth && over.Y <= panel.ActualHeight;
        var hasClips = _viewModel.Document.Tracks.Exists(t => t.Clips.Count > 0);
        var fromBrowser = e.Data.GetDataPresent(Views.MediaBrowserPane.DragFormat);
        var onMediaBin = place is null && IsOverMediaBrowser(sender, e);
        var route = RouteOf(hasClips, _viewModel.HasSource, _viewModel.IsBusy, place is not null, onAudioTab, fromBrowser, link is not null, files, onMediaBin,
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
            UnPrewarm();
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
                var media = PlayheadFiles(files, fromBrowser && _viewModel.HasSource);
                if (range is { } part && media.Length == 1)
                    AddSubClip(media[0], playhead, part, sound: false);
                else
                    Dispatcher.BeginInvoke(() => AddFilesToTimeline(media, playhead));
                return;

            // Media dropped onto the Media Browser pane (5.4C, the user's standing rule; 5.4D, Q4 b: with nothing open too): the files go
            // into Project Media (the project's bin, one step of Undo), nothing onto the timeline; the pane shows Project Media with them in it.
            case DropRoute.AddToProjectMedia:
                var pooled = PlayheadFiles(files, fromBrowser: false);
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
                var rest = InLineAfter(files);
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

    /// <summary>Where a file or link dropped on the window goes (5.4A): decided by <see cref="RouteOf"/>, a pure function the golden tests read.</summary>
    internal enum DropRoute
    {
        /// <summary>Nothing that can be opened.</summary>
        None,

        /// <summary>Onto the timeline where it was let go (FindDropPlace): a row and a moment.</summary>
        TimelinePlace,

        /// <summary>Onto the timeline at the playhead as sound clips (let go on the Audio tab).</summary>
        AudioTab,

        /// <summary>Onto the timeline at the playhead, as the Media pane's Add does (AddFilesToTimeline).</summary>
        AddAtPlayhead,

        /// <summary>Nothing open: the first file opened, the pictures after it following it in line.</summary>
        OpenThenFollow,

        /// <summary>Opened as the video (LoadDroppedFile): straight away with nothing open, or after the replace prompt.</summary>
        OpenOrReplace,

        /// <summary>A project opened in a Project Tab (OpenProjectInTabAsync): never in place of the open one.</summary>
        OpenProject,

        /// <summary>Onto the Media Browser pane (5.4C): into Project Media (TimelineDocument.MediaPool), nothing onto the timeline.</summary>
        AddToProjectMedia,
    }

    /// <summary>
    /// Where a drop goes (5.4A, the user's standing rule: once the Sequence has clips, media dropped anywhere in the window is added
    /// to its timeline, never opened over it). In order: on the timeline, where it was let go; on the Audio tab, its sound at the
    /// playhead; on the Media Browser pane (5.4C; 5.4D: with nothing open too), the media files among it into Project Media (a link, and a
    /// drag from the browser itself, go as anywhere else); with clips on the Sequence, the media files among it at the playhead (a link the
    /// same, once downloaded), or from the media browser with a video open, every file but a project; a project with something open (or
    /// held: 5.4D review), in a tab of its own; with nothing open, several files opened one after another; anything else opened as the
    /// video (the replace prompt when one is open).
    /// </summary>
    /// <param name="hasClips">The Sequence has at least one clip.</param>
    /// <param name="hasSource">A video or project is open (MainViewModel.HasSource).</param>
    /// <param name="busy">An operation is running (MainViewModel.IsBusy).</param>
    /// <param name="onTimeline">Let go over the timeline's rows (FindDropPlace found a place).</param>
    /// <param name="onAudioTab">Let go over the Audio tab, with a video open.</param>
    /// <param name="fromBrowser">Dragged from the Media pane's browser.</param>
    /// <param name="isLink">A web address, not files.</param>
    /// <param name="files">The files dropped (none for a link).</param>
    /// <param name="onMediaBin">Let go over the Media Browser pane (5.4C), in the window it is in.</param>
    /// <param name="holdsSomething">
    /// The tab in front holds something worth keeping with no video open (5.4D review: !MainViewModel.HoldsNothingForTab, files pooled in a
    /// blank window, say): a project dropped then opens in a tab of its own, as File &gt; Open does, never in place of the pool.
    /// </param>
    internal static DropRoute RouteOf(bool hasClips, bool hasSource, bool busy, bool onTimeline, bool onAudioTab, bool fromBrowser, bool isLink, IReadOnlyList<string> files,
        bool onMediaBin = false, bool holdsSomething = false)
    {
        if (isLink)
            return onTimeline ? DropRoute.TimelinePlace : onAudioTab ? DropRoute.AudioTab : hasClips ? DropRoute.AddAtPlayhead : DropRoute.OpenOrReplace;
        if (files.Count == 0)
            return DropRoute.None;
        if (onTimeline)
            return DropRoute.TimelinePlace;
        if (onAudioTab)
            return DropRoute.AudioTab;
        // 5.4.4 (the user's): a drag out of the Media Browser let go over the Media Browser is no drop at all (it never went anywhere).
        if (onMediaBin && fromBrowser)
            return DropRoute.None;
        // 5.4D (Q4 b): even with nothing open, a drop on the Media Browser pools its media; a blank window keeps the pool for the project
        // its first file starts.
        if (onMediaBin && !fromBrowser && PlayheadFiles(files, fromBrowser: false).Length > 0)
            return DropRoute.AddToProjectMedia;
        if ((hasClips || (hasSource && fromBrowser)) && PlayheadFiles(files, fromBrowser && hasSource).Length > 0)
            return DropRoute.AddAtPlayhead;
        if ((hasSource || holdsSomething) && !busy && IsProjectFile(files[0]))
            return DropRoute.OpenProject;
        if (!hasSource && !busy && InLineAfter(files).Length > 0)
            return DropRoute.OpenThenFollow;
        return DropRoute.OpenOrReplace;
    }

    /// <summary>
    /// The files of a drop added at the playhead (<see cref="DropRoute.AddAtPlayhead"/>): the media among them (a video, a sound or a
    /// picture HandPeg reads, MediaBrowserItem.KindOf), and from the media browser (<paramref name="fromBrowser"/>) anything but a project.
    /// </summary>
    internal static string[] PlayheadFiles(IReadOnlyList<string> files, bool fromBrowser) =>
        files.Where(f => MediaBrowserItem.KindOf(f) is not null || (fromBrowser && !IsProjectFile(f))).ToArray();

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

    /// <summary>A HandPeg project or bundle (.hproj, .hpb).</summary>
    internal static bool IsProjectFile(string path) =>
        path.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase);

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
            if (dialog.Choice == ReplaceVideoChoice.SaveAndLoad
                && !_viewModel.SaveProject(dialog.ProjectName).StartsWith("Project saved", StringComparison.Ordinal))
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

/// <summary>One target of the drop overlay: a preset to drop a file on, with the icon it is drawn with.</summary>
public sealed record DropTargetItem(string Name, string IconData, object Target)
{
    /// <summary>A card with a header: a Game Preset.</summary>
    public const string StyleIcon = "M 3,5 H 23 V 21 H 3 Z M 3,10 H 23 M 7,14 H 15 M 7,17 H 12";
}
