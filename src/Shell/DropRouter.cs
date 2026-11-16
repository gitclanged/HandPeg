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

/// <summary>Destination of a dropped file or link, decided by the pure <see cref="DropRouter.RouteOf"/> (golden-tested).</summary>
internal enum DropRoute
{
    /// <summary>Nothing openable.</summary>
    None,

    /// <summary>Onto the timeline at the drop point (FindDropPlace: row and time).</summary>
    TimelinePlace,

    /// <summary>Audio clips at the playhead (dropped on the Audio tab).</summary>
    AudioTab,

    /// <summary>Clips at the playhead, like the Media pane's Add (AddFilesToTimeline).</summary>
    AddAtPlayhead,

    /// <summary>Nothing open: open the first file and append following images after it.</summary>
    OpenThenFollow,

    /// <summary>Open as the source (LoadDroppedFile), directly or after the replace prompt.</summary>
    OpenOrReplace,

    /// <summary>Open a project in a new tab (OpenProjectInTabAsync), never replacing the current one.</summary>
    OpenProject,

    /// <summary>Into Project Media (TimelineDocument.MediaPool) via the Media Browser pane; nothing on the timeline.</summary>
    AddToProjectMedia,
}

/// <summary>Drop overlay target: a preset and its icon.</summary>
public sealed record DropTargetItem(string Name, string IconData, object Target)
{
    /// <summary>A card with a header: a Game Preset.</summary>
    public const string StyleIcon = "M 3,5 H 23 V 21 H 3 Z M 3,10 H 23 M 7,14 H 15 M 7,17 H 12";
}

/// <summary>MainWindow.xaml elements used by the drop overlay.</summary>
internal sealed record DropOverlayParts(Grid Overlay, ItemsControl PresetList, ItemsControl SquishList, TextBlock PresetTitle,
    FrameworkElement PresetSection, FrameworkElement SquishSection, UIElement Video, Action ClearDropMark);

/// <summary>
/// Drag-and-drop routing: payload extraction (<see cref="Dragged"/>), pure policy (<see cref="RouteOf"/>), pre-warming a likely drop
/// (<see cref="PrewarmDrop"/>), and the empty-window overlay (Game Presets, Social Squisher presets). The window executes routes.
/// </summary>
/// <remarks>UI thread only. Overlay targets are built at the first idle after Frame 1 (<see cref="PrepareDropTargets"/>).</remarks>
internal sealed class DropRouter
{
    // A drag holds background work (WorkScheduler.HoldBackground), renewed on every move since cross-process drags have no reliable
    // end event. A drop extends the hold until its own work starts (clip placement) or its probe finishes; the opened file's I-frames,
    // waveforms and proxy are background work and are not held.
    internal static readonly TimeSpan DragHold = TimeSpan.FromMilliseconds(500), DropHold = TimeSpan.FromMilliseconds(400);

    // DragLeave fires per element and pointer positions are unreliable at the window edge, so a leave only starts a short timer;
    // any further drag event before it expires means the drag is still inside.
    private const int LeaveWaitMs = 140;

    // Pre-warm dedupe window: repeat requests for the same file within this many ms are ignored.
    private const int PrewarmAgainMs = 10_000;

    private readonly MainViewModel _viewModel;
    private readonly Window _window;
    private readonly DropOverlayParts _parts;
    private readonly Func<GpuPreviewEngine?> _engine;
    private readonly TimerScope _timers = new();
    private readonly Debounce _leave;
    private bool _dropTargetsBuilt;
    private string _dropTargetsMode = "";

    // Cache of the last queried drag and its payload (queried on every move).
    private IDataObject? _draggedData;
    private (string? File, string? Link) _dragged;

    // Pre-warm state: last file, timestamp, whether dragged, and its probe (cancelled when superseded or released).
    private string? _prewarmed;
    private long _prewarmedAt;
    private bool _prewarmedCarried;
    private CancellationTokenSource? _prewarmProbe;

    // Set while routing a drop on an overlay target (overlay hidden, pre-warm kept).
    private bool _routingDrop;

    public DropRouter(MainViewModel viewModel, Window window, DropOverlayParts parts, Func<GpuPreviewEngine?> engine)
    {
        (_viewModel, _window, _parts, _engine) = (viewModel, window, parts, engine);
        _leave = new Debounce(TimeSpan.FromMilliseconds(LeaveWaitMs), HideDropOverlay, DispatcherPriority.Background, _timers);
    }

    // ----- What a drag carries -----

    /// <summary>Openable drag payload: files (Explorer or Media Browser) or a web link (any yt-dlp-supported URL).</summary>
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

    /// <summary>Whether a drag carries an openable file or link.</summary>
    public bool CanOpen(IDataObject data) => Dragged(data) is { File: not null } or { Link: not null };

    /// <summary>Whether the drag originated in the Media Browser or Clip Library (MediaBrowserPane.DragFormat).</summary>
    public static bool IsInternalDrag(IDataObject data) => data.GetDataPresent(Views.MediaBrowserPane.DragFormat);

    /// <summary>Web URL from any browser drag format; null when none.</summary>
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

            // URL is the first line (Firefox appends the page title on the next).
            var first = text?.Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (first is not null && MainViewModel.IsWebAddress(first))
                return first;
        }

        return null;
    }

    // ----- The routing policy -----

    /// <summary>
    /// Drop routing policy (once the Sequence has clips, media is added, never opened over it). Precedence: timeline drop point; Audio
    /// tab -> audio at playhead; Media Browser pane -> Project Media (links and browser-origin drags fall through); clips present ->
    /// media at playhead (links after download; browser drags with a video open: all but projects); project with content open or held
    /// -> new tab; nothing open -> open files in sequence; otherwise open as source (replace prompt if one is open).
    /// </summary>
    /// <param name="hasClips">The Sequence has at least one clip.</param>
    /// <param name="hasSource">A source or project is open (MainViewModel.HasSource).</param>
    /// <param name="busy">An operation is running (MainViewModel.IsBusy).</param>
    /// <param name="onTimeline">Dropped over timeline rows (FindDropPlace succeeded).</param>
    /// <param name="onAudioTab">Dropped over the Audio tab with a source open.</param>
    /// <param name="fromBrowser">Dragged from the Media Browser.</param>
    /// <param name="isLink">A web URL rather than files.</param>
    /// <param name="files">Dropped files (empty for a link).</param>
    /// <param name="onMediaBin">Dropped over the Media Browser pane.</param>
    /// <param name="holdsSomething">
    /// The active tab holds content without a source (!MainViewModel.HoldsNothingForTab, e.g. pooled media): a dropped project then
    /// opens in a new tab like File &gt; Open, never replacing it.
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
        // A Media Browser drag dropped back on the Media Browser is a no-op.
        if (onMediaBin && fromBrowser)
            return DropRoute.None;
        // Even with nothing open, a Media Browser drop pools its media; the pool carries into the project the first file starts.
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
    /// Files added by <see cref="DropRoute.AddAtPlayhead"/>: readable media (MediaFiles.KindOf), or for browser drags
    /// (<paramref name="fromBrowser"/>) anything but projects.
    /// </summary>
    internal static string[] PlayheadFiles(IReadOnlyList<string> files, bool fromBrowser) =>
        files.Where(f => MediaFiles.KindOf(f) is not null || (fromBrowser && !IsProjectFile(f))).ToArray();

    /// <summary>Whether a path is a project or bundle (.hproj, .hpb).</summary>
    internal static bool IsProjectFile(string path) =>
        path.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Files after the first that are appended when it opens (excludes audio and projects).</summary>
    internal static string[] InLineAfterFirst(IDataObject data) => data.GetData(DataFormats.FileDrop) is string[] files ? InLineAfter(files) : [];

    /// <summary>Same for a file list: entries after the first that are neither audio nor projects.</summary>
    internal static string[] InLineAfter(IReadOnlyList<string> files) =>
        files.Count > 1
            ? files.Skip(1).Where(f => !f.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase) && MediaFiles.KindOf(f) != MediaBrowserKind.Audio).ToArray()
            : [];

    // ----- Drop pre-warm -----
    // For a likely drop (tile hover, drag start, drag over Game Presets) on a fast local drive (DriveKind): probe (cached by FileState),
    // key frames (seek index) and player worker open + first-frame decode. Real drops never wait on this; UnPrewarm releases it otherwise.

    /// <summary>
    /// Pre-warms a likely drop on a fast local drive. With <paramref name="carried"/> (an active drag) also lists key frames, which
    /// reads whole long files and is only worth it for a drag.
    /// </summary>
    public void PrewarmDrop(string path, bool carried)
    {
        GpuNativeBreadcrumbs.Mark("PrewarmDrop", (carried ? "carried " : "") + Path.GetFileName(path));
        if (string.IsNullOrWhiteSpace(path) || MainViewModel.IsWebAddress(path))
            return;

        // Drive speed is classified off the UI thread once per drive; skip until known.
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

        // Probe as background work (held during drags, never ahead of interactive loads; the drop's urgent probe covers it),
        // cancelling the previous pre-warm's probe so hovered tiles do not queue probes ahead of the drop.
        if (!again || _prewarmProbe is null)
        {
            _prewarmProbe?.Cancel();
            _prewarmProbe = new CancellationTokenSource();
            _ = MediaProbe.ProbeAsync(path, _prewarmProbe.Token, WorkLane.Background);
        }

        // Key frames for drags, off the UI thread (disk cache read first).
        if (carried)
            _ = Task.Run(() => FfmpegRunner.GetIFramesAsync(path, CancellationToken.None));

        // Open in the player worker only. Not GpuProgramStrip: starting it during a drag ran it on a secondary Intel Arc GPU and caused
        // 0x116 VIDEO_TDR_FAILURE; it opens the file after the drop as usual.
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

    /// <summary>Releases the pre-warmed file when no drop followed (drag left, or something else opened).</summary>
    public void UnPrewarm()
    {
        _engine()?.Prewarm(null);
        _prewarmed = null;
        _prewarmProbe?.Cancel();
        _prewarmProbe = null;
    }

    // ----- The drop overlay: targets for a file that is being dragged in -----

    /// <summary>Whether the overlay is shown (file dragged into an empty window).</summary>
    public bool OverlayShown => _parts.Overlay.Visibility == Visibility.Visible;

    /// <summary>
    /// Pre-builds overlay targets at idle and lays out the overlay while Hidden (not Collapsed, so layout runs) so the first drag
    /// shows instantly.
    /// </summary>
    public void PrepareDropTargets()
    {
        BuildDropTargets();
        if (_parts.Overlay.Visibility == Visibility.Collapsed)
            _parts.Overlay.Visibility = Visibility.Hidden;
    }

    /// <summary>Builds overlay targets (Game Presets, Social Squisher presets) on a muted accent background.</summary>
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

    /// <summary>Overlay target inputs signature; a change triggers a rebuild.</summary>
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

    /// <summary>DragEnter/DragOver: shows the overlay, only for an empty window.</summary>
    public void ShowDropOverlay(DragEventArgs e)
    {
        _leave.Cancel();
        if (OverlayShown || !CanOpen(e.Data))
            return;

        // Empty window only: with content open the drag targets the timeline, Canvas or Audio tab, so nothing overlays them.
        if (_viewModel.HasSource || _viewModel.IsBusy)
            return;

        // Pre-warm external drags heading for a Game Preset (fast drives only); internal Media Browser/Clip Library drags target the timeline.
        if (Dragged(e.Data).File is { } carried && !IsInternalDrag(e.Data))
            PrewarmDrop(carried, carried: true);

        // Built at launch idle (PrepareDropTargets); rebuilt only if the mode or presets changed.
        if (DropTargetsSignature() != _dropTargetsMode)
            BuildDropTargets();
        _parts.Overlay.Visibility = Visibility.Visible;

        // The player is a native HWND (airspace): WPF cannot draw over it, so hide it while the overlay is up.
        _parts.Video.Visibility = Visibility.Hidden;
    }

    /// <summary>DragLeave: hides the overlay after LeaveWaitMs unless the drag returns.</summary>
    public void LeaveDropOverlay() => _leave.Poke();

    /// <summary>Hides the overlay (drag left, dropped, or a target is being routed).</summary>
    public void HideDropOverlay()
    {
        // Not dropped on a target: release the pre-warmed file.
        if (!_routingDrop)
            UnPrewarm();
        _leave.Cancel();
        _parts.ClearDropMark();
        _parts.Overlay.Visibility = _dropTargetsBuilt ? Visibility.Hidden : Visibility.Collapsed;
        _parts.Video.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the overlay for a target drop, keeping the pre-warmed file.</summary>
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
