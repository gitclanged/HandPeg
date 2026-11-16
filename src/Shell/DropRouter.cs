using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Controls;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Shell;

/// <summary>Where a file or link dropped on the window goes (5.4A): decided by <see cref="DropRouter.RouteOf"/>, a pure function the golden tests read.</summary>
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

/// <summary>One target of the drop overlay: a preset to drop a file on, with the icon it is drawn with.</summary>
public sealed record DropTargetItem(string Name, string IconData, object Target)
{
    /// <summary>A card with a header: a Game Preset.</summary>
    public const string StyleIcon = "M 3,5 H 23 V 21 H 3 Z M 3,10 H 23 M 7,14 H 15 M 7,17 H 12";
}

/// <summary>The window's parts the drop overlay is drawn with (MainWindow.xaml).</summary>
internal sealed record DropOverlayParts(Grid Overlay, ItemsControl PresetList, ItemsControl SquishList, TextBlock PresetTitle,
    FrameworkElement PresetSection, FrameworkElement SquishSection, UIElement Video, Action ClearDropMark);

/// <summary>
/// What a drag carries and where its drop goes: the drag's files or link (<see cref="Dragged"/>), the routing policy (<see cref="RouteOf"/>,
/// pure), the video made ready ahead of a likely drop (<see cref="PrewarmDrop"/>), and the drop overlay of an empty window (Game Presets and
/// the Social Squisher's presets) shown while a file is carried in. The window runs the route (it adds clips, opens files and tabs).
/// </summary>
/// <remarks>The window's thread only. The overlay's targets are made at the first idle moment after Frame 1 (<see cref="PrepareDropTargets"/>).</remarks>
internal sealed class DropRouter
{
    // A file dragged in holds background work back (WorkScheduler.HoldBackground, 4.3E) until a moment after its last move
    // (each move holds it again: a drag from another program has no end HandPeg is told of for sure), and a drop holds it
    // a moment more: until its own work, which holds it for as long as it takes, has begun (clips put on the timeline), or
    // while a video it opens is probed (its own I-frames, waveforms and proxy are background work, and are not held long).
    internal static readonly TimeSpan DragHold = TimeSpan.FromMilliseconds(500), DropHold = TimeSpan.FromMilliseconds(400);

    // A drag "leaves" every element it passes out of, on its way to the next one inside the window, and where
    // the pointer is said to be at that moment cannot be relied on at the window's edge. So leaving only
    // starts a short wait: the drag is still in the window if it is heard from again before the wait is out.
    private const int LeaveWaitMs = 140;

    // The file made ready last, and when (Environment.TickCount64): asked again within this long, nothing is done again.
    private const int PrewarmAgainMs = 10_000;

    private readonly MainViewModel _viewModel;
    private readonly Window _window;
    private readonly DropOverlayParts _parts;
    private readonly Func<GpuPreviewEngine?> _engine;
    private readonly TimerScope _timers = new();
    private readonly Debounce _leave;
    private bool _dropTargetsBuilt;
    private string _dropTargetsMode = "";

    // The last drag asked about, and what it carries: a drag is asked about at every move.
    private IDataObject? _draggedData;
    private (string? File, string? Link) _dragged;

    // Ahead of a drop (5.0-Release): the file made ready last, when, whether it was carried, and the inspection asked for it
    // (cancelled when another is made ready, or it is let go of).
    private string? _prewarmed;
    private long _prewarmedAt;
    private bool _prewarmedCarried;
    private CancellationTokenSource? _prewarmProbe;

    // Whether a drop on a target is being routed (the overlay is put away for it, and what was made ready is kept).
    private bool _routingDrop;

    public DropRouter(MainViewModel viewModel, Window window, DropOverlayParts parts, Func<GpuPreviewEngine?> engine)
    {
        (_viewModel, _window, _parts, _engine) = (viewModel, window, parts, engine);
        _leave = new Debounce(TimeSpan.FromMilliseconds(LeaveWaitMs), HideDropOverlay, DispatcherPriority.Background, _timers);
    }

    // ----- What a drag carries -----

    /// <summary>
    /// What a drag carries that HandPeg can open: files (from Explorer, or the media browser), or a link (from a web
    /// browser's address bar or page: any address yt-dlp may download, not only YouTube's).
    /// </summary>
    public (string? File, string? Link) Dragged(IDataObject data)
    {
        if (ReferenceEquals(data, _draggedData))
            return _dragged;

        _draggedData = data;
        _dragged = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            ? (files[0], null)
            : (null, DraggedLink(data));
        return _dragged;
    }

    /// <summary>Whether a drag carries something HandPeg can open (a file or a link).</summary>
    public bool CanOpen(IDataObject data) => Dragged(data) is { File: not null } or { Link: not null };

    /// <summary>A drag that began in HandPeg's own Media Browser or Clip Library (MediaBrowserPane.DragFormat), not in Explorer.</summary>
    public static bool IsInternalDrag(IDataObject data) => data.GetDataPresent(Views.MediaBrowserPane.DragFormat);

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

    // ----- The routing policy -----

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
    /// picture HandPeg reads, MediaFiles.KindOf), and from the media browser (<paramref name="fromBrowser"/>) anything but a project.
    /// </summary>
    internal static string[] PlayheadFiles(IReadOnlyList<string> files, bool fromBrowser) =>
        files.Where(f => MediaFiles.KindOf(f) is not null || (fromBrowser && !IsProjectFile(f))).ToArray();

    /// <summary>A HandPeg project or bundle (.hproj, .hpb).</summary>
    internal static bool IsProjectFile(string path) =>
        path.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The pictures a drag carries after its first file: what follows it in line when it is opened. Sounds and projects are no such thing.</summary>
    internal static string[] InLineAfterFirst(IDataObject data) => data.GetData(DataFormats.FileDrop) is string[] files ? InLineAfter(files) : [];

    /// <summary>The same of a drop's files: those after the first that are neither a sound nor a project.</summary>
    internal static string[] InLineAfter(IReadOnlyList<string> files) =>
        files.Count > 1
            ? files.Skip(1).Where(f => !f.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase) && MediaFiles.KindOf(f) != MediaBrowserKind.Audio).ToArray()
            : [];

    // ----- Ahead of a drop (5.0-Release) -----
    // A video likely to be dropped (its Media tile rested on, a drag of it begun, carried over the drop overlay's Game Presets):
    // on a fast local drive (DriveKind), what its drop will ask for first is done now. Its inspection (MediaProbe keeps
    // it by FileState) and its key frames (kept by the seek index); the player's worker opening it and decoding its first frames.
    // A drop is urgent and never waits behind any of it; when no drop follows, the player lets go of it (UnPrewarm).

    /// <summary>
    /// A video likely to be dropped: made ready ahead on a fast local drive. <paramref name="carried"/>: it is being dragged
    /// (its key frames are listed too: a long recording's listing reads the whole file, worth it only for a drag).
    /// </summary>
    public void PrewarmDrop(string path, bool carried)
    {
        GpuNativeBreadcrumbs.Mark("PrewarmDrop", (carried ? "carried " : "") + Path.GetFileName(path));
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
        _engine()?.Prewarm(path);
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
    public void UnPrewarm()
    {
        _engine()?.Prewarm(null);
        _prewarmed = null;
        _prewarmProbe?.Cancel();
        _prewarmProbe = null;
    }

    // ----- The drop overlay: targets for a file that is being dragged in -----

    /// <summary>Whether the overlay is up (a file carried into an empty window).</summary>
    public bool OverlayShown => _parts.Overlay.Visibility == Visibility.Visible;

    /// <summary>
    /// Makes the overlay's targets ahead of time, once the window is up and idle, and lays the overlay out while it
    /// is still hidden (Hidden, not Collapsed, keeps it laid out), so that the first drag shows it at once.
    /// </summary>
    public void PrepareDropTargets()
    {
        BuildDropTargets();
        if (_parts.Overlay.Visibility == Visibility.Collapsed)
            _parts.Overlay.Visibility = Visibility.Hidden;
    }

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
        (_parts.PresetList.ItemsSource, _parts.SquishList.ItemsSource) = (presets, squish);
        _parts.PresetTitle.Text = "Open with a Game Preset";
        _parts.PresetSection.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _parts.SquishSection.Visibility = squish.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var accent = (_window.FindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.SteelBlue;
        var grey = (byte)((accent.R + accent.G + accent.B) / 3);
        byte Mute(byte channel) => (byte)(channel * 0.55 + grey * 0.30 + 20);
        _window.Resources["DropTargetBrush"] = UiBrushes.Frozen(Mute(accent.R), Mute(accent.G), Mute(accent.B));
        (_dropTargetsBuilt, _dropTargetsMode) = (true, DropTargetsSignature());
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
            (_window.FindResource("AccentBrush") as SolidColorBrush)?.Color.ToString() ?? "");
    }

    /// <summary>A drag entered or moved over the window: the overlay up, into an empty window only.</summary>
    public void ShowDropOverlay(DragEventArgs e)
    {
        _leave.Cancel();
        if (OverlayShown || !CanOpen(e.Data))
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
        _parts.Overlay.Visibility = Visibility.Visible;

        // The player is a native window, which WPF cannot draw over: it would stay bright in the middle of the
        // dimmed window. It is hidden for as long as the overlay is up.
        _parts.Video.Visibility = Visibility.Hidden;
    }

    /// <summary>A file that leaves the window takes the overlay with it, a moment later (see LeaveWaitMs).</summary>
    public void LeaveDropOverlay() => _leave.Poke();

    /// <summary>The overlay put away: the drag left, a drop was let go, or a target is being routed.</summary>
    public void HideDropOverlay()
    {
        // Carried away, not dropped on a target: the player lets go of what it opened for it.
        if (!_routingDrop)
            UnPrewarm();
        _leave.Cancel();
        _parts.ClearDropMark();
        _parts.Overlay.Visibility = _dropTargetsBuilt ? Visibility.Hidden : Visibility.Collapsed;
        _parts.Video.Visibility = Visibility.Visible;
    }

    /// <summary>The overlay put away for a drop on one of its targets, keeping what was made ready for the drop.</summary>
    public void HideForTarget()
    {
        _routingDrop = true;
        try
        {
            HideDropOverlay();
        }
        finally
        {
            _routingDrop = false;
        }
    }
}
