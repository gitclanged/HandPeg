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
/// The media browser pane (Editor Mode, side column): a folder's videos, sounds and pictures, to drag onto the
/// timeline. Each video's tile is a small timeline of its own: moving the pointer across it shows the frame at that
/// point. The frames are made in the background, one video at a time and only for the tiles on screen, from a strip
/// of a few frames per video (Settings: Preview &amp; Cache) kept in the browser's cache, so a folder opens at once the
/// next time. Only the strip of the tile last hovered is held whole in memory; the other tiles hold one small frame.
/// </summary>
public partial class MediaBrowserPane : UserControl
{
    // The views: how wide a tile's picture is (a row's, for the list).
    private static readonly (string Name, double Width)[] ViewSizes = [("List", 64), ("Small", 128), ("Medium", 200), ("Large", 320)];

    // A folder with more entries than this lists the first ones, and says so.
    private const int MostEntries = 3000;

    // Tiles holding a picture beyond this many give up the pictures of those out of sight (made again from the cache when they come back).
    private const int MostPosters = 300;

    // The decoded strips of the tiles hovered last.
    private const int StripsKept = 6;

    private readonly ObservableCollection<MediaBrowserItem> _items = [];
    private readonly DispatcherTimer _visibleCheck = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly List<MediaBrowserItem> _queue = [];
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
        Items.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => ScheduleVisibleCheck()));
        Items.SizeChanged += (_, _) => ScheduleVisibleCheck();

        var view = Array.FindIndex(ViewSizes, v => v.Name == AppSettings.Current.BrowserView);
        ViewBox.SelectedIndex = view < 0 ? 2 : view;
    }

    /// <summary>A file was asked to be opened as the main video.</summary>
    public event Action<string>? LoadRequested;

    /// <summary>Files were asked to be put on the timeline at the playhead: as clips, or (true) as sound on the Audio tab.</summary>
    public event Action<IReadOnlyList<string>, bool>? AddRequested;

    /// <summary>Whether a video is loaded, which decides what a double-click on a file does.</summary>
    public Func<bool>? HasVideo { get; set; }

    /// <summary>Where to start when no folder was shown before: the loaded video's folder, say.</summary>
    public Func<string?>? SuggestFolder { get; set; }

    // The height a tile's picture is decoded at, in real pixels.
    private int PictureHeight
    {
        get
        {
            var (name, width) = ViewSizes[Math.Max(ViewBox.SelectedIndex, 0)];
            var height = name == "List" ? 36 : Math.Round(width * 9 / 16);
            return (int)Math.Ceiling(height * VisualTreeHelper.GetDpi(this).DpiScaleY);
        }
    }

    // Opened the first time the pane is shown, not before: a hidden pane reads no folders.
    private void Pane_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
            return;

        if (!_started)
        {
            _started = true;
            var remembered = AppSettings.Current.BrowserFolder;
            var start = Directory.Exists(remembered) ? remembered
                : SuggestFolder?.Invoke() is { } suggested && Directory.Exists(suggested) ? suggested
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            _ = NavigateAsync(start);
        }
        else
        {
            ScheduleVisibleCheck();
        }
    }

    // ----- The folder -----

    /// <summary>Shows a folder; null or empty for the drives.</summary>
    public async Task NavigateAsync(string? folder)
    {
        _listing.Cancel();
        _listing = new CancellationTokenSource();
        var token = _listing.Token;
        _queue.Clear();
        _strips.Clear();
        _items.Clear();

        folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim().Trim('"');
        PathBox.Text = folder ?? "";
        if (folder is not null && !Directory.Exists(folder))
        {
            StatusText.Text = $"Not a folder that can be opened: {folder}";
            return;
        }

        _folder = folder;
        StatusText.Text = "Reading the folder...";
        List<MediaBrowserItem> entries;
        try
        {
            // Away from the interface: a folder on a network drive can take a moment to answer.
            entries = await Task.Run(() => List(folder), token);
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

        foreach (var entry in entries)
            _items.Add(entry);

        if (folder is not null)
            AppSettings.Current.BrowserFolder = folder;

        var files = entries.Count(e => e.IsFile);
        var folders = entries.Count - files;
        StatusText.Text = folder is null ? $"{folders} drive{(folders == 1 ? "" : "s")}."
            : $"{files} file{(files == 1 ? "" : "s")}, {folders} folder{(folders == 1 ? "" : "s")}{(entries.Count >= MostEntries ? $" (the first {MostEntries})" : "")}. Drag files onto the Layers or Audio tab.";
        if (_items.Count > 0)
            Items.ScrollIntoView(_items[0]);
        ScheduleVisibleCheck();
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
            list.Add(new MediaBrowserItem(sub.FullName, MediaBrowserKind.Folder, sub.Name));
        }

        foreach (var file in directory.EnumerateFiles().Where(Shown).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (list.Count >= MostEntries)
                break;
            if (MediaBrowserItem.KindOf(file.Name) is not { } kind)
                continue;

            list.Add(new MediaBrowserItem(file.FullName, kind, file.Name) { Info = MediaBrowserItem.FormatSize(file.Length) });
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
        if (_folder is null)
            return;

        // From a drive's root, up is the list of drives.
        _ = NavigateAsync(Directory.GetParent(_folder)?.FullName);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = NavigateAsync(_folder);

    // ----- The view -----

    private void ViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var (name, width) = ViewSizes[Math.Max(ViewBox.SelectedIndex, 0)];
        AppSettings.Current.BrowserView = name;
        var list = name == "List";
        Items.ItemTemplate = (DataTemplate)Resources[list ? "RowTemplate" : "TileTemplate"];
        Items.ItemsPanel = (ItemsPanelTemplate)Resources[list ? "RowPanel" : "TilePanel"];
        VirtualizingPanel.SetIsVirtualizing(Items, list);
        ScrollViewer.SetCanContentScroll(Items, list);
        Resources["TileWidth"] = width;
        Resources["TileHeight"] = Math.Round(width * 9 / 16);

        // The pictures were made for the size before: made again, from the strips already on disk.
        _strips.Clear();
        foreach (var item in _items)
            (item.Poster, item.HoverFrame, item.PreviewRequested) = (null, null, false);
        ScheduleVisibleCheck();
    }

    // ----- Pictures, for the tiles on screen -----

    private void ScheduleVisibleCheck()
    {
        _visibleCheck.Stop();
        _visibleCheck.Start();
    }

    private ScrollViewer? Scroller => VisualTreeHelper.GetChildrenCount(Items) > 0 && VisualTreeHelper.GetChild(Items, 0) is Border { Child: ScrollViewer scroller } ? scroller : null;

    /// <summary>Asks for the pictures of the tiles on screen (and just beyond), nearest first; lets go of those far out of sight when too many are held.</summary>
    private void QueueVisible()
    {
        if (!IsVisible || Scroller is not { } scroller)
            return;

        var view = new Rect(0, -scroller.ActualHeight / 2, scroller.ActualWidth, scroller.ActualHeight * 2);
        var seen = new HashSet<MediaBrowserItem>();
        foreach (var item in _items)
        {
            if (Items.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement { IsVisible: true } box)
                continue;

            try
            {
                if (box.TransformToAncestor(scroller).TransformBounds(new Rect(box.RenderSize)).IntersectsWith(view))
                    seen.Add(item);
            }
            catch (InvalidOperationException)
            {
                // Not laid out under the scroller (yet).
            }
        }

        var wanted = _items.Where(i => seen.Contains(i) && i.IsFile && !i.PreviewRequested).ToList();
        _queue.RemoveAll(wanted.Contains);
        _queue.InsertRange(0, wanted);

        if (_items.Count(i => i.Poster is not null) > MostPosters)
        {
            foreach (var item in _items.Where(i => i.Poster is not null && !seen.Contains(i)))
                (item.Poster, item.PreviewRequested) = (null, false);
        }

        if (!_working && _queue.Count > 0)
            _ = WorkAsync();
    }

    /// <summary>Makes the pictures asked for, one file at a time, for as long as the folder shown stays the same.</summary>
    private async Task WorkAsync()
    {
        _working = true;
        var token = _listing.Token;
        try
        {
            while (_queue.Count > 0 && !token.IsCancellationRequested)
            {
                var item = _queue[0];
                _queue.RemoveAt(0);
                if (item.PreviewRequested)
                    continue;

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
                if (item.StripPath is null)
                {
                    if (await MediaBrowserFrames.DescribeAsync(item.Path, token) is not { } video)
                    {
                        item.Info = "cannot be read";
                        return;
                    }

                    (item.Seconds, item.Info) = (video.Seconds, video.Seconds > 0 ? MediaBrowserItem.FormatSeconds(video.Seconds) : item.Info);
                    if (video.Width <= 0 || video.Height <= 0)
                        return;

                    item.Shape = (double)video.Width / video.Height;
                    var (frames, frameHeight) = (Math.Clamp(settings.BrowserPreviewFrames, 2, 40), Math.Clamp(settings.BrowserPreviewHeight, 90, 1080));
                    item.StripPath = await MediaBrowserFrames.StripAsync(item.Path, video.Seconds, video.Width, video.Height, frames, frameHeight, token);
                }

                if (item.StripPath is { } strip)
                    item.Poster = await Task.Run(() => MediaBrowserFrames.Poster(strip, item.Shape, height, 0.3), token);
                return;
        }
    }

    // ----- The pointer across a tile: the frame at that point -----

    private void Picture_MouseMove(object sender, MouseEventArgs e)
    {
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

        // As files from Explorer: whatever takes those (the time bars, the Audio tab, the player) takes these.
        DragDrop.DoDragDrop(Items, new DataObject(DataFormats.FileDrop, files), DragDropEffects.Copy);
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
        Add("Up One Folder", GoUp, _folder is not null);
        Add("Refresh", () => _ = NavigateAsync(_folder));
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"Media browser: Explorer could not be opened: {ex.Message}");
        }
    }
}
