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
/// The media browser pane (side column): a folder's videos, sounds and pictures, to drag onto the
/// timeline. Each video's tile is a small timeline of its own: moving the pointer across it shows the frame at that
/// point. The frames are made in the background, one video at a time, from a strip of a few frames per video (Settings:
/// Preview &amp; Cache) kept in the browser's cache, so a folder opens at once the next time. Only the tiles in view (and
/// a row either side) are made and hold a picture at rest; with the pointer in the pane, the tiles out of sight get
/// theirs too, within a budget, until a minute after it left. Only the strips of the tiles last hovered are held whole.
/// </summary>
public partial class MediaBrowserPane : UserControl
{
    // The size slider: below this the files are a list; above it, tiles from the smallest width to the largest.
    private const double ListBelow = 8, SmallestTile = 96, LargestTile = 420;

    private static readonly string[] SortKeys = ["Name", "Date", "Size", "Type"];

    // A folder with more entries than this lists the first ones, and says so.
    private const int MostEntries = 3000;

    // Pictures held beyond this many bytes give up those of the tiles out of sight (made again from the cache when
    // they come back). Bytes, not a count: at the largest tiles one poster is twenty times what it is at the smallest.
    private const long PosterBudgetBytes = 32L * 1024 * 1024;

    // The decoded strips of the tiles hovered last.
    private const int StripsKept = 6;

    // How often the pane's work looks again whether background work is still held for a drag and drop (4.3E).
    private const int HeldPollMilliseconds = 100;

    private readonly ObservableCollection<MediaBrowserItem> _items = [];
    private readonly DispatcherTimer _visibleCheck = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _sizeSettled = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool? _listView;
    private double _tileWidth = 200;
    private readonly List<MediaBrowserItem> _queue = [];

    // The second pass (4.2D): videos whose tile shows a poster made on its own, waiting for their strip (Background lane),
    // and those whose strip is being made because the pointer crossed them first (Interactive).
    private readonly List<MediaBrowserItem> _stripsWanted = [];
    private readonly HashSet<MediaBrowserItem> _stripsOnDemand = [];
    private readonly LinkedList<(MediaBrowserItem Item, IReadOnlyList<BitmapSource> Frames)> _strips = new();
    private CancellationTokenSource _listing = new();
    private bool _working, _started;
    private MediaBrowserItem? _decoding;
    private string? _folder;

    // A press in the list: where, on what, and whether a selection of several was kept for dragging them all.
    private Point? _pressAt;
    private MediaBrowserItem? _pressed;
    private bool _keepSelection;

    public MediaBrowserPane()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
        _visibleCheck.Tick += (_, _) =>
        {
            _visibleCheck.Stop();
            QueueVisible();
        };
        Items.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        {
            ScheduleVisibleCheck();

            // A scroll is something happening in the pane too: the minute before the pictures out of sight go starts again.
            if (!IsMouseOver)
                RestartRest();
        }));
        MouseEnter += Pane_MouseEnter;
        MouseLeave += Pane_MouseLeave;
        _rest.Tick += Rest_Tick;
        Items.SizeChanged += (_, _) => ScheduleVisibleCheck();
        _sizeSettled.Tick += (_, _) =>
        {
            _sizeSettled.Stop();
            SharpenPictures();
        };

        var settings = AppSettings.Current;
        var sort = Array.IndexOf(SortKeys, settings.BrowserSort);
        SortBox.SelectedIndex = sort < 0 ? 0 : sort;
        SortDirectionButton.IsChecked = settings.BrowserSortDescending;
        SizeSlider.Value = Math.Clamp(settings.BrowserTileSize, 0, 100);
        ApplySize(SizeSlider.Value);

        _projectMode = settings.BrowserShowsProject;
        (_projectMode ? ProjectSide : FolderSide).IsChecked = true;
        FolderBar.Visibility = _projectMode ? Visibility.Collapsed : Visibility.Visible;
        _projectSettled.Tick += (_, _) =>
        {
            _projectSettled.Stop();
            if (_projectMode && _started && IsVisible)
                _ = ListProjectAsync();
        };
    }

    // ----- Project Media or Folder Browser -----
    // Project Media lists every file the open project uses (ProjectFiles), with no folder bar; Folder Browser is a folder
    // on disk. The folder last shown is kept for coming back to it.

    private bool _projectMode;
    private readonly DispatcherTimer _projectSettled = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>The media files the open project uses, for Project Media.</summary>
    public Func<IReadOnlyList<string>>? ProjectFiles { get; set; }

    /// <summary>The project's files have changed (a clip added or removed, another video loaded): Project Media lists them again, once things settle.</summary>
    public void ProjectChanged()
    {
        if (!_projectMode)
            return;

        _projectSettled.Stop();
        _projectSettled.Start();
    }

    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        var project = ReferenceEquals(sender, ProjectSide);
        if (!IsInitialized || project == _projectMode)
            return;

        _projectMode = AppSettings.Current.BrowserShowsProject = project;
        FolderBar.Visibility = project ? Visibility.Collapsed : Visibility.Visible;
        if (!_started)
            return;

        if (project)
            _ = ListProjectAsync();
        else
            _ = _folder is null ? NavigateAsync(null, fromStart: true) : NavigateAsync(_folder);
    }

    /// <summary>Lists the files the open project uses, sorted as the folder's would be; a file that is gone says so.</summary>
    private async Task ListProjectAsync()
    {
        StopIndexing();
        _listing.Cancel();
        _listing = new CancellationTokenSource();
        var token = _listing.Token;
        _queue.Clear();
        _stripsWanted.Clear();
        _strips.Clear();
        _items.Clear();

        var files = ProjectFiles?.Invoke() ?? [];
        List<MediaBrowserItem> entries;
        try
        {
            entries = await Task.Run(() => files.Select(path =>
            {
                var file = new FileInfo(path);
                var kind = MediaBrowserItem.KindOf(file.Name) ?? MediaBrowserKind.Video;
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
            StatusText.Text = $"The project's files could not be read: {ex.Message}";
            return;
        }

        if (token.IsCancellationRequested || !await AddInTurnsAsync(Sorted(entries), token))
            return;

        StatusText.Text = entries.Count == 0
            ? "The project uses no media files yet: open a video, or drop files onto the Layers or Audio tab."
            : $"{entries.Count} file{(entries.Count == 1 ? "" : "s")} in this project. Drag them onto the Layers or Audio tab again.";
        if (_items.Count > 0)
            Items.ScrollIntoView(_items[0]);
        ScheduleVisibleCheck();
    }

    /// <summary>
    /// Where Folder Browser starts when no folder was shown before: the remembered one, the video's, or Videos. It asks
    /// the disk, so it is never asked on the interface's thread (a remembered folder on a network drive that is not
    /// there can keep it waiting for seconds, and the pane is first shown while the window is being laid out).
    /// </summary>
    private static string StartFolder(string remembered, string? suggested) =>
        Directory.Exists(remembered) ? remembered
        : suggested is not null && Directory.Exists(suggested) ? suggested
        : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>A file was asked to be opened as the main video.</summary>
    public event Action<string>? LoadRequested;

    /// <summary>How large the files are shown (the size slider, 0 a list to 100 the largest tiles): the Media tab's menu sets it (4.2B-2).</summary>
    public double TileSize
    {
        get => SizeSlider.Value;
        set => SizeSlider.Value = Math.Clamp(value, SizeSlider.Minimum, SizeSlider.Maximum);
    }

    /// <summary>Files were asked to be put on the timeline at the playhead: as clips, or (true) as sound on the Audio tab.</summary>
    public event Action<IReadOnlyList<string>, bool>? AddRequested;

    /// <summary>Whether a video is loaded, which decides what a double-click on a file does.</summary>
    public Func<bool>? HasVideo { get; set; }

    /// <summary>Where to start when no folder was shown before: the loaded video's folder, say.</summary>
    public Func<string?>? SuggestFolder { get; set; }

    // The height a tile's picture is decoded at, in real pixels.
    private int PictureHeight => (int)Math.Ceiling((_listView == true ? 36 : Math.Round(_tileWidth * 9 / 16)) * VisualTreeHelper.GetDpi(this).DpiScaleY);

    // Opened the first time the pane is shown, not before: a hidden pane reads no folders.
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
            // Once the window has drawn (it may be the one being laid out for its first frame): what a folder holds is not
            // what the first frame waits for. A folder of 290 videos, its tiles all made at once, held it back half a second.
            _started = true;
            Dispatcher.BeginInvoke(() => _ = _projectMode ? ListProjectAsync() : NavigateAsync(null, fromStart: true), DispatcherPriority.ContextIdle);
        }
        else
        {
            ScheduleVisibleCheck();
        }
    }

    // How many tiles go in at a time: the window draws and answers the pointer between one batch and the next (the tile
    // view is not virtualized, so every tile is made as it is added).
    private const int TilesPerTurn = 24;

    /// <summary>Adds the entries a batch at a time; false when a new listing began meanwhile (it has cleared the list).</summary>
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

    // ----- The folder -----

    /// <summary>Shows a folder; null or empty for the drives; with <paramref name="fromStart"/>, the folder it starts in (StartFolder).</summary>
    public async Task NavigateAsync(string? folder, bool fromStart = false)
    {
        StopIndexing();
        _listing.Cancel();
        _listing = new CancellationTokenSource();
        var token = _listing.Token;
        _queue.Clear();
        _stripsWanted.Clear();
        _strips.Clear();
        _items.Clear();

        folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim().Trim('"');
        var (remembered, suggested) = fromStart ? (AppSettings.Current.BrowserFolder, SuggestFolder?.Invoke()) : ("", null);
        PathBox.Text = folder ?? "";
        StatusText.Text = "Reading the folder...";
        List<MediaBrowserItem>? entries;
        try
        {
            // Away from the interface, whether it is a folder too: a folder on a network drive can take a moment to answer.
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
            StatusText.Text = $"The folder could not be read: {ex.Message}";
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

        if (folder is not null)
            AppSettings.Current.BrowserFolder = folder;

        var files = entries.Count(e => e.IsFile);
        var folders = entries.Count - files;
        StatusText.Text = folder is null ? $"{folders} drive{(folders == 1 ? "" : "s")}."
            : $"{files} file{(files == 1 ? "" : "s")}, {folders} folder{(folders == 1 ? "" : "s")}{(entries.Count >= MostEntries ? $" (the first {MostEntries})" : "")}. Drag files onto the Layers or Audio tab.";
        if (_items.Count > 0)
            Items.ScrollIntoView(_items[0]);
        ScheduleVisibleCheck();
        StartIndexDwell();
    }

    // ----- Indexing the folder shown -----
    // A folder that stays open for a while (Settings, 40 s by default) has every one of its videos prepared, not only
    // those on screen: their frames for the pointer to scrub through, made one at a time as Idle work (see
    // WorkScheduler), after whatever the tiles on screen are waiting for. Posters are still made for tiles on
    // screen only. Not on battery, nor on a network drive, unless Settings says so.

    private readonly DispatcherTimer _indexDwell = new();
    private CancellationTokenSource? _indexing;

    private void StartIndexDwell()
    {
        var delay = AppSettings.Current.BrowserIndexDelaySeconds;
        if (_folder is null || _projectMode || !IsVisible || delay <= 0 || _indexing is not null)
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
                    // The tiles on screen go first: the indexer waits while the pane has their pictures to make, and while a
                    // file is dragged and dropped (4.3E; its tool is stopped where it is meanwhile).
                    while (_queue.Count > 0 || _working || WorkScheduler.IsHeld)
                        await Task.Delay(250, work.Token);

                    work.Report((double)i / videos.Count, $"{i + 1} of {videos.Count} videos");
                    if (videos[i].StripPath is null)
                        await PrepareStripAsync(videos[i], work.Token, WorkLane.Idle);
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
            AppLog.Write($"Media browser: indexing {folder}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_indexing, indexing))
                _indexing = null;
            indexing.Dispose();
        }
    }

    /// <summary>What a folder holds that the browser shows: its folders, then its media files, each by name.</summary>
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
            if (MediaBrowserItem.KindOf(file.Name) is not { } kind)
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
        if (_folder is null || _projectMode)
            return;

        // From a drive's root, up is the list of drives.
        _ = NavigateAsync(Directory.GetParent(_folder)?.FullName);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _projectMode ? ListProjectAsync() : NavigateAsync(_folder);

    // ----- The view: its order and its size -----

    /// <summary>The entries in the order chosen: drives and folders first (by name, or by date when sorting by date), then the files.</summary>
    private static List<MediaBrowserItem> Sorted(IEnumerable<MediaBrowserItem> entries)
    {
        var settings = AppSettings.Current;
        var (key, descending) = (settings.BrowserSort, settings.BrowserSortDescending);
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var all = entries.ToList();
        IEnumerable<MediaBrowserItem> Order(IEnumerable<MediaBrowserItem> items, bool files)
        {
            IOrderedEnumerable<MediaBrowserItem> ordered = (key, files) switch
            {
                ("Date", _) => descending ? items.OrderByDescending(i => i.Modified) : items.OrderBy(i => i.Modified),
                ("Size", true) => descending ? items.OrderByDescending(i => i.Bytes) : items.OrderBy(i => i.Bytes),
                ("Type", true) => descending
                    ? items.OrderByDescending(i => Path.GetExtension(i.Name), comparer)
                    : items.OrderBy(i => Path.GetExtension(i.Name), comparer),
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
        AppSettings.Current.BrowserSort = SortKeys[Math.Max(SortBox.SelectedIndex, 0)];
        if (_items.Count > 0)
            Resort();
    }

    private void SortDirection_Changed(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.BrowserSortDescending = SortDirectionButton.IsChecked == true;
        if (_items.Count > 0)
            Resort();
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
            return;

        AppSettings.Current.BrowserTileSize = (int)Math.Round(e.NewValue);
        ApplySize(e.NewValue);

        // The pictures are scaled at once; once the slider rests, those now drawn larger than they were made are made again.
        _sizeSettled.Stop();
        _sizeSettled.Start();
    }

    /// <summary>The list, or tiles of a width: from the size slider's value.</summary>
    private void ApplySize(double value)
    {
        var list = value < ListBelow;
        _tileWidth = Math.Round(SmallestTile + (Math.Max(value, ListBelow) - ListBelow) / (100 - ListBelow) * (LargestTile - SmallestTile));
        Resources["TileWidth"] = _tileWidth;
        Resources["TileHeight"] = Math.Round(_tileWidth * 9 / 16);
        if (_listView == list)
            return;

        // Both views make only what is in view (the tiles' panel scrolls itself by pixels).
        _listView = list;
        Items.ItemTemplate = (DataTemplate)Resources[list ? "RowTemplate" : "TileTemplate"];
        Items.ItemsPanel = (ItemsPanelTemplate)Resources[list ? "RowPanel" : "TilePanel"];
        VirtualizingPanel.SetIsVirtualizing(Items, true);
        ScrollViewer.SetCanContentScroll(Items, true);
        ScheduleVisibleCheck();
    }

    /// <summary>Makes again, from the strips already on disk, the pictures now shown larger than they were made.</summary>
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

    // ----- Pictures, for the tiles on screen -----

    private void ScheduleVisibleCheck()
    {
        _visibleCheck.Stop();
        _visibleCheck.Start();
    }

    // The list's template is a decorator around its scroller (a Border, since 4.1.0 a ButtonChrome): taken by what it
    // holds, never by the decorator's type, or no tile is ever found in sight and no poster made (4.1.1).
    private ScrollViewer? Scroller => VisualTreeHelper.GetChildrenCount(Items) > 0 && VisualTreeHelper.GetChild(Items, 0) is Decorator { Child: ScrollViewer scroller } ? scroller : null;

    // ----- What is held, and for how long -----
    // At rest, only the tiles in view (and one row either side) hold their pictures. Once the pointer comes into the pane,
    // the pictures of the tiles out of sight are made too, nearest first, while what is held stays within the budget, so
    // scrolling never finds tiles blank. A minute after the pointer left the pane (or after the last scroll without it),
    // the pictures out of sight are let go again, and with them the memory they took.

    // How long the pane keeps the pictures out of sight once nothing happens in it.
    private const int RestSeconds = 60;

    private readonly DispatcherTimer _rest = new() { Interval = TimeSpan.FromSeconds(RestSeconds) };

    // The tiles in view (and one row either side) at the last check, and whether those out of sight are being made.
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

    /// <summary>A minute with nothing in the pane: the pictures of the tiles out of sight, and every decoded strip, are let go.</summary>
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

        // The decoded strips are for the pointer scrubbing across a tile: none is wanted at rest, and a tile's is decoded
        // again (at its size, away from the interface) the next time the pointer comes onto it.
        var strips = _strips.Count;
        _strips.Clear();

        if (pictures + strips > 0)
        {
            AppLog.Write($"Media browser: at rest for {RestSeconds} s, let go of {pictures} pictures out of sight and {strips} strips.");
            GiveBackMemory();
        }
    }

    /// <summary>
    /// At rest, with every picture wanted made: the memory that making them took is given back to the system. After a
    /// burst of work the collector keeps what it committed for later (24 MB of 45 after a folder's first pictures); a
    /// picture's pixels are native memory, freed only once it is collected and its finalizer has run; and the process
    /// heap keeps freed blocks committed. At rest nothing would bring any of it back for a long while. Once the window is
    /// idle, away from it (a finalizer must never wait on the interface's thread): an aggressive collection, the
    /// finalizers, another, and the heap's free blocks handed back.
    /// </summary>
    private void GiveBackMemory()
    {
        if (_givingBack)
            return;

        _givingBack = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (_prefetching || _working)
            {
                _givingBack = false;
                return;
            }

            Task.Run(() =>
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                HeapCompact(GetProcessHeap(), 0);
            }).ContinueWith(_ => _givingBack = false, TaskScheduler.Default);
        }, DispatcherPriority.ApplicationIdle);
    }

    private volatile bool _givingBack;

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial IntPtr GetProcessHeap();

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial nuint HeapCompact(IntPtr heap, uint flags);

    /// <summary>What the held pictures and decoded strips take, in bytes.</summary>
    private long HeldBytes() =>
        _items.Sum(i => i.Poster is BitmapSource picture ? (long)picture.PixelWidth * picture.PixelHeight * 4 : 0)
        + _strips.Sum(s => s.Frames.Sum(f => (long)f.PixelWidth * f.PixelHeight * 4));

    /// <summary>
    /// Asks for the pictures of the tiles in view and one row either side, nearest first; while the pointer has been in
    /// the pane, those of the tiles out of sight after them (nearest first). Lets go of those out of sight when more than
    /// the budget is held.
    /// </summary>
    private void QueueVisible()
    {
        if (!IsVisible || Scroller is not { } scroller)
            return;

        // The tiles made (the panels make only those in view and a row either side), where they are.
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
                // Not laid out under the scroller (yet).
            }
        }

        var row = boxes.Count > 0 ? boxes.Max(b => b.Box.Height) : 0;
        var view = new Rect(0, -row, scroller.ActualWidth, scroller.ActualHeight + 2 * row);
        _inSight.Clear();
        foreach (var (item, box) in boxes.Where(b => b.Box.IntersectsWith(view)))
            _inSight.Add(item);

        var wanted = _items.Where(i => _inSight.Contains(i) && i.IsFile && !i.PreviewRequested).ToList();
        _queue.RemoveAll(wanted.Contains);
        _queue.InsertRange(0, wanted);

        // With the pointer in the pane: the others after them, nearest to those in view first. (Whether a tile is queued is
        // asked of a set: asked of the list, a folder of thousands took tens of milliseconds as the pointer came in, 4.3E.)
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

    /// <summary>Makes the pictures asked for, one file at a time, for as long as the folder shown stays the same.</summary>
    private async Task WorkAsync()
    {
        _working = true;
        var token = _listing.Token;

        // Videos whose strip is not kept yet (4.1.1): their posters are made only after every tile in sight whose strip or
        // poster is kept has its poster (no tool run), so one long video never holds up the rest. Since 4.2D those posters
        // are one frame each (PreparePosterAsync), and the strips come in a second pass, once every tile in sight has its
        // poster on screen: in the Background lane, a tile in sight first; at once, in the Interactive lane, for a tile the
        // pointer crosses before its strip is there (Picture_MouseMove).
        var unkept = new List<MediaBrowserItem>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                // A file being dragged, or what its drop needs (4.3E, WorkScheduler.HoldBackground): nothing more is started
                // until it is done, the Interactive lane's posters included.
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

                // A tile out of sight only while the pointer has been in the pane and the budget has room: once it is
                // full, the rest wait (asked again when they come into view).
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
            AppLog.Write($"Media browser: {ex.Message}");
        }
        finally
        {
            _working = false;
        }

        // Another folder was opened meanwhile: its pictures are made now.
        if (_queue.Count > 0 && !_listing.IsCancellationRequested)
            _ = WorkAsync();
        else if (!_prefetching)
            GiveBackMemory();
    }

    /// <summary>
    /// A video's tile from the cache alone (4.1.1): its length, shape, strip and poster when its description and strip are
    /// kept, read and cut away from the interface (a millisecond or so), and shown at once. Since 4.2D also a poster made
    /// on its own and kept, its strip then wanted in the second pass. False when neither is kept.
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

    /// <summary>A video whose tile shows a poster made on its own: its strip is made in the second pass.</summary>
    private void WantStrip(MediaBrowserItem item)
    {
        if (item.StripPath is null && !_stripsWanted.Contains(item))
            _stripsWanted.Add(item);
    }

    /// <summary>
    /// The next video whose strip the second pass makes: one in sight first, else (while the pointer has been in the pane,
    /// so pictures out of sight are being made too) the first waiting. Those out of sight otherwise wait for their tile to
    /// come into view again, or for the folder indexer.
    /// </summary>
    private MediaBrowserItem? NextStripWanted()
    {
        // Whether a video is still listed is asked of a set (4.3E): asked of the list, for every one waiting, it was
        // thousands times thousands of steps on the interface's thread for a large folder.
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
                // A tile in sight is what the user is waiting for (4.1.1): its poster goes in the Interactive lane, never
                // behind the proxies, waveforms and thumbnails of the Background lane. One frame first (4.2D); the strip
                // follows in the second pass. Only when no poster could be made on its own is the strip made at once.
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
    /// The first pass for a video whose strip is not kept (4.2D): its length and shape, and one frame as its poster (kept,
    /// or made now). False when the file cannot be read or the frame could not be made.
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

    /// <summary>A video's length and shape, and its strip of frames (kept, or made now): what scrubbing across its tile needs.</summary>
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

    // ----- The pointer across a tile: the frame at that point -----

    private void Picture_MouseMove(object sender, MouseEventArgs e)
    {
        // A tile showing a poster made on its own (4.2D): its strip is made now, in the Interactive lane, not left to wait
        // for the second pass. Not while the button is down (4.3E): that is a drag beginning, and the file is about to be
        // dropped (and probed), not scrubbed.
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
    /// A hovered video's strip, made at once (the second pass would have made it later); then scrubbing works. One at a
    /// time (4.3E): the strip of a tile the pointer has left for another is stopped and goes back to the second pass, so
    /// the pointer crossing a row of tiles on its way somewhere does not fill the Interactive lane with a strip per tile
    /// (each a few seconds of FFmpeg) for what the user does next to wait behind.
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
            AppLog.Write($"Media browser: {ex.Message}");
        }
        finally
        {
            _stripsOnDemand.Remove(item);
            if (_onDemand is { } current && ReferenceEquals(current.Stop, stop))
                _onDemand = null;

            // Stopped for another tile or a drag, in the same listing: its strip is made in the second pass after all. A
            // source that was cancelled is left to the collector (its cancelling may still be running on the pool).
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

    // The strip being made at once for the tile last hovered, and what stops it.
    private (MediaBrowserItem Item, CancellationTokenSource Stop)? _onDemand;

    /// <summary>
    /// Stops the strip being made at once for a hovered tile (another tile is hovered, or a drag begins). Its tools are
    /// stopped on the thread pool: ending a tool and its children walks every process, too long for the interface's thread.
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

    /// <summary>Decodes the hovered tile's strip, at the tile's size, away from the interface; then the frame under the pointer is shown.</summary>
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

    // ----- Opening, adding, dragging out -----

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

    /// <summary>The drag format that marks files dragged out of the media browser (beside the file list itself).</summary>
    public const string DragFormat = "HandPeg.MediaBrowserFiles";

    private void Open(MediaBrowserItem item)
    {
        if (!item.IsFile)
        {
            _ = NavigateAsync(item.Path);
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
    }

    private void Items_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressAt = e.GetPosition(Items);
        _pressed = FindItem(e.OriginalSource as DependencyObject);
        _keepSelection = false;

        // A press on one of several selected files keeps them all selected, so that all of them can be dragged.
        if (e.ClickCount == 1 && _pressed is not null && Items.SelectedItems.Count > 1 && Items.SelectedItems.Contains(_pressed) && Keyboard.Modifiers == ModifierKeys.None)
        {
            _keepSelection = true;
            e.Handled = true;
        }
    }

    private void Items_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is not { } from || e.LeftButton != MouseButtonState.Pressed || _pressed is not { IsFile: true } pressed)
            return;

        var moved = e.GetPosition(Items) - from;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _pressAt = null;
        _keepSelection = false;
        var files = Items.SelectedItems.Contains(pressed) ? Selected().Where(i => i.IsFile).Select(i => i.Path).ToArray() : [pressed.Path];

        // As files from Explorer: whatever takes those (the time bars, the Audio tab, the player) takes these. Marked as
        // the media browser's, so that with something open they go onto the timeline wherever they are
        // let go, never over what is open (4.1.1-B).
        var data = new DataObject(DataFormats.FileDrop, files);
        data.SetData(DragFormat, true);

        // For as long as it is dragged (the drag is a loop of its own on this thread, which the pane's work would stall)
        // background work is held back and its tools stopped where they are, the pane's own work waits between files, and
        // a strip being made for the pointer is stopped (4.3E). The drop's own work holds it from there (MainWindow.Drops).
        StopStripOnDemand();
        using (WorkScheduler.HoldBackground())
            DragDrop.DoDragDrop(Items, data, DragDropEffects.Copy);
    }

    private void Items_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_keepSelection && _pressed is not null)
        {
            Items.SelectedItems.Clear();
            Items.SelectedItem = _pressed;
        }

        (_pressAt, _keepSelection) = (null, false);
    }

    // ----- Middle-click auto-scroll (Pass 4.2B-2) -----
    // The middle button pressed over the files sets an anchor: the list scrolls on its own, as fast as the pointer is
    // far above or below it (a few DIPs around it stand still). Held and moved, letting go stops it; clicked, it goes on
    // until the next click of any button, Escape, or the list losing the pointer. Each frame (CompositionTarget) moves
    // it by the time since the last, so the speed is the same at any frame rate.

    private Point? _autoScrollAnchor;
    private Point _autoScrollPointer;
    private bool _autoScrollDragged;
    private TimeSpan _autoScrollLast;
    private const double AutoScrollStill = 8, AutoScrollSpeed = 8, AutoScrollMaxSpeed = 6000;

    private void Items_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_autoScrollAnchor is not null)
        {
            // Any press while scrolling ends it, and does nothing else.
            StopAutoScroll();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Middle || Scroller is not { ScrollableHeight: > 0 } || !Items.CaptureMouse())
            return;

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

        // Held and dragged: letting go ends it. A click: it goes on until the next click.
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
        if (under is not null && !Items.SelectedItems.Contains(under))
            Items.SelectedItem = under;

        var files = Selected().Where(i => i.IsFile).ToList();
        var loaded = HasVideo?.Invoke() == true;
        if (under is { IsFile: false })
        {
            Add("Open", () => _ = NavigateAsync(under.Path));
        }
        else if (files.Count > 0)
        {
            var playable = files.Where(f => f.Kind != MediaBrowserKind.Image).ToList();
            Add("Load as Main Video", () => LoadRequested?.Invoke(playable[0].Path), playable.Count == 1);
            Add(files.Count > 1 ? "Add to Timeline at Playhead (End to End)" : "Add to Timeline at Playhead",
                () => AddRequested?.Invoke([.. files.Where(f => f.Kind != MediaBrowserKind.Audio).Select(f => f.Path)], false),
                loaded && files.Any(f => f.Kind != MediaBrowserKind.Audio));
            Add("Add Sound to the Audio Tab at Playhead", () => AddRequested?.Invoke([.. playable.Select(f => f.Path)], true), loaded && playable.Count > 0);
            menu.Items.Add(new Separator());
            Add("Show in Explorer", () => ShowInExplorer(files[0].Path));
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        if (!_projectMode)
            Add("Up One Folder", GoUp, _folder is not null);
        Add("Refresh", () => _ = _projectMode ? ListProjectAsync() : NavigateAsync(_folder));
    }

    // Started on the thread pool (4.3E): a shell start takes the interface's thread for tens of milliseconds.
    private static void ShowInExplorer(string path) => _ = Task.Run(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"Media browser: Explorer could not be opened: {ex.Message}");
        }
    });
}
