using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.Views;

/// <summary>How the media browser lays its files out (5.4C): tiles, a table of details, or a preview and cutter beside a list.</summary>
public enum MediaBrowserView { Grid, List, Filmstrip }

// ----- The media browser's three views (5.4C) -----
// One list (Items) in all three, its template and panel switched (so the pictures, the hover scrubbing, the selection and the drag work
// the same in each): Grid, the tiles that fill the pane (5.3.1); List, a table of each file's details under a header whose columns sort,
// a narrow pane dropping the columns that matter least, and once the pane is 600 DIPs wide the preview and cutter beside it; Filmstrip,
// the preview and sub-clip cutter on the left and the list in a column on the right (BrowserFilmstripListWidth), a splitter between.
// Law 1: the pane is made before the first frame and shown by default, so nothing here is made in the constructor but the columns' widths;
// the header, the preview and the cutter are made after the first frame (BuildDeferredParts, ContextIdle), and the details are read only
// for the rows in sight, off the interface's thread.
public partial class MediaBrowserPane
{
    /// <summary>The List view shows its preview from this width of the pane, DIPs...</summary>
    internal const double SplitPreviewFrom = 600;

    /// <summary>...and hides it below this one, so dragging a splitter across the line never makes it flicker.</summary>
    internal const double SplitPreviewBelow = 576;

    /// <summary>The least the List view's table keeps beside its preview, DIPs.</summary>
    internal const double ListKeepsAtLeast = 340;

    /// <summary>The Filmstrip view's list: its least width, DIPs (and at most half the pane)...</summary>
    internal const double FilmstripListLeast = 180;

    /// <summary>...down to this one in a narrow pane (5.4D), so the preview beside it keeps <see cref="PreviewLeast"/>.</summary>
    internal const double FilmstripListNarrow = 140;

    /// <summary>The least the Filmstrip view's preview and cutter keep beside the list, DIPs (5.4D).</summary>
    internal const double PreviewLeast = 120;

    /// <summary>The Filmstrip view's list width to start with, and the one a double-click on its splitter puts back (5.4D), DIPs.</summary>
    internal const double FilmstripListDefault = 240;

    private const double SplitterWidth = 5;

    // The List view's columns after the name, in the order they stand, with the width each takes and the sort key of its header.
    internal static readonly (string Resource, string Key, string Header, double Width)[] DetailColumns =
    [
        ("ColResolution", "Resolution", "Resolution", 84),
        ("ColFramerate", "Framerate", "Framerate", 66),
        ("ColDuration", "Duration", "Duration", 62),
        ("ColCodec", "Codec", "Codec", 58),
        ("ColAudio", "Audio", "Audio", 48),
        ("ColSize", "Size", "Size", 70),
    ];

    // Which of them a narrowing table lets go first: the sound streams and the codec, then the size, the frame rate, the resolution.
    private static readonly string[] DroppedFirst = ["Audio", "Codec", "Size", "Framerate", "Resolution", "Duration"];

    // The picture's column, and the least the name keeps, DIPs; and what a row has round its cells (the list item's edge and padding).
    private const double ThumbColumn = 72, NameAtLeast = 120, RowChrome = 16;

    private MediaBrowserView _view;
    private bool _viewSet, _splitShown, _syncingView, _syncingSort;
    private double _tileSize;
    private string _sortKey = "Name";

    /// <summary>The view the pane shows; setting it switches the view at once and, outside the picker, keeps it in
    /// <see cref="Services.AppSettings.BrowserView"/>.</summary>
    public MediaBrowserView ViewMode
    {
        get => _view;
        set => SetView(value, remember: true);
    }

    /// <summary>
    /// The view a saved <see cref="AppSettings.BrowserView"/> names; an empty or unknown one is the List view where the tile size was a
    /// list's (below <see cref="ListBelow"/>, as the size slider had it before 5.4C), else the Grid.
    /// </summary>
    internal static MediaBrowserView ViewOf(string? saved, int tileSize) =>
        Enum.TryParse<MediaBrowserView>(saved?.Trim(), ignoreCase: true, out var view) && Enum.IsDefined(view) ? view
        : tileSize < ListBelow ? MediaBrowserView.List : MediaBrowserView.Grid;

    /// <summary>
    /// How large the tiles are, 0 to 100 (the tab menu's Tile Size, 4.2B-2): a value below <see cref="ListBelow"/> is the List view (as the
    /// size slider's smallest steps were), any other the Grid at that size. Outside the picker it is kept in BrowserTileSize.
    /// </summary>
    public double TileSize
    {
        get => _tileSize;
        set
        {
            _tileSize = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);
            if (!PickerMode)
                AppSettings.Current.BrowserTileSize = (int)Math.Round(_tileSize);
            ApplyTileWidth(_tileSize);
            SetView(_tileSize < ListBelow ? MediaBrowserView.List : MediaBrowserView.Grid, remember: true);

            // The pictures are scaled at once; once the size rests, those now drawn larger than they were made are made again.
            _sizeSettled.Stop();
            _sizeSettled.Start();
        }
    }

    /// <summary>The sort and the view as the settings have them (the constructor; the picker leaves Filmstrip out, as it has no timeline).</summary>
    private void InitializeView(AppSettings settings)
    {
        _tileSize = Math.Clamp(settings.BrowserTileSize, 0, 100);
        ApplyTileWidth(_tileSize);
        if (PickerMode)
            FilmstripChoice.Visibility = Visibility.Collapsed;
        SetView(ViewOf(settings.BrowserView, settings.BrowserTileSize), remember: false);
    }

    /// <summary>The sort the settings name: one of the Sort box's, or a column of the List view (the box then shows none).</summary>
    private void InitializeSort(AppSettings settings)
    {
        var known = SortKeys.Concat(DetailColumns.Select(c => c.Key)).FirstOrDefault(k => string.Equals(k, settings.BrowserSort, StringComparison.OrdinalIgnoreCase));
        _sortKey = known ?? "Name";
        _syncingSort = true;
        try
        {
            SortBox.SelectedIndex = Array.IndexOf(SortKeys, _sortKey);
        }
        finally
        {
            _syncingSort = false;
        }
    }

    private void ViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingView || !IsInitialized || ViewBox.SelectedIndex < 0)
            return;

        SetView((MediaBrowserView)ViewBox.SelectedIndex, remember: true);
    }

    /// <summary>Shows a view (the picker never shows Filmstrip: the Grid instead), kept in the settings when <paramref name="remember"/>.</summary>
    private void SetView(MediaBrowserView view, bool remember)
    {
        if (!Enum.IsDefined(view) || (PickerMode && view == MediaBrowserView.Filmstrip))
            view = MediaBrowserView.Grid;
        if (remember && !PickerMode)
            AppSettings.Current.BrowserView = view.ToString();
        if (ViewBox.SelectedIndex != (int)view)
        {
            _syncingView = true;
            try
            {
                ViewBox.SelectedIndex = (int)view;
            }
            finally
            {
                _syncingView = false;
            }
        }

        if (_viewSet && view == _view)
            return;

        var was = _viewSet ? _view : (MediaBrowserView?)null;
        (_view, _viewSet) = (view, true);
        ApplyView(was);
        ViewModeChanged?.Invoke(view);
    }

    /// <summary>The view shown has changed (5.4D): the dock raises or lowers the Media pane's least width in place (the Filmstrip view's is wider).</summary>
    public event Action<MediaBrowserView>? ViewModeChanged;

    /// <summary>The list's template and panel for the view, the header shown or not, and the columns laid out.</summary>
    private void ApplyView(MediaBrowserView? was)
    {
        // Both panels make only what is in view (the tiles' panel scrolls itself by pixels); the List and Filmstrip views share theirs.
        var (template, panel) = _view switch
        {
            MediaBrowserView.List => ("DetailRowTemplate", "RowPanel"),
            MediaBrowserView.Filmstrip => ("CompactRowTemplate", "RowPanel"),
            _ => ("TileTemplate", "TilePanel"),
        };
        Items.ItemTemplate = (DataTemplate)Resources[template];
        if (was is null || (was == MediaBrowserView.Grid) != (_view == MediaBrowserView.Grid))
            Items.ItemsPanel = (ItemsPanelTemplate)Resources[panel];
        VirtualizingPanel.SetIsVirtualizing(Items, true);
        ScrollViewer.SetCanContentScroll(Items, true);

        ListHeaderHost.Visibility = _view == MediaBrowserView.List && ListHeaderHost.Content is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_view == MediaBrowserView.List && ListHeaderHost.Content is null && _started)
            Dispatcher.BeginInvoke(BuildDeferredParts, DispatcherPriority.ContextIdle);

        LayOutColumns();
        ScheduleVisibleCheck();
    }

    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            LayOutColumns();
    }

    /// <summary>
    /// Whether the List view shows its preview at a width of the pane (<see cref="SplitPreviewFrom"/> on, below
    /// <see cref="SplitPreviewBelow"/> off, as it was in between).
    /// </summary>
    internal static bool SplitShows(bool shown, double width) => width >= (shown ? SplitPreviewBelow : SplitPreviewFrom);

    /// <summary>
    /// The Filmstrip view's list width: the one kept (240 by default), at least <see cref="FilmstripListLeastAt"/> and at most half the pane
    /// (never under that least).
    /// </summary>
    internal static double FilmstripListWidth(double kept, double paneWidth)
    {
        var least = FilmstripListLeastAt(paneWidth);
        var most = Math.Max(least, paneWidth / 2);
        return Math.Max(least, Math.Min(double.IsFinite(kept) && kept > 0 ? kept : FilmstripListDefault, most));
    }

    /// <summary>
    /// The least the Filmstrip view's list keeps in a pane this wide (5.4D): <see cref="FilmstripListLeast"/>, narrowed down to
    /// <see cref="FilmstripListNarrow"/> as the pane narrows, so the preview beside it keeps <see cref="PreviewLeast"/>.
    /// </summary>
    internal static double FilmstripListLeastAt(double paneWidth) =>
        Math.Max(FilmstripListNarrow, Math.Min(FilmstripListLeast, Math.Floor(paneWidth - SplitterWidth - PreviewLeast)));

    /// <summary>The List view's table width beside its preview: about half the pane, never under <see cref="ListKeepsAtLeast"/>.</summary>
    internal static double SplitListWidth(double paneWidth) => Math.Max(ListKeepsAtLeast, Math.Round(paneWidth * 0.55));

    /// <summary>
    /// The columns of the List view a row of <paramref name="rowWidth"/> DIPs shows, in order: every one while the name keeps
    /// 120 DIPs, else the sound streams and the codec go first, then the size, the frame rate, the resolution and the length.
    /// </summary>
    internal static IReadOnlyList<string> ColumnsShown(double rowWidth)
    {
        var shown = DetailColumns.Select(c => c.Key).ToList();
        foreach (var key in DroppedFirst)
        {
            if (ThumbColumn + NameAtLeast + DetailColumns.Where(c => shown.Contains(c.Key)).Sum(c => c.Width) <= rowWidth)
                break;
            shown.Remove(key);
        }

        return shown;
    }

    /// <summary>The preview's column, the splitter and the list's column for the view and the pane's width.</summary>
    private void LayOutColumns()
    {
        var width = Math.Min(ContentGrid.ActualWidth, _contentOffered);
        var preview = false;
        PreviewSplitter.Visibility = Visibility.Collapsed;
        (ListColumn.MinWidth, ListColumn.MaxWidth) = (0, double.PositiveInfinity);
        PreviewColumn.MinWidth = 0;
        switch (_view)
        {
            case MediaBrowserView.List:
                // (Never in the picker: it has no timeline to put a sub-clip on.)
                _splitShown = !PickerMode && width > 0 && SplitShows(_splitShown, width);
                preview = _splitShown;
                ListColumn.Width = preview ? new GridLength(SplitListWidth(width)) : new GridLength(1, GridUnitType.Star);
                SplitColumn.Width = new GridLength(0);
                break;

            case MediaBrowserView.Filmstrip:
                preview = true;
                FitFilmstripColumns(width);
                SplitColumn.Width = new GridLength(SplitterWidth);
                PreviewSplitter.Visibility = Visibility.Visible;
                break;

            default:
                ListColumn.Width = new GridLength(1, GridUnitType.Star);
                SplitColumn.Width = new GridLength(0);
                break;
        }

        PreviewColumn.Width = preview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PreviewHost.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        if (_view == MediaBrowserView.List)
            ApplyColumns((preview ? SplitListWidth(width) : width) - RowChrome - SystemParameters.VerticalScrollBarWidth);
        PreviewShownChanged(preview);
    }

    /// <summary>
    /// The Filmstrip view's list column for a width of the pane's content (5.4D): the width kept, at most half, and in a narrow pane its least
    /// given way down to 140 DIPs so the preview and cutter keep 120 beside it (PreviewColumn's least).
    /// </summary>
    private void FitFilmstripColumns(double width)
    {
        var list = FilmstripListWidth(AppSettings.Current.BrowserFilmstripListWidth, width);
        var least = FilmstripListLeastAt(width);
        (ListColumn.MinWidth, ListColumn.MaxWidth) = (least, Math.Max(least, width / 2));
        PreviewColumn.MinWidth = PreviewLeast;
        if (ListColumn.Width.GridUnitType != GridUnitType.Pixel || Math.Abs(ListColumn.Width.Value - list) > 0.5)
            ListColumn.Width = new GridLength(list);
    }

    // The width the pane last offered its content (5.4D): the content's grid is never laid out narrower than its columns' least widths
    // together, so its own width alone would never let the Filmstrip list give way. Unbounded until a measure says.
    private double _contentOffered = double.PositiveInfinity;

    /// <summary>
    /// Measured for a width (5.4D): in the Filmstrip view, the list's least fitted to the width offered (less what lies round the content's
    /// grid) before the grid is measured, so a pane narrowed to the Media column's least (280) lays the list and the preview out inside it.
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsFinite(constraint.Width))
        {
            var offered = Math.Max(0, constraint.Width - Math.Max(0, ActualWidth - ContentGrid.ActualWidth));
            if (Math.Abs(offered - _contentOffered) > 0.5)
            {
                _contentOffered = offered;
                if (_view == MediaBrowserView.Filmstrip)
                    FitFilmstripColumns(offered);
            }
        }

        return base.MeasureOverride(constraint);
    }

    /// <summary>The splitter let go: the list's width is kept (BrowserFilmstripListWidth), and laid out from it from now on.</summary>
    private void PreviewSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var width = Math.Round(ListColumn.ActualWidth);
        if (!PickerMode && width >= FilmstripListNarrow)
            AppSettings.Current.BrowserFilmstripListWidth = width;
    }

    /// <summary>
    /// A double-click on the splitter (5.4D): the list back to its 240 DIPs (as wide as the pane lets it be), kept. Taken on the press, so
    /// the second click never starts a drag whose end would keep the old width.
    /// </summary>
    private void PreviewSplitter_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || PickerMode)
            return;

        e.Handled = true;
        AppSettings.Current.BrowserFilmstripListWidth = FilmstripListDefault;
        LayOutColumns();
    }

    /// <summary>Each column of the List view as wide as it stands, or 0 where the row is too narrow for it (the header's and the rows' alike).</summary>
    private void ApplyColumns(double rowWidth)
    {
        var shown = ColumnsShown(rowWidth);
        foreach (var column in DetailColumns)
        {
            var wanted = new GridLength(shown.Contains(column.Key) ? column.Width : 0);
            if (Resources[column.Resource] is not GridLength now || now != wanted)
                Resources[column.Resource] = wanted;
        }
    }

    // ----- The List view's header (made at first need) -----

    private Grid? _header;
    private readonly Dictionary<string, TextBlock> _headerGlyphs = [];

    /// <summary>
    /// The parts of the views made at first need, after the first frame (5.4C, Law 1): the List view's header and, where the view shows
    /// it, the preview and the cutter.
    /// </summary>
    private void BuildDeferredParts()
    {
        if (_view == MediaBrowserView.List && _header is null)
            BuildHeader();
        if (PreviewHost.Visibility == Visibility.Visible)
            EnsurePreview();
    }

    /// <summary>The header row: a cell per column, as wide as the rows' (the same resources); a click sorts by it, again the other way.</summary>
    private void BuildHeader()
    {
        var header = new Grid { Height = 22, Margin = new Thickness(0, 0, 0, 2) };
        void Column(string? resource, double star = 0)
        {
            var definition = new ColumnDefinition();
            if (resource is null)
                definition.Width = new GridLength(star > 0 ? star : 1, GridUnitType.Star);
            else
                definition.SetResourceReference(ColumnDefinition.WidthProperty, resource);
            header.ColumnDefinitions.Add(definition);
        }

        Column("ColThumb");
        Column(null);
        foreach (var column in DetailColumns)
            Column(column.Resource);

        void Cell(int index, string label, string key)
        {
            var glyph = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, Margin = new Thickness(3, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = label, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var row = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = $"Sort by {label.ToLowerInvariant()} (again: the other way)" };
            DockPanel.SetDock(glyph, Dock.Right);
            row.Children.Add(glyph);
            row.Children.Add(text);
            System.Windows.Automation.AutomationProperties.SetName(row, $"Sort by {label}");
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SortByColumn(key);
            };
            Grid.SetColumn(row, index);
            header.Children.Add(row);
            _headerGlyphs[key] = glyph;
        }

        Cell(1, "File Name", "Name");
        for (var i = 0; i < DetailColumns.Length; i++)
            Cell(i + 2, DetailColumns[i].Header, DetailColumns[i].Key);

        _header = header;
        ListHeaderHost.Content = header;
        ListHeaderHost.Visibility = _view == MediaBrowserView.List ? Visibility.Visible : Visibility.Collapsed;
        Items.SizeChanged += (_, _) => FitHeaderToRows();
        Items.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.ViewportWidthChange != 0)
                FitHeaderToRows();
        }));
        ShowSortInHeader();
        Dispatcher.BeginInvoke(FitHeaderToRows, DispatcherPriority.Loaded);
    }

    /// <summary>The header's edges where the rows' cells begin and end: inside the list's border and an item's padding, short of its scroll bar.</summary>
    private void FitHeaderToRows()
    {
        if (_header is null || Scroller is not { } scroller || Items.ActualWidth <= 0)
            return;

        try
        {
            var left = scroller.TranslatePoint(new Point(0, 0), Items).X;
            var right = Math.Max(Items.ActualWidth - left - scroller.ViewportWidth, 0);
            _header.Margin = new Thickness(left + RowChrome / 2, 0, right + RowChrome / 2, 2);
        }
        catch (InvalidOperationException)
        {
            // Not laid out together yet.
        }
    }

    /// <summary>A column header clicked: the files sorted by it, ascending; the same one again, the other way.</summary>
    private void SortByColumn(string key)
    {
        var descending = string.Equals(_sortKey, key, StringComparison.Ordinal) && SortDirectionButton.IsChecked != true;
        _sortKey = key;
        if (!PickerMode)
            AppSettings.Current.BrowserSort = key;
        _syncingSort = true;
        try
        {
            SortBox.SelectedIndex = Array.IndexOf(SortKeys, key);
        }
        finally
        {
            _syncingSort = false;
        }

        if (SortDirectionButton.IsChecked == descending)
        {
            ShowSortInHeader();
            if (_items.Count > 0)
                Resort();
        }
        else
        {
            // Its handler sorts again and keeps the direction.
            SortDirectionButton.IsChecked = descending;
        }
    }

    /// <summary>The sorting column's arrow in the header (up ascending, down descending); none on the others.</summary>
    private void ShowSortInHeader()
    {
        var descending = SortDirectionButton.IsChecked == true;
        foreach (var (key, glyph) in _headerGlyphs)
            glyph.Text = key == _sortKey ? descending ? "" : "" : "";
    }

    // ----- The details of the rows in sight (List and Filmstrip) -----

    private readonly List<MediaBrowserItem> _detailsWanted = [];
    private bool _readingDetails;

    /// <summary>Asks for the details of the files in sight, in the List and Filmstrip views only (the Grid shows none).</summary>
    private void WantDetails()
    {
        if (_view == MediaBrowserView.Grid)
            return;

        foreach (var item in _items)
        {
            if (_inSight.Contains(item) && item.IsFile && !item.DetailsRequested && !_detailsWanted.Contains(item))
                _detailsWanted.Add(item);
        }

        if (!_readingDetails && _detailsWanted.Count > 0)
            _ = ReadDetailsAsync();
    }

    /// <summary>
    /// Reads the details asked for, one file at a time, those still in sight first, away from the interface (MediaBrowserFrames: the
    /// cache, else ffprobe in the Background lane), never while a drag and drop holds background work.
    /// </summary>
    private async Task ReadDetailsAsync()
    {
        _readingDetails = true;
        var token = _listing.Token;
        try
        {
            while (_detailsWanted.Count > 0 && !token.IsCancellationRequested)
            {
                while (WorkScheduler.IsHeld)
                    await Task.Delay(HeldPollMilliseconds, token);

                var item = _detailsWanted.FirstOrDefault(_inSight.Contains);
                if (item is null)
                {
                    // Scrolled out of sight before their turn: asked for again when they come back.
                    _detailsWanted.Clear();
                    break;
                }

                _detailsWanted.Remove(item);
                item.DetailsRequested = true;
                if (await MediaBrowserFrames.DetailsAsync(item.Path, token) is { } details)
                    item.TakeDetails(details.Seconds, details.Width, details.Height, details.FrameRate, details.Codec, details.AudioStreams);
                else
                    item.DetailsKnown = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"Media browser: details: {ex.Message}");
        }
        finally
        {
            _readingDetails = false;
        }

        if (_detailsWanted.Count > 0 && !_listing.IsCancellationRequested)
            _ = ReadDetailsAsync();
    }
}
