using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.Views;

/// <summary>Media Browser layout: tiles, a details table, or a preview/cutter beside a list.</summary>
public enum MediaBrowserView { Grid, List, Filmstrip }

// ----- Media Browser views -----
// One list (Items) for all three views; only its template and panel swap, so thumbnails, hover scrub, selection and drag behave alike.
// Grid: tiles filling the pane. List: a sortable details table that drops low-priority columns when narrow and shows the preview/cutter
// beside it from 600 DIPs. Filmstrip: preview and sub-clip cutter on the left, list column on the right (BrowserFilmstripListWidth),
// splitter between. Law 1: the pane exists before Frame 1, so the constructor only sets column widths; header, preview and cutter build
// after Frame 1 (BuildDeferredParts, ContextIdle), and details load off the UI thread only for visible rows.
public partial class MediaBrowserPane
{
    /// <summary>List view shows its preview at or above this pane width (DIPs)...</summary>
    internal const double SplitPreviewFrom = 600;

    /// <summary>...and hides it below this one (hysteresis so splitter drags across the threshold never flicker).</summary>
    internal const double SplitPreviewBelow = 576;

    /// <summary>Minimum List table width beside the preview (DIPs).</summary>
    internal const double ListKeepsAtLeast = 340;

    /// <summary>Filmstrip list minimum width in DIPs (maximum is half the pane)...</summary>
    internal const double FilmstripListLeast = 180;

    /// <summary>...relaxed to this in a narrow pane so the preview keeps <see cref="PreviewLeast"/>.</summary>
    internal const double FilmstripListNarrow = 140;

    /// <summary>Minimum Filmstrip preview/cutter width beside the list (DIPs).</summary>
    internal const double PreviewLeast = 120;

    /// <summary>Default Filmstrip list width, also restored by double-clicking the splitter (DIPs).</summary>
    internal const double FilmstripListDefault = 240;

    private const double SplitterWidth = 5;

    // List detail columns after Name, in display order: resource key, sort key, header and width.
    internal static readonly (string Resource, string Key, string Header, double Width)[] DetailColumns =
    [
        ("ColResolution", "Resolution", "Resolution", 84),
        ("ColFramerate", "Framerate", "Framerate", 66),
        ("ColDuration", "Duration", "Duration", 62),
        ("ColCodec", "Codec", "Codec", 58),
        ("ColAudio", "Audio", "Audio", 48),
        ("ColSize", "Size", "Size", 70),
    ];

    // Column drop order as the table narrows (first dropped first).
    private static readonly string[] DroppedFirst = ["Audio", "Codec", "Size", "Framerate", "Resolution", "Duration"];

    // DIPs: thumbnail column, minimum name width, and per-row chrome (item border and padding).
    private const double ThumbColumn = 72, NameAtLeast = 120, RowChrome = 16;

    private MediaBrowserView _view;
    private bool _viewSet, _splitShown, _syncingView, _syncingSort;
    private double _tileSize;
    private string _sortKey = "Name";

    /// <summary>Current view; setting switches immediately and, outside picker mode, persists to
    /// <see cref="Services.AppSettings.BrowserView"/>.</summary>
    public MediaBrowserView ViewMode
    {
        get => _view;
        set => SetView(value, remember: true);
    }

    /// <summary>
    /// Parses a saved <see cref="AppSettings.BrowserView"/>; empty or unknown falls back to List when the legacy tile size is below
    /// <see cref="ListBelow"/>, else Grid.
    /// </summary>
    internal static MediaBrowserView ViewOf(string? saved, int tileSize) =>
        Enum.TryParse<MediaBrowserView>(saved?.Trim(), ignoreCase: true, out var view) && Enum.IsDefined(view) ? view
        : tileSize < ListBelow ? MediaBrowserView.List : MediaBrowserView.Grid;

    /// <summary>
    /// Tile size 0..100 (tab menu Tile Size): below <see cref="ListBelow"/> selects List, otherwise Grid at that size. Persisted to
    /// BrowserTileSize outside picker mode.
    /// </summary>
    public double TileSize
    {
        get => _tileSize;
        set
        {
            _tileSize = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);
            if (!PickerMode)
                AppSettings.Set(s => s.BrowserTileSize, (s, v) => s.BrowserTileSize = v, (int)Math.Round(_tileSize));
            ApplyTileWidth(_tileSize);
            SetView(_tileSize < ListBelow ? MediaBrowserView.List : MediaBrowserView.Grid, remember: true);

            // Thumbnails scale immediately; once the size settles, upscaled ones are regenerated at the new size.
            _sizeSettled.Stop();
            _sizeSettled.Start();
        }
    }

    /// <summary>Constructor-time sort and view from settings (picker mode excludes Filmstrip: no timeline target).</summary>
    private void InitializeView(AppSettings settings)
    {
        _tileSize = Math.Clamp(settings.BrowserTileSize, 0, 100);
        ApplyTileWidth(_tileSize);
        if (PickerMode)
            FilmstripChoice.Visibility = Visibility.Collapsed;
        SetView(ViewOf(settings.BrowserView, settings.BrowserTileSize), remember: false);
    }

    /// <summary>Restores the saved sort: a Sort box key, or a List column key (Sort box then shows no selection).</summary>
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

    /// <summary>Switches view (picker mode maps Filmstrip to Grid); persists when <paramref name="remember"/>.</summary>
    private void SetView(MediaBrowserView view, bool remember)
    {
        if (!Enum.IsDefined(view) || (PickerMode && view == MediaBrowserView.Filmstrip))
            view = MediaBrowserView.Grid;
        if (remember && !PickerMode)
            AppSettings.Set(s => s.BrowserView, (s, v) => s.BrowserView = v, view.ToString());
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

    /// <summary>Raised on view change; the dock adjusts the Media pane's minimum width in place (Filmstrip needs more).</summary>
    public event Action<MediaBrowserView>? ViewModeChanged;

    /// <summary>Applies the view's item template and panel, header visibility and column layout.</summary>
    private void ApplyView(MediaBrowserView? was)
    {
        // Both panels virtualize (the tile panel scrolls by pixel); List and Filmstrip share the row panel.
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
    /// List preview visibility with hysteresis: on at <see cref="SplitPreviewFrom"/>, off below <see cref="SplitPreviewBelow"/>, unchanged
    /// in between.
    /// </summary>
    internal static bool SplitShows(bool shown, double width) => width >= (shown ? SplitPreviewBelow : SplitPreviewFrom);

    /// <summary>
    /// Filmstrip list width: the saved width (default 240) clamped to [<see cref="FilmstripListLeastAt"/>, half the pane], the minimum
    /// winning.
    /// </summary>
    internal static double FilmstripListWidth(double kept, double paneWidth)
    {
        var least = FilmstripListLeastAt(paneWidth);
        var most = Math.Max(least, paneWidth / 2);
        return Math.Max(least, Math.Min(double.IsFinite(kept) && kept > 0 ? kept : FilmstripListDefault, most));
    }

    /// <summary>
    /// Filmstrip list minimum for a pane width: <see cref="FilmstripListLeast"/>, relaxed to <see cref="FilmstripListNarrow"/> as the pane
    /// narrows so the preview keeps <see cref="PreviewLeast"/>.
    /// </summary>
    internal static double FilmstripListLeastAt(double paneWidth) =>
        Math.Max(FilmstripListNarrow, Math.Min(FilmstripListLeast, Math.Floor(paneWidth - SplitterWidth - PreviewLeast)));

    /// <summary>List table width beside the preview: 55% of the pane, at least <see cref="ListKeepsAtLeast"/>.</summary>
    internal static double SplitListWidth(double paneWidth) => Math.Max(ListKeepsAtLeast, Math.Round(paneWidth * 0.55));

    /// <summary>
    /// Detail columns that fit in <paramref name="rowWidth"/> DIPs while Name keeps 120: drops Audio, Codec, Size, Framerate, Resolution,
    /// then Duration as needed.
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

    /// <summary>Lays out the preview column, splitter and list column for the current view and pane width.</summary>
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
                // Never in picker mode (no timeline for sub-clips).
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
    /// Sets Filmstrip column constraints for a content width: saved list width up to half, with the list minimum relaxing to 140 DIPs in
    /// narrow panes so the preview keeps 120 (PreviewColumn.MinWidth).
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

    // Width last offered to the content. ContentGrid never lays out below the sum of its column minimums, so its ActualWidth alone would
    // never let the Filmstrip list shrink. Infinite until the first measure.
    private double _contentOffered = double.PositiveInfinity;

    /// <summary>
    /// In Filmstrip view, fits column minimums to the offered width (minus chrome around ContentGrid) before measuring, so a pane at the
    /// Media column minimum (280) still lays out list and preview inside it.
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

    /// <summary>Splitter release: persists the list width (BrowserFilmstripListWidth) for future layouts.</summary>
    private void PreviewSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var width = Math.Round(ListColumn.ActualWidth);
        if (!PickerMode && width >= FilmstripListNarrow)
            AppSettings.Set(s => s.BrowserFilmstripListWidth, (s, v) => s.BrowserFilmstripListWidth = v, width);
    }

    /// <summary>
    /// Splitter double-click: resets and persists the list width to 240 DIPs (pane permitting). Handled on press so the second click never
    /// starts a drag whose completion would save the old width.
    /// </summary>
    private void PreviewSplitter_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || PickerMode)
            return;

        e.Handled = true;
        AppSettings.Set(s => s.BrowserFilmstripListWidth, (s, v) => s.BrowserFilmstripListWidth = v, FilmstripListDefault);
        LayOutColumns();
    }

    /// <summary>Sets each detail column's shared GridLength resource (header and rows): its width, or 0 when it does not fit.</summary>
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

    // ----- List header (built lazily) -----

    private Grid? _header;
    private readonly Dictionary<string, TextBlock> _headerGlyphs = [];

    /// <summary>
    /// Builds lazily created parts after Frame 1 (Law 1): the List header and, when the view shows it, the preview and cutter.
    /// </summary>
    private void BuildDeferredParts()
    {
        if (_view == MediaBrowserView.List && _header is null)
            BuildHeader();
        if (PreviewHost.Visibility == Visibility.Visible)
            EnsurePreview();
    }

    /// <summary>Builds the header: one cell per column sharing the rows' width resources; click sorts, click again reverses.</summary>
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

    /// <summary>Aligns header margins with row cells: inside the list border and item padding, excluding the scroll bar.</summary>
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
            // Not yet in a common visual tree.
        }
    }

    /// <summary>Header click: sort ascending by the column; same column again toggles direction.</summary>
    private void SortByColumn(string key)
    {
        var descending = string.Equals(_sortKey, key, StringComparison.Ordinal) && SortDirectionButton.IsChecked != true;
        _sortKey = key;
        if (!PickerMode)
            AppSettings.Set(s => s.BrowserSort, (s, v) => s.BrowserSort = v, key);
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
            // The direction button's handler re-sorts with the new direction.
            SortDirectionButton.IsChecked = descending;
        }
    }

    /// <summary>Shows the sort arrow on the active column only (up = ascending, down = descending).</summary>
    private void ShowSortInHeader()
    {
        var descending = SortDirectionButton.IsChecked == true;
        foreach (var (key, glyph) in _headerGlyphs)
            glyph.Text = key == _sortKey ? descending ? Glyphs.ChevronDown : Glyphs.ChevronUp : "";
    }

    // ----- Visible-row details (List and Filmstrip) -----

    private readonly List<MediaBrowserItem> _detailsWanted = [];
    private bool _readingDetails;

    /// <summary>Queues detail reads for visible files (List and Filmstrip only; Grid shows no details).</summary>
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
    /// Reads queued details one file at a time, visible first, off the UI thread (MediaBrowserFrames: cache, else ffprobe on the Background
    /// lane); pauses while drag-and-drop holds background work.
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
                    // Remaining items scrolled out of view; they are re-queued when visible again.
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
            AppLog.Write("Media browser: details", ex);
        }
        finally
        {
            _readingDetails = false;
        }

        if (_detailsWanted.Count > 0 && !_listing.IsCancellationRequested)
            _ = ReadDetailsAsync();
    }
}
