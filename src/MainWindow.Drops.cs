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
// A file dragged in shows the drop overlay (style presets, the Social Squisher's presets); one dropped on the
// window (or a link, or a tile from the Media pane) is routed to the timeline, the layers or the player.
public partial class MainWindow
{
    // ----- The drop overlay: targets for a file that is being dragged in -----

    private bool _dropTargetsBuilt;
    private string _dropTargetsMode = "";

    /// <summary>
    /// Makes the overlay's targets: the style presets and the Social Squisher's presets. Their background is the
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
        DropPresetTitle.Text = "Open with a style preset";
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
        // timeline, the layers or the audio tab, and nothing is put in its way.
        if (_viewModel.HasSource || _viewModel.IsBusy)
            return;

        // A file on its way to a style: what its drop will need is made ready while it is carried (on a fast drive).
        if (Dragged(e.Data).File is { } carried)
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

        // Over a style: its drop is near.
        if (sender is FrameworkElement { DataContext: DropTargetItem { Target: StyleFile } } && Dragged(e.Data).File is { } carried)
            PrewarmDrop(carried, carried: true);
    }

    private void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
    }

    // ----- Ahead of a drop (Pass 5.0-Release, the user's directive 5) -----
    // A video likely to be dropped (its Media tile rested on, a drag of it begun, carried over the drop overlay's styles):
    // on a fast local drive (DriveKind), what its drop will ask for first is done now. Its inspection (MediaProbe keeps
    // it by FileState) and its key frames (kept by the seek index); the Overview Bar's worker started and the file opened
    // in it; the player's worker opening it and decoding its first frames. A drop is urgent and never waits behind any of
    // it; when no drop follows, the player lets go of it (UnPrewarm) and the Overview Bar's worker ends as ever when idle.

    // The file made ready last, and when (Environment.TickCount64): asked again within this long, nothing is done again.
    private string? _prewarmed;
    private long _prewarmedAt;
    private bool _prewarmedCarried;
    private const int PrewarmAgainMs = 10_000;

    // The inspection asked for the file made ready last (cancelled when another is made ready, or it is let go of).
    private CancellationTokenSource? _prewarmProbe;

    // When the last drop on a style was let go (the timing line written when its Overview Bar sheet comes), and whether a
    // drop on a target is being routed (the overlay is put away for it, and what was made ready is kept).
    private System.Diagnostics.Stopwatch? _dropClock;
    private bool _routingDrop;

    /// <summary>
    /// A video likely to be dropped: made ready ahead on a fast local drive. <paramref name="carried"/>: it is being dragged
    /// (its key frames are listed too: a long recording's listing reads the whole file, worth it only for a drag).
    /// </summary>
    private void PrewarmDrop(string path, bool carried)
    {
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

        // Opened in the Overview Bar's worker (started when it is not running) and in the player's.
        if (GpuProgramStrip.IsWanted && (_viewModel.ShowTimelineThumbnails || _viewModel.ShowHoverPreviews))
            ProgramStrip().Prewarm(path);
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

    /// <summary>A file let go on a target: opened with that style, opened and given that encoding preset, or squished for that preset.</summary>
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

        // A file, or a link, which is downloaded on the way: a style is given to the video the link loads, and the
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
                _ = TimeStyleDropAsync(_viewModel.LoadFileWithStyleAsync(source, style.Path), _dropClock);
                break;

            case SquishPreset squish:
                _ = SquishDroppedAsync(source, squish);
                break;
        }
    }

    /// <summary>How long a drop on a style took to open its file with the style (the log; the Overview Bar's sheet adds its own line).</summary>
    private static async Task TimeStyleDropAsync(Task loading, System.Diagnostics.Stopwatch clock)
    {
        try
        {
            await loading;
            AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Drop on a style: opened with the style in {clock.ElapsedMilliseconds} ms."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Drop on a style", ex);
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
    // The rows of the Layers and Audio tabs are full of number boxes, and a text box answers a drag itself
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

        // A link: opened as the video, or, dropped on the timeline with a video open, downloaded and put there.
        if (link is not null)
        {
            DropLink(e, link);
            return;
        }

        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [dropped!];
        var file = files[0];

        // Dropped on the timeline, with a video open: the files become clips at the moment they were dropped on, by the
        // row they were dropped on (see FindDropPlace); on a sound's row, their sound becomes sound clips. Several at once
        // follow one another on one track, or, let go on a row's header or with Alt held, each go on a track of their
        // own at the same moment (IsStackDrop).
        if (FindDropPlace(e) is { } place)
        {
            var stack = IsStackDrop(e, place.OnHeader, files.Length);
            if (place.Row is { Kind: TimelineRowKind.Audio })
                DropLater("a drop of sound", () => AddDroppedAudioAsync(files, place.Seconds));
            else
                DropLater("a drop of clips", () => AddDroppedLayersAsync(files, place.Seconds, DropTrackOf(place.Row), stack));
            return;
        }

        // Dropped on the Audio tab (the sound's settings), with a video open: the files' sound goes onto the timeline, at
        // the playhead, as sound clips of their own.
        if (_viewModel.HasSource && AudioPanel is { IsVisible: true })
        {
            var at = e.GetPosition(AudioPanel);
            if (at.X >= 0 && at.Y >= 0 && at.X <= AudioPanel.ActualWidth && at.Y <= AudioPanel.ActualHeight)
            {
                var seconds = _viewModel.PositionMs / 1000;
                DropLater("a drop of sound", () => AddDroppedAudioAsync(files, seconds));
                return;
            }
        }

        // From the media browser, with a video or project open (4.1.1-B): the files go onto the timeline at
        // the playhead, wherever they are let go (the player, the master timeline, a pane), never over what is open.
        // A project file is still opened as one.
        if (_viewModel.HasSource && e.Data.GetDataPresent(Views.MediaBrowserPane.DragFormat)
            && files.Where(f => !f.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase)).ToArray() is { Length: > 0 } media)
        {
            var at = _viewModel.PositionMs / 1000;
            Dispatcher.BeginInvoke(() => AddFilesToTimeline(media, at));
            return;
        }

        // The dialog must not open inside the drop itself: Explorer waits, frozen, until the drop handler returns.
        Dispatcher.BeginInvoke(() => LoadDroppedFile(file));
    }

    /// <summary>
    /// A link dropped on the window: on the timeline (or the Audio tab), with a video open, it is downloaded (yt-dlp) and
    /// put there as a dropped file would be; anywhere else it is opened as the video, as if typed into the source box.
    /// </summary>
    private void DropLink(DragEventArgs e, string link)
    {
        var place = FindDropPlace(e);
        var onAudioTab = place is null && _viewModel.HasSource && AudioPanel is { IsVisible: true } panel
            && e.GetPosition(panel) is var at && at.X >= 0 && at.Y >= 0 && at.X <= panel.ActualWidth && at.Y <= panel.ActualHeight;
        if (place is null && !onAudioTab)
        {
            Dispatcher.BeginInvoke(() => LoadDroppedFile(link));
            return;
        }

        var audio = onAudioTab || place is { Row.Kind: TimelineRowKind.Audio };
        var track = place is { } on ? DropTrackOf(on.Row) : null;
        var seconds = place?.Seconds ?? _viewModel.PositionMs / 1000;
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
    private async Task AddDroppedAudioAsync(string[] files, double seconds)
    {
        using var hold = WorkScheduler.HoldBackground();
        Activate();
        var added = 0;
        foreach (var file in await Task.Run(() => files.Where(File.Exists).ToArray()))
        {
            if (await _viewModel.AddAudioLayerAsync(file, seconds))
                added++;
        }

        if (added > 1)
            _viewModel.StatusText = $"Added {added} audio clips. Each is mixed into the first audio track; drag a block to move it.";
        ScheduleClipRedraw();
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>
    /// Adds dropped files as clips: pictures as image layers, anything else as video layers. Each goes on a track of
    /// its own just above the row (or fills an empty track, or joins the row's track when Settings allow and there is
    /// room: MainViewModel.PlaceClip); on the main video's row it takes the main video's place. Several videos dropped
    /// at once follow each other on one track, end to end, from the moment dropped on (a Sequence Drop); stacked
    /// (<paramref name="stack"/>, 4.4: a Stack Drop), each goes on a track of its own just above the one before, all at that
    /// moment. Background work is held back until they are on it and filled in (4.3E: each video's probe is urgent, never
    /// behind the media browser's posters and strips), and whether the files are there is asked away from the interface.
    /// </summary>
    private async Task AddDroppedLayersAsync(string[] files, double seconds, Layer? row, bool stack = false)
    {
        using var hold = WorkScheduler.HoldBackground();
        Activate();
        if (!_viewModel.IsVideoReencoded)
        {
            _viewModel.StatusText = "Layers need the video to be encoded: choose a video encoder other than Copy on the Video tab, then drop the file again.";
            return;
        }

        var existing = await Task.Run(() => files.Where(File.Exists).ToArray());
        var (added, at, videos) = (0, seconds, existing.Count(f => !ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
        var follows = false;
        foreach (var file in existing)
        {
            if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                if (_viewModel.AddImageLayer(file, at, row))
                    added++;
                continue;
            }

            if (await _viewModel.AddVideoClipAsync(file, at, row, playOnce: videos > 1, follows: follows) is not { } clip)
                continue;

            added++;

            // Stacked, the next video goes beside this one's track, at the same moment: there is no room on its track, so
            // it gets a track of its own just above. Otherwise it goes on the same track, where this one ends.
            if (stack)
                row = clip;
            else if (clip.Duration > 0.001)
                (row, at, follows) = (clip, clip.StartTime + clip.Duration, true);
        }

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
        }

        _viewModel.LoadFile(file);
    }
}

/// <summary>One target of the drop overlay: a preset to drop a file on, with the icon it is drawn with.</summary>
public sealed record DropTargetItem(string Name, string IconData, object Target)
{
    /// <summary>A card with a header: a style.</summary>
    public const string StyleIcon = "M 3,5 H 23 V 21 H 3 Z M 3,10 H 23 M 7,14 H 15 M 7,17 H 12";


}
