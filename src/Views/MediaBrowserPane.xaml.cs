using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;
using Microsoft.Win32;

namespace HandPegApp.Views;

/// <summary>
/// Media browser pane: lists a folder's video, audio and images for dragging onto the timeline; hovering a video tile scrubs it.
/// Per-video frame strips (Settings: Preview &amp; Cache) are generated in the background one file at a time and cached, so revisits
/// are instant. At rest only visible tiles (plus one row each side) hold images; with the pointer inside, off-screen tiles fill
/// within a byte budget until a minute after it leaves. Only recently hovered strips stay decoded.
/// </summary>
public partial class MediaBrowserPane : UserControl
{
    // Tile Size (0..100) threshold: below it the List view is used; above it tiles scale from minimum to maximum width.
    internal const double ListBelow = 8;
    private const double SmallestTile = 96, LargestTile = 420;

    // Horizontal chrome around a tile image: template margin plus item border and padding.
    private const double TileChrome = 22;

    private static readonly string[] SortKeys = ["Name", "Date", "Size", "Type"];

    // Listing cap per folder; larger folders are truncated with a notice.
    private const int MostEntries = 3000;

    // Image byte budget: above it, off-screen tiles release images (rebuilt from cache when visible). Bytes, not a count: the largest
    // poster is ~20x the smallest.
    private const long PosterBudgetBytes = 32L * 1024 * 1024;

    // Max decoded strips kept (most recently hovered tiles, LRU in _strips).
    private const int StripsKept = 6;

    // Poll interval while background work is held for a drag-and-drop.
    private const int HeldPollMilliseconds = 100;

    private readonly ObservableCollection<MediaBrowserItem> _items = [];
    private readonly DispatcherTimer _visibleCheck = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _sizeSettled = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private double _tileWidth = 200;

    // Displayed tile image width: the Tile Size width as stretched by the panel to fill the pane.
    private double _shownTileWidth;
    private readonly List<MediaBrowserItem> _queue = [];

    // Second pass: poster-only videos awaiting their strip (Background lane), and those being stripped now because hovered (Interactive).
    private readonly List<MediaBrowserItem> _stripsWanted = [];
    private readonly HashSet<MediaBrowserItem> _stripsOnDemand = [];
    private readonly LinkedList<(MediaBrowserItem Item, IReadOnlyList<BitmapSource> Frames)> _strips = new();
    private CancellationTokenSource _listing = new();
    private bool _working, _started;
    private MediaBrowserItem? _decoding;
    private string? _folder;

    // List press state: point, item, and whether a multi-selection was preserved for dragging.
    private Point? _pressAt;
    private MediaBrowserItem? _pressed;
    private ModifierKeys _pressKeys;
    private bool _keepSelection;

    public MediaBrowserPane() : this(pickerMode: false)
    {
    }

    /// <param name="pickerMode">
    /// Creates the New Project studio's media picker (<see cref="PickerMode"/>) instead of the docked Media pane.
    /// </param>
    public MediaBrowserPane(bool pickerMode)
    {
        PickerMode = pickerMode;

        // The XAML registers the Media pane's dock title slot; a picker must unregister it.
        var mediaSlot = pickerMode ? Docking.PaneTitleSlot.Of("Media") : null;
        InitializeComponent();
        if (pickerMode)
            Docking.PaneTitleSlot.Restore("Media", mediaSlot);

        Items.ItemsSource = _items;
        _visibleCheck.Tick += (_, _) =>
        {
            _visibleCheck.Stop();
            QueueVisible();
        };
        Items.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        {
            ScheduleVisibleCheck();

            // Scrolling counts as activity: restart the off-screen release timer.
            if (!IsMouseOver)
                RestartRest();
        }));
        MouseEnter += Pane_MouseEnter;
        MouseLeave += Pane_MouseLeave;
        _rest.Tick += Rest_Tick;
        Items.SizeChanged += (_, _) => ScheduleVisibleCheck();
        Items.AddHandler(VirtualizingTilePanel.CellWidthChangedEvent, new RoutedEventHandler(TilesStretched));
        _sizeSettled.Tick += (_, _) =>
        {
            _sizeSettled.Stop();
            SharpenPictures();
        };

        var settings = AppSettings.Current;
        InitializeSort(settings);
        SortDirectionButton.IsChecked = settings.BrowserSortDescending;
        InitializeView(settings);

        // Fast wheel input coasts the list (MomentumScroller); O(1) here, the scroller is created on first wheel.
        ScrollMomentum.Attach(Items);
        if (!pickerMode)
            Items.SelectionChanged += Items_SelectionChanged;

        _libraryMode = settings.BrowserShowsLibrary;
        _projectMode = !_libraryMode && settings.BrowserShowsProject && !pickerMode;
        (_libraryMode ? LibrarySide : _projectMode ? ProjectSide : FolderSide).IsChecked = true;
        if (pickerMode)
            StartPicking();
        ShowBars();
        _projectSettled.Tick += (_, _) =>
        {
            _projectSettled.Stop();
            if (_projectMode && _started && IsVisible)
                _ = ListProjectAsync(always: false);
        };
        _searchSettled.Tick += (_, _) =>
        {
            _searchSettled.Stop();
            if (_libraryMode && _started)
                _ = ListLibraryAsync();
        };
    }

    // ----- Picker mode (New Project studio) -----
    // The pane hosted in a dialog to choose a New Project's files: Folder Browser or Clip Library (no Project Media yet). Click, double-click
    // or Enter toggles a file; choices persist across folders in selection order. Never writes Media pane settings, never takes the dock
    // title slot, never drags out, and frees its resources when the dialog closes (Release).

    /// <summary>True for the New Project picker instance (set by the constructor).</summary>
    public bool PickerMode { get; }

    // Chosen paths in selection order (case-insensitive).
    private readonly List<string> _picked = [];

    /// <summary>Picker selection in order, across all folders visited.</summary>
    public IReadOnlyList<string> SelectedFiles => _picked;

    /// <summary>Raised when the picker selection changes.</summary>
    public event Action? SelectedFilesChanged;

    private void StartPicking()
    {
        ProjectSide.Visibility = Visibility.Collapsed;
        Items.SelectionMode = SelectionMode.Multiple;
        Items.SelectionChanged += Picker_SelectionChanged;

        // Re-select chosen files when their folder is listed again.
        _items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is { Count: > 0 } added && _picked.Count > 0)
                Dispatcher.BeginInvoke(() => SelectPicked(added.OfType<MediaBrowserItem>()), DispatcherPriority.Background);
        };
    }

    private bool _selectingPicked;

    private void SelectPicked(IEnumerable<MediaBrowserItem> items)
    {
        _selectingPicked = true;
        try
        {
            foreach (var item in items)
            {
                if (item.IsFile && _items.Contains(item) && !Items.SelectedItems.Contains(item) && _picked.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
                    Items.SelectedItems.Add(item);
            }
        }
        finally
        {
            _selectingPicked = false;
        }
    }

    private void Picker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingPicked)
            return;

        var changed = false;
        foreach (var item in e.AddedItems.OfType<MediaBrowserItem>())
        {
            if (!item.IsFile)
            {
                // Folders navigate; they are never chosen.
                Items.SelectedItems.Remove(item);
                continue;
            }

            if (!_picked.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            {
                _picked.Add(item.Path);
                changed = true;
            }
        }

        // Deselect only files the user unselected while listed; files dropped by a folder change stay chosen.
        foreach (var item in e.RemovedItems.OfType<MediaBrowserItem>())
        {
            if (item.IsFile && _items.Contains(item) && _picked.RemoveAll(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase)) > 0)
                changed = true;
        }

        if (changed)
            SelectedFilesChanged?.Invoke();
    }

    /// <summary>Toggles a file's picker selection (double-click, Enter).</summary>
    private void TogglePick(MediaBrowserItem item)
    {
        if (Items.SelectedItems.Contains(item))
            Items.SelectedItems.Remove(item);
        else
            Items.SelectedItems.Add(item);
    }

    /// <summary>Clears the picker selection.</summary>
    public void ClearSelectedFiles()
    {
        _picked.Clear();
        _selectingPicked = true;
        try
        {
            Items.SelectedItems.Clear();
        }
        finally
        {
            _selectingPicked = false;
        }

        SelectedFilesChanged?.Invoke();
    }

    /// <summary>
    /// Picker teardown on dialog close: cancels listing and indexing, stops all timers (a running timer roots the pane), releases images
    /// and strips, and requests a quiet-time memory compaction.
    /// </summary>
    public void Release()
    {
        _listing.Cancel();
        StopIndexing();
        StopStripOnDemand();
        StopAutoScroll();
        foreach (var timer in (DispatcherTimer?[])[_visibleCheck, _sizeSettled, _rest, _projectSettled, _searchSettled, _restTimer, _indexDwell])
            timer?.Stop();

        (_prefetching, _restingOn) = (false, null);
        ReleasePreview();
        _detailsWanted.Clear();
        _queue.Clear();
        _stripsWanted.Clear();
        _strips.Clear();
        _inSight.Clear();
        foreach (var item in _items)
            (item.Poster, item.HoverFrame, item.PreviewRequested) = (null, null, false);
        _items.Clear();
        GiveBackMemory();
    }

    // ----- Modes: Project Media, Folder Browser, Clip Library -----
    // Project Media lists every file the project uses (ProjectFiles), without a folder bar; Folder Browser shows a disk folder
    // (last folder remembered); Clip Library lists saved clips (ClipLibrary) under a search box.

    private bool _projectMode, _libraryMode;
    private readonly DispatcherTimer _searchSettled = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private bool FolderMode => !_projectMode && !_libraryMode;

    private void ShowBars()
    {
        FolderBar.Visibility = FolderMode ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = _libraryMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Relists the current mode (the start folder on first use).</summary>
    private Task ListAgain(bool fromStart = false) =>
        _libraryMode ? ListLibraryAsync() : _projectMode ? ListProjectAsync() : _folder is null || fromStart ? NavigateAsync(null, fromStart: true) : NavigateAsync(_folder);

    /// <summary>Clip Library changed: relist if it is showing.</summary>
    public void LibraryChanged()
    {
        if (_libraryMode && _started)
            _ = ListLibraryAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchSettled.Stop();
        _searchSettled.Start();
    }

    /// <summary>Lists Clip Library clips matching the search, sorted like folder files.</summary>
    private async Task ListLibraryAsync()
    {
        StopIndexing();
        _listing.Cancel();
        _listing = new CancellationTokenSource();
        var token = _listing.Token;
        _queue.Clear();
        _detailsWanted.Clear();
        _stripsWanted.Clear();
        _strips.Clear();
        _items.Clear();
        _projectListed = null;

        var search = SearchBox.Text.Trim();
        List<MediaBrowserItem> entries;
        try
        {
            entries = await Task.Run(() => ClipLibrary.Load().Where(clip => clip.Matches(search)).Select(clip =>
            {
                var file = new FileInfo(clip.Path);
                var tags = clip.Tags.Count > 0 ? " \u00B7 " + string.Join(", ", clip.Tags) : "";
                return new MediaBrowserItem(file.FullName, MediaFiles.KindOf(file.Name) ?? MediaBrowserKind.Video, clip.Title)
                {
                    Info = MediaBrowserItem.FormatSeconds(clip.Duration) + tags, Bytes = file.Length, Modified = clip.Saved,
                };
            }).ToList(), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            Controls.UiFault.ShowOnce(StatusText, $"clip-library|{ex.HResult}", $"The Clip Library could not be read: {ex.Message}", "Media browser: the Clip Library", ex);
            return;
        }

        if (token.IsCancellationRequested || !await AddInTurnsAsync(Sorted(entries), token))
            return;

        StatusText.Text = entries.Count > 0
            ? $"{entries.Count} clip{(entries.Count == 1 ? "" : "s")} in your Clip Library{(search.Length > 0 ? $" for \"{search}\"" : "")}. Drag them onto the timeline of any project."
            : search.Length > 0
                ? $"No clip in your Clip Library has \"{search}\" in its title or tags."
                : "Your Clip Library is empty: a clip's menu, Save Clip to Personal Clip Library..., keeps one here.";
        if (_items.Count > 0)
            Items.ScrollIntoView(_items[0]);
        ScheduleVisibleCheck();
    }
    private readonly DispatcherTimer _projectSettled = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>Project Media files (the project bin, MainViewModel.ProjectMediaFiles) in insertion order.</summary>
    public Func<IReadOnlyList<string>>? ProjectFiles { get; set; }

    /// <summary>
    /// Project files may have changed (clip or bin edits, another project): debounced relist of Project Media, only if the file set differs
    /// (timeline edits touching no files leave list, selection and preview untouched).
    /// </summary>
    public void ProjectChanged()
    {
        if (!_projectMode)
            return;

        _projectSettled.Stop();
        _projectSettled.Start();
    }

    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        var (project, library) = (ReferenceEquals(sender, ProjectSide), ReferenceEquals(sender, LibrarySide));
        if (!IsInitialized || (project, library) == (_projectMode, _libraryMode))
            return;

        (_projectMode, _libraryMode) = (project, library);
        if (!PickerMode)
            (AppSettings.Current.BrowserShowsProject, AppSettings.Current.BrowserShowsLibrary) = (project, library);
        ShowBars();
        if (_started)
            _ = ListAgain();
    }

    // Last Project Media file set (null after another mode's listing); used to skip no-op relists.
    private List<string>? _projectListed;

    /// <summary>Lists Project Media files sorted like a folder, flagging missing files; preserves selection.</summary>
    private async Task ListProjectAsync(bool always = true)
    {
        var files = ProjectFiles?.Invoke() ?? [];
        if (!always && _projectListed is { } listed && listed.SequenceEqual(files, StringComparer.OrdinalIgnoreCase))
            return;

        var chosen = Selected().Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        StopIndexing();
        _listing.Cancel();
        _listing = new CancellationTokenSource();
        var token = _listing.Token;
        _queue.Clear();
        _detailsWanted.Clear();
        _stripsWanted.Clear();
        _strips.Clear();
        _relistingFor = token;
        _items.Clear();
        _projectListed = [.. files];

        List<MediaBrowserItem> entries;
        try
        {
            entries = await Task.Run(() => files.Select(path =>
            {
                var file = new FileInfo(path);
                var kind = MediaFiles.KindOf(file.Name) ?? MediaBrowserKind.Video;
                return file.Exists
                    ? new MediaBrowserItem(file.FullName, kind, file.Name) { Info = MediaBrowserItem.FormatSize(file.Length), Bytes = file.Length, Modified = file.LastWriteTime }
                    : new MediaBrowserItem(path, kind, file.Name) { Info = "missing" };
            }).ToList(), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            Controls.UiFault.ShowOnce(StatusText, $"project-files|{ex.HResult}", $"The project's files could not be read: {ex.Message}", "Media browser: the project's files", ex);
            _relistingFor = null;
            return;
        }

        if (token.IsCancellationRequested || !await AddInTurnsAsync(Sorted(entries), token))
            return;

        StatusText.Text = entries.Count == 0
            ? "Project Media is empty: drop files here, or onto the timeline, to put them in the project."
            : $"{entries.Count} file{(entries.Count == 1 ? "" : "s")} in Project Media. Drag them onto the timeline; drop more here to keep them in the project.";
        var again = _items.Where(i => chosen.Contains(i.Path)).ToList();
        if (again.Count > 0)
        {
            foreach (var item in again)
                Items.SelectedItems.Add(item);
            Items.ScrollIntoView(again[0]);
        }
        else if (_items.Count > 0)
        {
            Items.ScrollIntoView(_items[0]);
        }

        if (_relistingFor == token)
            _relistingFor = null;
        ScheduleVisibleCheck();
    }

    // True during a Project Media relist: the cutter keeps its file, In, Out and playhead (ShowChosen) instead of resetting as the list
    // empties and refills.
    private CancellationToken? _relistingFor;

    private bool Relisting => _relistingFor is { IsCancellationRequested: false };

    /// <summary>
    /// Folder Browser start folder: remembered, else the loaded video's, else Videos. Probes the disk, so never on the UI thread (an
    /// offline network folder can block for seconds, and the pane first shows during window layout).
    /// </summary>
    private static string StartFolder(string remembered, string? suggested) =>
        Directory.Exists(remembered) ? remembered
        : suggested is not null && Directory.Exists(suggested) ? suggested
        : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>Request to open a file (replacing the open video, if any).</summary>
    public event Action<string>? LoadRequested;

    /// <summary>
    /// A video will likely be dropped soon: hovered for <see cref="PrewarmRestMs"/> (false) or drag started (true). The host may pre-warm
    /// what the drop needs (fast local drives only, MainWindow.Drops).
    /// </summary>
    public event Action<string, bool>? PrewarmRequested;

    /// <summary>Hover dwell in ms before a tile triggers <see cref="PrewarmRequested"/>.</summary>
    public const int PrewarmRestMs = 120;

    // Hovered tile and its dwell timer (created lazily on first hover).
    private MediaBrowserItem? _restingOn;
    private DispatcherTimer? _restTimer;

    /// <summary>Starts the dwell timer on entering a video tile (moves within it do not restart it).</summary>
    private void RestOn(MediaBrowserItem item)
    {
        if (ReferenceEquals(_restingOn, item))
            return;
        _restingOn = item;
        if (_restTimer is null)
        {
            _restTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(PrewarmRestMs) };
            _restTimer.Tick += RestTimer_Tick;
        }

        _restTimer.Stop();
        _restTimer.Start();
    }

    private void RestTimer_Tick(object? sender, EventArgs e)
    {
        _restTimer?.Stop();
        if (_restingOn is { IsFile: true } item)
            PrewarmRequested?.Invoke(item.Path, false);
    }

    /// <summary>Request to insert files at the playhead, as clips or (true) as audio-only clips.</summary>
    public event Action<IReadOnlyList<string>, bool>? AddRequested;

    /// <summary>Whether a video is loaded (selects the double-click action).</summary>
    public Func<bool>? HasVideo { get; set; }

    /// <summary>Fallback start folder supplied by the host (e.g. the loaded video's folder).</summary>
    public Func<string?>? SuggestFolder { get; set; }

    // Tile image decode height in physical pixels.
    private int PictureHeight => (int)Math.Ceiling((_view != MediaBrowserView.Grid ? 36 : Math.Round(ShownTileWidth * 9 / 16)) * VisualTreeHelper.GetDpi(this).DpiScaleY);

    private double ShownTileWidth => _shownTileWidth > 0 ? _shownTileWidth : _tileWidth;

    // Lazy first listing on first show: a hidden pane reads no folders.
    private void Pane_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            StopIndexing();
            return;
        }

        StartIndexDwell();

        if (!_started)
        {
            // Defer until after the window renders (Law 1): building 290 tiles synchronously once delayed Frame 1 by ~0.5 s.
            _started = true;
            Dispatcher.BeginInvoke(() => _ = ListAgain(fromStart: true), DispatcherPriority.ContextIdle);

            // Build on first need (Law 1): List view header, preview and cutter.
            Dispatcher.BeginInvoke(BuildDeferredParts, DispatcherPriority.ContextIdle);
        }
        else
        {
            ScheduleVisibleCheck();
        }
    }

    // Tiles added per dispatcher batch so rendering and input run between batches (the tile view is not virtualized).
    private const int TilesPerTurn = 24;

    /// <summary>Adds entries in batches; returns false if a newer listing started (and cleared the list).</summary>
    private async Task<bool> AddInTurnsAsync(IEnumerable<MediaBrowserItem> entries, CancellationToken token)
    {
        var added = 0;
        foreach (var entry in entries)
        {
            if (added > 0 && added % TilesPerTurn == 0)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (token.IsCancellationRequested)
                    return false;
            }

            _items.Add(entry);
            added++;
        }

        return true;
    }

    // ----- Folder navigation -----

    /// <summary>Shows a folder (null/empty = drive list); <paramref name="fromStart"/> resolves StartFolder first.</summary>
    public async Task NavigateAsync(string? folder, bool fromStart = false)
    {
        StopIndexing();
        _listing.Cancel();
        _listing = new CancellationTokenSource();
        var token = _listing.Token;
        _queue.Clear();
        _detailsWanted.Clear();
        _stripsWanted.Clear();
        _strips.Clear();
        _items.Clear();
        _projectListed = null;

        folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim().Trim('"');
        var (remembered, suggested) = fromStart ? (AppSettings.Current.BrowserFolder, SuggestFolder?.Invoke()) : ("", null);
        PathBox.Text = folder ?? "";
        StatusText.Text = "Reading the folder...";
        List<MediaBrowserItem>? entries;
        try
        {
            // Directory existence check off the UI thread (network folders can be slow).
            (folder, entries) = await Task.Run(() =>
            {
                var shown = fromStart ? StartFolder(remembered, suggested) : folder;
                return (shown, shown is null || Directory.Exists(shown) ? List(shown) : null);
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Controls.UiFault.ShowOnce(StatusText, $"folder|{folder}|{ex.HResult}", $"The folder could not be read: {ex.Message}", $"Media browser: the folder {folder}", ex);
            return;
        }

        if (token.IsCancellationRequested)
            return;

        PathBox.Text = folder ?? "";
        if (entries is null)
        {
            StatusText.Text = $"Not a folder that can be opened: {folder}";
            return;
        }

        _folder = folder;
        if (!await AddInTurnsAsync(Sorted(entries), token))
            return;

        if (folder is not null && !PickerMode)
            AppSettings.Set(s => s.BrowserFolder, (s, v) => s.BrowserFolder = v, folder);

        var files = entries.Count(e => e.IsFile);
        var folders = entries.Count - files;
        StatusText.Text = folder is null ? $"{folders} drive{(folders == 1 ? "" : "s")}."
            : $"{files} file{(files == 1 ? "" : "s")}, {folders} folder{(folders == 1 ? "" : "s")}{(entries.Count >= MostEntries ? $" (the first {MostEntries})" : "")}. Drag files onto the timeline.";
        if (_items.Count > 0)
            Items.ScrollIntoView(_items[0]);
        ScheduleVisibleCheck();
        StartIndexDwell();
    }

    // ----- Folder indexing -----
    // After the folder stays open for a dwell (Settings, default 40 s), every video's scrub strip is built one at a time as Idle work
    // (WorkScheduler), after visible-tile work. Posters remain visible-only. Skipped on battery or network drives unless Settings allow.

    private readonly DispatcherTimer _indexDwell = new();
    private CancellationTokenSource? _indexing;

    private void StartIndexDwell()
    {
        var delay = AppSettings.Current.BrowserIndexDelaySeconds;
        // Not in picker mode: a short-lived dialog prepares only visible tiles.
        if (_folder is null || !FolderMode || !IsVisible || delay <= 0 || _indexing is not null || PickerMode)
            return;

        if (!_indexHooked)
        {
            _indexDwell.Tick += (_, _) =>
            {
                _indexDwell.Stop();
                _ = IndexFolderAsync();
            };
            _indexHooked = true;
        }

        _indexDwell.Interval = TimeSpan.FromSeconds(delay);
        _indexDwell.Stop();
        _indexDwell.Start();
    }

    private bool _indexHooked;

    private void StopIndexing()
    {
        _indexDwell.Stop();
        _indexing?.Cancel();
        _indexing = null;
    }

    private async Task IndexFolderAsync()
    {
        var settings = AppSettings.Current;
        if (_folder is not { } folder || (!settings.BrowserIndexOnBattery && PowerStatus.OnBattery))
            return;
        if (!settings.BrowserIndexNetwork && await Task.Run(() => PowerStatus.IsNetworkFolder(folder)))
            return;

        var videos = _items.Where(i => i.IsVideo && i.StripPath is null).Take(Math.Max(settings.BrowserIndexMaxFiles, 0)).ToList();
        if (videos.Count == 0 || _folder != folder)
            return;

        var indexing = _indexing = CancellationTokenSource.CreateLinkedTokenSource(_listing.Token);
        try
        {
            var shown = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder;
            await WorkScheduler.Run(new WorkSpec($"Indexing {shown}", WorkLane.Idle) { Key = $"index|{folder}" }, async work =>
            {
                for (var i = 0; i < videos.Count; i++)
                {
                    // Visible tiles first: wait while the pane has pending images or a drag-and-drop is in progress (tools paused meanwhile).
                    while (_queue.Count > 0 || _working || WorkScheduler.IsHeld)
                        await Task.Delay(250, work.Token);

                    work.Report((double)i / videos.Count, $"{i + 1} of {videos.Count} videos");
                    if (videos[i].StripPath is null)
                        await PrepareStripAsync(videos[i], work.Token, WorkLane.Idle);
                    await PrebakeTimelineAsync(videos[i].Path, work.Token);
                }
            }, indexing.Token);

            if (settings.NotifyIndexingDone)
                Notifier.Show("Folder indexed", $"The videos in {folder} are ready to scrub in the media browser.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"Media browser: indexing {folder}", ex);
        }
        finally
        {
            if (ReferenceEquals(_indexing, indexing))
                _indexing = null;
            indexing.Dispose();
        }
    }

    /// <summary>Folder listing: subfolders, then media files, each sorted by name.</summary>
    private static List<MediaBrowserItem> List(string? folder)
    {
        if (folder is null)
        {
            return [.. DriveInfo.GetDrives().Where(d => d.IsReady).Select(d =>
                new MediaBrowserItem(d.RootDirectory.FullName, MediaBrowserKind.Drive, d.VolumeLabel.Length > 0 ? $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})" : d.Name.TrimEnd('\\')))];
        }

        static bool Shown(FileSystemInfo entry) => (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        var directory = new DirectoryInfo(folder);
        var list = new List<MediaBrowserItem>();
        foreach (var sub in directory.EnumerateDirectories().Where(Shown).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (list.Count >= MostEntries)
                return list;
            list.Add(new MediaBrowserItem(sub.FullName, MediaBrowserKind.Folder, sub.Name) { Modified = sub.LastWriteTime });
        }

        foreach (var file in directory.EnumerateFiles().Where(Shown).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (list.Count >= MostEntries)
                break;
            if (MediaFiles.KindOf(file.Name) is not { } kind)
                continue;

            list.Add(new MediaBrowserItem(file.FullName, kind, file.Name) { Info = MediaBrowserItem.FormatSize(file.Length), Bytes = file.Length, Modified = file.LastWriteTime });
        }

        return list;
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        _ = NavigateAsync(PathBox.Text);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder of media" };
        if (_folder is not null && Directory.Exists(_folder))
            dialog.InitialDirectory = _folder;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            _ = NavigateAsync(dialog.FolderName);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private void GoUp()
    {
        if (_folder is null || !FolderMode)
            return;

        // Up from a drive root goes to the drive list.
        _ = NavigateAsync(Directory.GetParent(_folder)?.FullName);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ListAgain();

    // ----- Sort order and tile size -----

    /// <summary>
    /// Sorts by the pane's Sort box and direction (initialized from Browser settings; the picker sorts independently and persists nothing).
    /// </summary>
    private List<MediaBrowserItem> Sorted(IEnumerable<MediaBrowserItem> entries) => SortedBy(entries, _sortKey, SortDirectionButton.IsChecked == true);

    /// <summary>
    /// Sorts drives and folders first (name, or date when sorting by date), then files by Name, Date, Size, Type or a List column
    /// (Resolution in pixels, Framerate, Duration, Codec, Audio stream count); unknown values sort last in either direction.
    /// </summary>
    internal static List<MediaBrowserItem> SortedBy(IEnumerable<MediaBrowserItem> entries, string key, bool descending)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var all = entries.ToList();
        IOrderedEnumerable<MediaBrowserItem> By<T>(IEnumerable<MediaBrowserItem> items, Func<MediaBrowserItem, bool> known, Func<MediaBrowserItem, T> value, IComparer<T>? order = null)
        {
            var unknownLast = items.OrderBy(i => known(i) ? 0 : 1);
            return descending ? unknownLast.ThenByDescending(value, order) : unknownLast.ThenBy(value, order);
        }

        IEnumerable<MediaBrowserItem> Order(IEnumerable<MediaBrowserItem> items, bool files)
        {
            IOrderedEnumerable<MediaBrowserItem> ordered = (key, files) switch
            {
                ("Date", _) => descending ? items.OrderByDescending(i => i.Modified) : items.OrderBy(i => i.Modified),
                ("Size", true) => descending ? items.OrderByDescending(i => i.Bytes) : items.OrderBy(i => i.Bytes),
                ("Type", true) => descending
                    ? items.OrderByDescending(i => Path.GetExtension(i.Name), comparer)
                    : items.OrderBy(i => Path.GetExtension(i.Name), comparer),
                ("Resolution", true) => By(items, i => i.VideoWidth > 0 && i.VideoHeight > 0, i => (long)i.VideoWidth * i.VideoHeight),
                ("Framerate", true) => By(items, i => i.FrameRate > 0, i => i.FrameRate),
                ("Duration", true) => By(items, i => i.Seconds > 0, i => i.Seconds),
                ("Codec", true) => By(items, i => i.Codec.Length > 0, i => i.CodecText, comparer),
                ("Audio", true) => By(items, i => i.AudioStreams >= 0, i => i.AudioStreams),
                _ => descending ? items.OrderByDescending(i => i.Name, comparer) : items.OrderBy(i => i.Name, comparer),
            };
            return ordered.ThenBy(i => i.Name, comparer);
        }

        return [.. Order(all.Where(i => !i.IsFile), files: false), .. Order(all.Where(i => i.IsFile), files: true)];
    }

    private void Resort()
    {
        var sorted = Sorted(_items);
        _items.Clear();
        foreach (var item in sorted)
            _items.Add(item);
        ScheduleVisibleCheck();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Cleared because a List column (not in the box) was chosen as the sort key: no-op.
        if (_syncingSort || SortBox.SelectedIndex < 0)
            return;

        _sortKey = SortKeys[SortBox.SelectedIndex];
        if (!PickerMode)
            AppSettings.Set(s => s.BrowserSort, (s, v) => s.BrowserSort = v, _sortKey);
        ShowSortInHeader();
        if (_items.Count > 0)
            Resort();
    }

    private void SortDirection_Changed(object sender, RoutedEventArgs e)
    {
        if (!PickerMode)
            AppSettings.Set(s => s.BrowserSortDescending, (s, v) => s.BrowserSortDescending = v, SortDirectionButton.IsChecked == true);
        ShowSortInHeader();
        if (_items.Count > 0)
            Resort();
    }

    /// <summary>Tile width for a Tile Size (0..100; below <see cref="ListBelow"/> the minimum); the panel stretches to fill.</summary>
    private void ApplyTileWidth(double value)
    {
        _tileWidth = Math.Round(SmallestTile + (Math.Max(value, ListBelow) - ListBelow) / (100 - ListBelow) * (LargestTile - SmallestTile));
        Resources["TileWidth"] = _tileWidth + TileChrome;
    }

    // Tile panel (recreated on each switch back to tiles) reported its stretched width; images narrower than shown are rebuilt once the
    // width settles, as after a Tile Size change.
    private void TilesStretched(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not VirtualizingTilePanel panel)
            return;

        var shown = Math.Max(panel.CellWidth - TileChrome, 1);
        if (Math.Abs(shown - _shownTileWidth) < 0.5)
            return;

        var grew = _shownTileWidth > 0 && shown > _shownTileWidth;
        _shownTileWidth = shown;
        if (grew)
        {
            _sizeSettled.Stop();
            _sizeSettled.Start();
        }
    }

    /// <summary>Rebuilds upscaled tile images from the cached strips on disk.</summary>
    private void SharpenPictures()
    {
        var wanted = PictureHeight * 0.85;
        _strips.Clear();
        foreach (var item in _items)
        {
            if (item.Poster is BitmapSource { PixelHeight: var made } && made < wanted)
                item.PreviewRequested = false;
        }

        ScheduleVisibleCheck();
    }

    // ----- Tile images -----

    private void ScheduleVisibleCheck()
    {
        _visibleCheck.Stop();
        _visibleCheck.Start();
    }

    // The list template wraps its ScrollViewer in a Decorator (ButtonChrome); find it by child, not decorator type, or visibility tests
    // find no tiles and no posters are made.
    private ScrollViewer? Scroller => VisualTreeHelper.GetChildrenCount(Items) > 0 && VisualTreeHelper.GetChild(Items, 0) is Decorator { Child: ScrollViewer scroller } ? scroller : null;

    // ----- Image retention -----
    // At rest only visible tiles (+1 row each side) hold images. With the pointer inside, off-screen tiles are filled nearest-first within
    // the byte budget so scrolling never shows blanks. One minute after the pointer leaves (or the last scroll), off-screen images are released.

    // Inactivity delay before off-screen images are released.
    private const int RestSeconds = 60;

    private readonly DispatcherTimer _rest = new() { Interval = TimeSpan.FromSeconds(RestSeconds) };

    // Visible tiles (+1 row each side) at the last check, and whether off-screen fill is active.
    private readonly HashSet<MediaBrowserItem> _inSight = [];
    private bool _prefetching;

    private void Pane_MouseEnter(object sender, MouseEventArgs e)
    {
        _rest.Stop();
        if (_prefetching)
            return;

        _prefetching = true;
        ScheduleVisibleCheck();
    }

    private void Pane_MouseLeave(object sender, MouseEventArgs e) => RestartRest();

    private void RestartRest()
    {
        _rest.Stop();
        if (_prefetching)
            _rest.Start();
    }

    /// <summary>Inactivity timeout: releases off-screen images and all decoded strips.</summary>
    private void Rest_Tick(object? sender, EventArgs e)
    {
        _rest.Stop();
        if (IsMouseOver)
            return;

        _prefetching = false;
        _queue.RemoveAll(i => !_inSight.Contains(i));
        var pictures = 0;
        foreach (var item in _items.Where(i => i.Poster is not null && !_inSight.Contains(i)))
        {
            (item.Poster, item.PreviewRequested) = (null, false);
            pictures++;
        }

        // Decoded strips serve hover scrubbing only; re-decoded off the UI thread on next hover.
        var strips = _strips.Count;
        _strips.Clear();

        if (pictures + strips > 0)
        {
            AppLog.Write($"Media browser: at rest for {RestSeconds} s, let go of {pictures} pictures out of sight and {strips} strips.");
            GiveBackMemory();
        }
    }

    /// <summary>
    /// Once idle with all wanted images built, requests one compacting GC plus process-heap compaction: after bursts the GC keeps its
    /// commit (24 of 45 MB after a folder's first images), bitmap pixels are native memory freed only by finalizers, and the process heap
    /// retains freed blocks. Goes through <see cref="MemoryHygiene"/> (coalesced, quiet-gated, collected on the UI thread in the same turn
    /// as the check, since a blocking GC suspends the frame pump and audio threads).
    /// </summary>
    private void GiveBackMemory()
    {
        if (_prefetching || _working)
            return;
        const string Reason = "media browser at rest";
        if (AskCompact is { } ask)
            ask(Reason);
        else
            MemoryHygiene.RequestCompactOnce(Reason, compactProcessHeap: true);
    }

    /// <summary>
    /// Set by the main window after Frame 1 to route <see cref="GiveBackMemory"/> through its quiet gate. Null for picker instances, which
    /// call <see cref="MemoryHygiene"/> directly (dropped when no window answers).
    /// </summary>
    public Action<string>? AskCompact { get; set; }

    /// <summary>Bytes held by tile images and decoded strips.</summary>
    private long HeldBytes() =>
        _items.Sum(i => i.Poster is BitmapSource picture ? (long)picture.PixelWidth * picture.PixelHeight * 4 : 0)
        + _strips.Sum(s => s.Frames.Sum(f => (long)f.PixelWidth * f.PixelHeight * 4));

    /// <summary>
    /// Queues images for visible tiles (+1 row each side) nearest-first, then, while the pointer is active in the pane, off-screen tiles
    /// nearest-first. Releases off-screen images when over budget.
    /// </summary>
    private void QueueVisible()
    {
        if (!IsVisible || Scroller is not { } scroller)
            return;

        // Realized tiles (panels realize only visible +1 row) and their positions.
        var boxes = new List<(MediaBrowserItem Item, Rect Box)>();
        foreach (var item in _items)
        {
            if (Items.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement { IsVisible: true } box)
                continue;

            try
            {
                boxes.Add((item, box.TransformToAncestor(scroller).TransformBounds(new Rect(box.RenderSize))));
            }
            catch (InvalidOperationException)
            {
                // Not yet arranged within the scroller.
            }
        }

        var row = boxes.Count > 0 ? boxes.Max(b => b.Box.Height) : 0;
        var view = new Rect(0, -row, scroller.ActualWidth, scroller.ActualHeight + 2 * row);
        _inSight.Clear();
        foreach (var (item, box) in boxes.Where(b => b.Box.IntersectsWith(view)))
            _inSight.Add(item);

        // List/Filmstrip view details for visible rows.
        WantDetails();

        var wanted = _items.Where(i => _inSight.Contains(i) && i.IsFile && !i.PreviewRequested).ToList();
        _queue.RemoveAll(wanted.Contains);
        _queue.InsertRange(0, wanted);

        // Pointer active: then off-screen tiles nearest-first. Queue membership uses a HashSet (a list scan cost tens of ms on
        // folders of thousands).
        if (_prefetching && _inSight.Count > 0)
        {
            var shown = _inSight.Select(_items.IndexOf).ToList();
            var (top, bottom) = (shown.Min(), shown.Max());
            var queued = _queue.ToHashSet();
            var rest = _items.Select((item, index) => (Item: item, Distance: index < top ? top - index : index - bottom))
                .Where(p => !_inSight.Contains(p.Item) && p.Item.IsFile && !p.Item.PreviewRequested && !queued.Contains(p.Item))
                .OrderBy(p => p.Distance).Select(p => p.Item).ToList();
            _queue.AddRange(rest);
        }

        if (HeldBytes() > PosterBudgetBytes)
        {
            foreach (var item in _items.Where(i => i.Poster is not null && !_inSight.Contains(i)))
                (item.Poster, item.PreviewRequested) = (null, false);
        }

        if (!_working && (_queue.Count > 0 || _stripsWanted.Any(_inSight.Contains)))
            _ = WorkAsync();
    }

    /// <summary>Image worker: processes queued files one at a time while the listed folder is unchanged.</summary>
    private async Task WorkAsync()
    {
        _working = true;
        var token = _listing.Token;

        // Uncached videos wait until every visible cached tile shows its poster (no tool run), so one long file never blocks the rest.
        // Their poster is a single frame (PreparePosterAsync); strips follow in a second pass once all visible posters show: Background
        // lane visible-first, or Interactive immediately for a hovered tile without a strip (Picture_MouseMove).
        var unkept = new List<MediaBrowserItem>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Drag-and-drop in progress (WorkScheduler.HoldBackground): start nothing, Interactive posters included.
                while (WorkScheduler.IsHeld)
                    await Task.Delay(HeldPollMilliseconds, token);

                MediaBrowserItem item;
                var waited = false;
                if (_queue.Count > 0 && (unkept.Count == 0 || _inSight.Contains(_queue[0])))
                {
                    item = _queue[0];
                    _queue.RemoveAt(0);
                }
                else if (unkept.Count > 0)
                {
                    (item, waited) = (unkept[0], true);
                    unkept.RemoveAt(0);
                }
                else if (NextStripWanted() is { } wanted)
                {
                    if (wanted.StripPath is null && !_stripsOnDemand.Contains(wanted))
                        await PrepareStripAsync(wanted, token, WorkLane.Background);
                    continue;
                }
                else
                {
                    break;
                }

                if (item.PreviewRequested)
                    continue;

                // Off-screen tiles only while the pointer is active and under budget; the rest re-queue when visible.
                if (!_inSight.Contains(item) && (!_prefetching || HeldBytes() >= PosterBudgetBytes))
                {
                    _queue.RemoveAll(i => !_inSight.Contains(i));
                    unkept.RemoveAll(i => !_inSight.Contains(i));
                    continue;
                }

                if (!waited && item.Kind == MediaBrowserKind.Video && item.StripPath is null)
                {
                    if (!await TakeKeptAsync(item, token))
                        unkept.Add(item);
                    continue;
                }

                item.PreviewRequested = true;
                await PrepareAsync(item, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Media browser", ex);
        }
        finally
        {
            _working = false;
        }

        // Folder changed meanwhile: restart for the new listing.
        if (_queue.Count > 0 && !_listing.IsCancellationRequested)
            _ = WorkAsync();
        else if (!_prefetching)
            GiveBackMemory();
    }

    /// <summary>
    /// Cache-only tile fill: with cached metadata and strip, reads and slices them off the UI thread (~1 ms) and shows them; with only a
    /// cached poster, shows it and queues the strip for the second pass. False when nothing is cached.
    /// </summary>
    private async Task<bool> TakeKeptAsync(MediaBrowserItem item, CancellationToken token)
    {
        var settings = AppSettings.Current;
        var (path, height) = (item.Path, PictureHeight);
        var (frames, frameHeight) = (Math.Clamp(settings.BrowserPreviewFrames, 2, 40), Math.Clamp(settings.BrowserPreviewHeight, 90, 1080));
        if (await Task.Run(() => MediaBrowserFrames.Kept(path, frames, frameHeight, height, 0.3), token) is { } kept)
        {
            item.PreviewRequested = true;
            (item.Seconds, item.Info) = (kept.Seconds, kept.Seconds > 0 ? MediaBrowserItem.FormatSeconds(kept.Seconds) : item.Info);
            (item.Shape, item.StripPath, item.Poster) = ((double)kept.Width / kept.Height, kept.Strip, kept.Poster);
            return true;
        }

        if (await Task.Run(() => MediaBrowserFrames.KeptPosterOnly(path, height), token) is not { Poster: not null } poster)
            return false;

        item.PreviewRequested = true;
        (item.Seconds, item.Info) = (poster.Seconds, poster.Seconds > 0 ? MediaBrowserItem.FormatSeconds(poster.Seconds) : item.Info);
        (item.Shape, item.Poster) = ((double)poster.Width / poster.Height, poster.Poster);
        WantStrip(item);
        return true;
    }

    /// <summary>Queues a poster-only video for the second (strip) pass.</summary>
    private void WantStrip(MediaBrowserItem item)
    {
        if (item.StripPath is null && !_stripsWanted.Contains(item))
            _stripsWanted.Add(item);
    }

    /// <summary>
    /// Next second-pass video: a visible one first, else (pointer active) the first waiting; otherwise off-screen ones wait until
    /// visible or for the folder indexer.
    /// </summary>
    private MediaBrowserItem? NextStripWanted()
    {
        // Listed-check via a set: a list scan per waiting item was O(n^2) on the UI thread for large folders.
        if (_stripsWanted.Count > 0)
        {
            var listed = _items.ToHashSet();
            _stripsWanted.RemoveAll(i => i.StripPath is not null || !listed.Contains(i));
        }

        var next = _stripsWanted.FirstOrDefault(_inSight.Contains) ?? (_prefetching ? _stripsWanted.FirstOrDefault() : null);
        if (next is not null)
            _stripsWanted.Remove(next);
        return next;
    }

    private async Task PrepareAsync(MediaBrowserItem item, CancellationToken token)
    {
        var height = PictureHeight;
        var settings = AppSettings.Current;
        switch (item.Kind)
        {
            case MediaBrowserKind.Image:
                item.Poster = await Task.Run(() => MediaBrowserFrames.Picture(item.Path, height), token);
                return;

            case MediaBrowserKind.Audio:
                if (item.Seconds <= 0 && await MediaBrowserFrames.DescribeAsync(item.Path, token) is { Seconds: > 0 } sound)
                    (item.Seconds, item.Info) = (sound.Seconds, MediaBrowserItem.FormatSeconds(sound.Seconds));
                return;

            case MediaBrowserKind.Video:
                // Visible tile: poster in the Interactive lane (ahead of Background proxies, waveforms, thumbnails). One frame first; the
                // strip follows in the second pass, unless no standalone poster could be made, in which case the strip is built now.
                var lane = _inSight.Contains(item) ? WorkLane.Interactive : WorkLane.Background;
                if (item.StripPath is null)
                {
                    if (await PreparePosterAsync(item, token, lane, height))
                    {
                        WantStrip(item);
                        return;
                    }

                    if (item.Shape <= 0 || item.Info == "cannot be read")
                        return;
                    await PrepareStripAsync(item, token, lane);
                }

                if (item.StripPath is { } strip)
                    item.Poster = await Task.Run(() => MediaBrowserFrames.Poster(strip, item.Shape, height, 0.3), token);
                return;
        }
    }

    /// <summary>
    /// First pass for an uncached video: probes duration and size, and gets a one-frame poster (cached or built now). False on read or
    /// decode failure.
    /// </summary>
    private static async Task<bool> PreparePosterAsync(MediaBrowserItem item, CancellationToken token, WorkLane lane, int height)
    {
        if (await MediaBrowserFrames.DescribeAsync(item.Path, token, lane) is not { } video)
        {
            item.Info = "cannot be read";
            return false;
        }

        (item.Seconds, item.Info) = (video.Seconds, video.Seconds > 0 ? MediaBrowserItem.FormatSeconds(video.Seconds) : item.Info);
        if (video.Width <= 0 || video.Height <= 0)
            return false;

        item.Shape = (double)video.Width / video.Height;
        if (await MediaBrowserFrames.PosterOnlyAsync(item.Path, video.Seconds, height, token, lane) is not { } poster)
            return false;

        item.Poster = await Task.Run(() => MediaBrowserFrames.Picture(poster, height), token);
        return item.Poster is not null;
    }

    /// <summary>Probes duration and size and gets the frame strip (cached or built now) needed for hover scrubbing.</summary>
    private static async Task PrepareStripAsync(MediaBrowserItem item, CancellationToken token, WorkLane lane)
    {
        if (await MediaBrowserFrames.DescribeAsync(item.Path, token, lane) is not { } video)
        {
            item.Info = "cannot be read";
            return;
        }

        (item.Seconds, item.Info) = (video.Seconds, video.Seconds > 0 ? MediaBrowserItem.FormatSeconds(video.Seconds) : item.Info);
        if (video.Width <= 0 || video.Height <= 0)
            return;

        var settings = AppSettings.Current;
        item.Shape = (double)video.Width / video.Height;
        var (frames, frameHeight) = (Math.Clamp(settings.BrowserPreviewFrames, 2, 40), Math.Clamp(settings.BrowserPreviewHeight, 90, 1080));
        item.StripPath = await MediaBrowserFrames.StripAsync(item.Path, video.Seconds, video.Width, video.Height, frames, frameHeight, token, lane);
    }

    // ----- Hover scrubbing -----

    private void Picture_MouseMove(object sender, MouseEventArgs e)
    {
        // Hover dwell signals a likely drop.
        if (sender is FrameworkElement { DataContext: MediaBrowserItem { IsVideo: true } hovered })
            RestOn(hovered);

        // Poster-only tile: build its strip now (Interactive lane) instead of waiting for the second pass; not with a button
        // down (that is a drag start: the file will be dropped and probed, not scrubbed).
        if (sender is FrameworkElement { DataContext: MediaBrowserItem { IsVideo: true, StripPath: null, Poster: not null } waiting })
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                _ = MakeStripNowAsync(waiting);
            return;
        }

        if (sender is not FrameworkElement { DataContext: MediaBrowserItem { IsVideo: true, StripPath: not null } item, ActualWidth: > 0 } picture)
            return;

        var fraction = Math.Clamp(e.GetPosition(picture).X / picture.ActualWidth, 0, 1);
        item.HoverFraction = fraction;
        item.HoverText = item.Seconds > 0 ? $"{MediaBrowserItem.FormatSeconds(fraction * item.Seconds)} / {MediaBrowserItem.FormatSeconds(item.Seconds)}" : "";
        if (StripOf(item) is { } frames)
            item.HoverFrame = frames[Math.Min((int)(fraction * frames.Count), frames.Count - 1)];
        else
            _ = DecodeStripAsync(item);
    }

    /// <summary>
    /// Builds a hovered video's strip immediately to enable scrubbing. Single-flight: hovering another tile cancels it back to the second
    /// pass, so sweeping across tiles never queues a multi-second FFmpeg job per tile ahead of the next real action.
    /// </summary>
    private async Task MakeStripNowAsync(MediaBrowserItem item)
    {
        if (!_stripsOnDemand.Add(item))
            return;

        StopStripOnDemand();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_listing.Token);
        _onDemand = (item, stop);
        _stripsWanted.Remove(item);
        try
        {
            await PrepareStripAsync(item, stop.Token, WorkLane.Interactive);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Media browser", ex);
        }
        finally
        {
            _stripsOnDemand.Remove(item);
            if (_onDemand is { } current && ReferenceEquals(current.Stop, stop))
                _onDemand = null;

            // Cancelled by another hover or a drag in the same listing: requeue for the second pass. The cancelled source is left to
            // the GC (its cancellation may still be running on the pool).
            if (stop.IsCancellationRequested)
            {
                if (!_listing.IsCancellationRequested && item.StripPath is null && _items.Contains(item))
                {
                    WantStrip(item);
                    ScheduleVisibleCheck();
                }
            }
            else
            {
                stop.Dispose();
            }
        }
    }

    // In-flight hover strip job and its cancellation source.
    private (MediaBrowserItem Item, CancellationTokenSource Stop)? _onDemand;

    /// <summary>
    /// Cancels the hover strip job (new hover or drag start). Tool processes are killed on the pool: walking the process tree is too slow
    /// for the UI thread.
    /// </summary>
    private void StopStripOnDemand()
    {
        if (_onDemand is not { } running)
            return;

        _onDemand = null;
        _ = running.Stop.CancelAsync();
    }

    private void Picture_MouseLeave(object sender, MouseEventArgs e)
    {
        _restTimer?.Stop();
        _restingOn = null;
        if (sender is FrameworkElement { DataContext: MediaBrowserItem item })
            (item.HoverFraction, item.HoverFrame, item.HoverText) = (double.NaN, null, "");
    }

    private IReadOnlyList<BitmapSource>? StripOf(MediaBrowserItem item)
    {
        for (var node = _strips.First; node is not null; node = node.Next)
        {
            if (!ReferenceEquals(node.Value.Item, item))
                continue;

            _strips.Remove(node);
            _strips.AddFirst(node);
            return node.Value.Frames;
        }

        return null;
    }

    /// <summary>Decodes the hovered tile's strip at tile size off the UI thread, then shows the frame under the pointer.</summary>
    private async Task DecodeStripAsync(MediaBrowserItem item)
    {
        if (ReferenceEquals(_decoding, item) || item.StripPath is not { } strip)
            return;

        _decoding = item;
        var (height, shape) = (PictureHeight, item.Shape);
        var frames = await Task.Run(() => MediaBrowserFrames.Frames(strip, shape, height));
        _decoding = null;
        if (frames is not { Count: > 0 } || !_items.Contains(item))
            return;

        _strips.AddFirst((item, frames));
        while (_strips.Count > StripsKept)
            _strips.RemoveLast();
        if (item.IsScrubbing)
            item.HoverFrame = frames[Math.Min((int)(item.HoverFraction * frames.Count), frames.Count - 1)];
    }

    // ----- Open, insert, drag out -----

    private static MediaBrowserItem? FindItem(DependencyObject? element)
    {
        for (var at = element; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is ListBoxItem { DataContext: MediaBrowserItem item })
                return item;
        }

        return null;
    }

    private List<MediaBrowserItem> Selected() => [.. Items.SelectedItems.OfType<MediaBrowserItem>()];

    /// <summary>Data format marking a drag as originating from the media browser (alongside FileDrop).</summary>
    public const string DragFormat = "HandPeg.MediaBrowserFiles";

    private void Open(MediaBrowserItem item)
    {
        if (!item.IsFile)
        {
            _ = NavigateAsync(item.Path);
            return;
        }

        // Picker: toggle selection instead of opening.
        if (PickerMode)
        {
            TogglePick(item);
            return;
        }

        if (HasVideo?.Invoke() == true)
            AddRequested?.Invoke([item.Path], item.Kind == MediaBrowserKind.Audio);
        else if (item.Kind != MediaBrowserKind.Image)
            LoadRequested?.Invoke(item.Path);
    }

    private void Item_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: MediaBrowserItem item })
        {
            e.Handled = true;
            Open(item);
        }
    }

    private void Items_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            GoUp();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Items.SelectedItem is MediaBrowserItem item)
        {
            Open(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && _projectMode && !PickerMode)
        {
            // Project Media Delete (OwnsKey): remove selected files from the bin.
            RemoveSelectedFromProject();
            e.Handled = true;
        }
    }

    private void Items_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressAt = e.GetPosition(Items);
        _pressed = FindItem(e.OriginalSource as DependencyObject);
        _keepSelection = false;

        // Pressing a member of a multi-selection keeps the selection (even with Ctrl/Shift held) so all can be dragged; the press's own
        // selection change is applied on a no-drag release (Items_PreviewMouseLeftButtonUp). Not in picker mode (clicks toggle, no drag).
        if (!PickerMode && e.ClickCount == 1 && _pressed is not null && Items.SelectedItems.Count > 1 && Items.SelectedItems.Contains(_pressed))
        {
            (_keepSelection, _pressKeys) = (true, Keyboard.Modifiers);
            e.Handled = true;
        }
    }

    private void Items_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (PickerMode || _pressAt is not { } from || e.LeftButton != MouseButtonState.Pressed || _pressed is not { IsFile: true } pressed)
            return;

        var moved = e.GetPosition(Items) - from;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _pressAt = null;
        _keepSelection = false;
        var files = Items.SelectedItems.Contains(pressed) ? Selected().Where(i => i.IsFile).Select(i => i.Path).ToArray() : [pressed.Path];

        // FileDrop like Explorer, so every Explorer drop target accepts it; the browser marker makes drops with media open insert
        // onto the timeline instead of replacing it.
        var data = new DataObject(DataFormats.FileDrop, files);
        data.SetData(DragFormat, true);

        // DoDragDrop runs a nested loop on this thread: hold background work (tools paused), pause pane work between files, and cancel
        // the hover strip; the drop's own work then holds it (MainWindow.Drops). Internal drags skip drop pre-warm (MainWindow.IsInternalDrag);
        // hover dwell still pre-warms (PrewarmRequested).
        StopStripOnDemand();
        _restTimer?.Stop();
        using (WorkScheduler.HoldBackground())
            DragDrop.DoDragDrop(Items, data, DragDropEffects.Copy);
    }

    private void Items_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_keepSelection && _pressed is not null)
        {
            if (_pressKeys.HasFlag(ModifierKeys.Control))
            {
                Items.SelectedItems.Remove(_pressed);
            }
            else if (!_pressKeys.HasFlag(ModifierKeys.Shift))
            {
                Items.SelectedItems.Clear();
                Items.SelectedItem = _pressed;
            }
        }

        (_pressAt, _keepSelection) = (null, false);
    }

    // ----- Middle-click auto-scroll -----
    // Middle press sets an anchor; scroll speed scales with pointer distance from it (small dead zone). Press-drag-release stops on
    // release; a click continues until any click, Escape or capture loss. Per-frame (CompositionTarget) steps scale by elapsed time.

    private Point? _autoScrollAnchor;
    private Point _autoScrollPointer;
    private bool _autoScrollDragged;
    private TimeSpan _autoScrollLast;
    private const double AutoScrollStill = 8, AutoScrollSpeed = 8, AutoScrollMaxSpeed = 6000;

    private void Items_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_autoScrollAnchor is not null)
        {
            // Any press while auto-scrolling only ends it.
            StopAutoScroll();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Middle || Scroller is not { ScrollableHeight: > 0 } || !Items.CaptureMouse())
            return;

        // Auto-scroll replaces any wheel coast.
        ScrollMomentum.Stop(Items);
        _autoScrollAnchor = _autoScrollPointer = e.GetPosition(Items);
        (_autoScrollDragged, _autoScrollLast) = (false, TimeSpan.Zero);
        Items.Cursor = Cursors.ScrollNS;
        Items.PreviewMouseMove += AutoScroll_MouseMove;
        CompositionTarget.Rendering += AutoScroll_Rendering;
        e.Handled = true;
    }

    private void AutoScroll_MouseMove(object sender, MouseEventArgs e)
    {
        if (_autoScrollAnchor is not { } anchor)
            return;

        _autoScrollPointer = e.GetPosition(Items);
        if (e.MiddleButton == MouseButtonState.Pressed && Math.Abs(_autoScrollPointer.Y - anchor.Y) > AutoScrollStill)
            _autoScrollDragged = true;
    }

    private void Items_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _autoScrollAnchor is null)
            return;

        // Drag mode ends on release; click mode continues until the next click.
        if (_autoScrollDragged)
            StopAutoScroll();
        e.Handled = true;
    }

    private void Items_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _autoScrollAnchor is null)
            return;

        StopAutoScroll();
        e.Handled = true;
    }

    private void Items_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_autoScrollAnchor is not null && !ReferenceEquals(Mouse.Captured, Items))
            StopAutoScroll();
    }

    private void AutoScroll_Rendering(object? sender, EventArgs e)
    {
        if (_autoScrollAnchor is not { } anchor || Scroller is not { } scroller || !IsVisible)
        {
            StopAutoScroll();
            return;
        }

        var now = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        var seconds = _autoScrollLast == TimeSpan.Zero ? 0 : Math.Min((now - _autoScrollLast).TotalSeconds, 0.1);
        _autoScrollLast = now;
        var distance = _autoScrollPointer.Y - anchor.Y;
        if (seconds <= 0 || Math.Abs(distance) <= AutoScrollStill)
            return;

        var speed = Math.Clamp((distance - Math.Sign(distance) * AutoScrollStill) * AutoScrollSpeed, -AutoScrollMaxSpeed, AutoScrollMaxSpeed);
        scroller.ScrollToVerticalOffset(Math.Clamp(scroller.VerticalOffset + speed * seconds, 0, scroller.ScrollableHeight));
    }

    private void StopAutoScroll()
    {
        if (_autoScrollAnchor is null)
            return;

        _autoScrollAnchor = null;
        CompositionTarget.Rendering -= AutoScroll_Rendering;
        Items.PreviewMouseMove -= AutoScroll_MouseMove;
        Items.ClearValue(CursorProperty);
        if (ReferenceEquals(Mouse.Captured, Items))
            Items.ReleaseMouseCapture();
    }

    private void Items_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Items.ContextMenu is not { } menu)
            return;

        menu.Items.Clear();
        void Add(string header, Action action, bool enabled = true)
        {
            var entry = new MenuItem { Header = header, IsEnabled = enabled };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }

        var under = FindItem(e.OriginalSource as DependencyObject);
        if (under is not null && !Items.SelectedItems.Contains(under) && !PickerMode)
            Items.SelectedItem = under;

        var files = Selected().Where(i => i.IsFile).ToList();
        var loaded = HasVideo?.Invoke() == true;
        if (under is { IsFile: false })
        {
            Add("Open", () => _ = NavigateAsync(under.Path));
        }
        else if (PickerMode)
        {
            // Picker: no open/insert; selection unchanged.
            if (under is { IsFile: true })
                Add("Show in Explorer", () => ShowInExplorer(under.Path));
        }
        else if (files.Count > 0)
        {
            var playable = files.Where(f => f.Kind != MediaBrowserKind.Image).ToList();
            Add(loaded ? "Open in Place of the Open Video" : "Open Video", () => LoadRequested?.Invoke(playable[0].Path), playable.Count == 1);
            Add(files.Count > 1 ? "Add to Timeline at Playhead (End to End)" : "Add to Timeline at Playhead",
                () => AddRequested?.Invoke([.. files.Where(f => f.Kind != MediaBrowserKind.Audio).Select(f => f.Path)], false),
                loaded && files.Any(f => f.Kind != MediaBrowserKind.Audio));
            Add("Add as Audio at Playhead", () => AddRequested?.Invoke([.. playable.Select(f => f.Path)], true), loaded && playable.Count > 0);
            menu.Items.Add(new Separator());
            AddProjectMediaItems(menu, files);
            Add("Show in Explorer", () => ShowInExplorer(files[0].Path));

            // Clip Library Delete: remove clips and their files.
            if (_libraryMode)
                Add(files.Count > 1 ? $"Remove {files.Count} Clips from Clip Library" : "Remove from Clip Library", () => RemoveFromLibrary(files));
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        if (FolderMode)
            Add("Up One Folder", GoUp, _folder is not null);
        Add("Refresh", () => _ = ListAgain());
    }

    private void RemoveFromLibrary(IEnumerable<MediaBrowserItem> files)
    {
        foreach (var file in files)
        {
            if (ClipLibrary.Find(file.Path) is { } entry)
                ClipLibrary.Remove(entry);
        }

        _ = ListLibraryAsync();
    }

    // Shell launches run on the pool (they block for tens of ms).
    private static void ShowInExplorer(string path) => _ = Task.Run(() =>
    {
        try
        {
            ShellOpen.Reveal(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write("Media browser: Explorer could not be opened", ex);
        }
    });
}
