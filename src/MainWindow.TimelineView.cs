using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Models;

namespace HandPegApp;

// ----- The time bars' view: which stretch of the sequence they show -----
// Every time bar (the Layers tab's and the Audio tab's) shows the same stretch of the sequence: from _viewStart
// seconds, ViewSpan seconds across. The timeline reaches a little past the end of the sequence, so that there
// is room to drop a clip after the last one, or drag one there, and the sequence grows to hold it. Fitted (the
// default), the bars show all of that and follow the sequence as it grows; zoomed, a second keeps its width, and
// the ruler, the scrollbar under the bars, Shift + wheel or a middle-button drag move along. Ctrl + wheel over a
// bar, or the wheel over the ruler, zoom about the pointer; the buttons and the slider about the playhead.
// The master timeline follows when "Sync Master Timeline Stretch" is on in the Views menu.
public partial class MainWindow
{
    /// <summary>The least the time bars can show across, in seconds: a few frames each wide enough to grab.</summary>
    private const double MinViewSpan = 0.5;

    /// <summary>What the rulers show across while the timeline is empty, in seconds (4.3E: a ruler is always shown, with its ticks).</summary>
    private const double EmptyRulerSeconds = 60;

    /// <summary>The room between the zoom buttons and a ruler that has no time bar to line up with yet, in pixels.</summary>
    private const double RulerGap = 6;

    private double _viewStart, _viewSpan;
    private bool _viewFit = true;

    // The rulers, scrollbars and zoom sliders of the tabs that have been made (they are made with their tab).
    private readonly List<TimelineRuler> _rulers = [];
    private readonly List<ScrollBar> _timelineScrollBars = [];
    private readonly List<Slider> _zoomSliders = [];
    private bool _showingView;
    private bool _chromeAlignQueued;

    /// <summary>How far the time bars reach, in seconds: the sequence, and room after it to drop and drag clips into.</summary>
    private double TimelineExtent
    {
        get
        {
            var seconds = _viewModel.DurationMs / 1000;
            return seconds <= 0 ? 0 : seconds + Math.Max(seconds * 0.12, 5);
        }
    }

    /// <summary>How many seconds the time bars show across.</summary>
    private double ViewSpan
    {
        get
        {
            var extent = TimelineExtent;
            return _viewFit || _viewSpan <= 0 || _viewSpan >= extent ? extent : Math.Max(_viewSpan, Math.Min(MinViewSpan, extent));
        }
    }

    /// <summary>The moment of the sequence at a point along a time bar (or anything as wide and placed as one).</summary>
    private double TimeAt(FrameworkElement bar, double x) => bar.ActualWidth > 0 ? _viewStart + x / bar.ActualWidth * ViewSpan : 0;

    /// <summary>How far along the zoom is, 0 (the whole timeline) to 1 (the closest): what the sliders show.</summary>
    private double ZoomLevel
    {
        get
        {
            var extent = TimelineExtent;
            return extent <= MinViewSpan ? 0 : Math.Clamp(Math.Log(extent / ViewSpan) / Math.Log(extent / MinViewSpan), 0, 1);
        }
    }

    /// <summary>Shows a stretch of the timeline: from <paramref name="start"/>, <paramref name="span"/> seconds across (all of it, at its longest).</summary>
    private void SetView(double start, double span)
    {
        var extent = TimelineExtent;
        if (extent <= 0)
            return;

        span = Math.Clamp(span, Math.Min(MinViewSpan, extent), extent);
        _viewFit = span >= extent - 1e-6;
        (_viewStart, _viewSpan) = (_viewFit ? 0 : start, span);
        ApplyTimelineView();
    }

    /// <summary>Zooms by a factor (above 1 is closer) about a point of the bars, given as a fraction of their width: what is there stays there.</summary>
    private void ZoomTimeline(double factor, double atFraction)
    {
        var span = ViewSpan;
        if (span <= 0)
            return;

        var under = _viewStart + atFraction * span;
        var next = span / factor;
        SetView(under - atFraction * next, next);
    }

    /// <summary>Zooms about the playhead while it is in view, and about the middle otherwise.</summary>
    private void ZoomAboutPlayhead(double factor)
    {
        var span = ViewSpan;
        if (span <= 0)
            return;

        var fraction = (_viewModel.PositionMs / 1000 - _viewStart) / span;
        ZoomTimeline(factor, fraction is >= 0 and <= 1 ? fraction : 0.5);
    }

    private void PanTimeline(double seconds)
    {
        if (_viewFit || Math.Abs(seconds) < 1e-9)
            return;

        _viewStart += seconds;
        ApplyTimelineView(announce: false);
    }

    /// <summary>Back to the whole timeline, as a newly loaded video starts.</summary>
    private void FitTimeline(bool announce = true)
    {
        (_viewFit, _viewStart, _viewSpan) = (true, 0, 0);
        ApplyTimelineView(announce);
    }

    private void ClipTrack_ZoomRequested(TimelineTrack track, double factor, double x)
    {
        if (track.ActualWidth > 0)
            ZoomTimeline(factor, x / track.ActualWidth);
    }

    private void ClipTrack_PanRequested(TimelineTrack track, double pixels)
    {
        if (track.ActualWidth > 0)
            PanTimeline(pixels / track.ActualWidth * ViewSpan);
    }

    /// <summary>Keeps the view inside the timeline, and draws the bars and everything around them for it.</summary>
    private void ApplyTimelineView(bool announce = true)
    {
        ClampView();
        foreach (var track in _clipTracks.ToList())
            DrawTrack(track);
        UpdateTimelineChrome();
        UpdateMasterStretch();
        DrawKeyTimeline();
        if (!announce)
            return;

        var extent = TimelineExtent;
        _viewModel.StatusText = _viewFit || extent <= 0
            ? "The time bars show the whole sequence, and room after it to drop clips into (Ctrl + wheel over a bar, or the wheel over the ruler, zooms)."
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Time bars zoomed {extent / ViewSpan:0.#}x: {TimeDisplay.Format(_viewStart)} to {TimeDisplay.Format(_viewStart + ViewSpan)} (Shift + wheel, the scrollbar or a middle-button drag moves along; \\ fits).");
    }

    private void ClampView()
    {
        var extent = TimelineExtent;
        if (_viewFit || extent <= 0)
        {
            _viewStart = 0;
            return;
        }

        _viewStart = Math.Clamp(_viewStart, 0, Math.Max(extent - ViewSpan, 0));
    }

    /// <summary>Brings the rulers, the scrollbars and the zoom sliders up to date with the view.</summary>
    private void UpdateTimelineChrome()
    {
        ClampView();
        var (extent, span, sequence) = (TimelineExtent, ViewSpan, _viewModel.DurationMs / 1000);
        _showingView = true;
        try
        {
            // With nothing on the timeline yet the rulers still read as rulers (4.3E): a minute's ticks, all of it shaded as room to drop clips into.
            var markers = _viewModel.Markers.Select(m => m.Seconds).ToList();
            foreach (var ruler in _rulers)
            {
                ruler.Show(_viewStart, span > 0 ? span : EmptyRulerSeconds, sequence, markers);
                PlaceRulerPlayhead(ruler);
            }

            foreach (var bar in _timelineScrollBars)
            {
                var room = Math.Max(extent - span, 0);
                (bar.Minimum, bar.Maximum, bar.ViewportSize) = (0, room, Math.Max(span, 0.001));
                (bar.LargeChange, bar.SmallChange) = (span * 0.9, span / 20);
                bar.Value = Math.Clamp(_viewStart, 0, room);
                bar.IsEnabled = room > 1e-3;
            }

            foreach (var slider in _zoomSliders)
                slider.Value = ZoomLevel;
        }
        finally
        {
            _showingView = false;
        }

        ScheduleChromeAlign();
    }

    private void PlaceRulerPlayhead(TimelineRuler ruler)
    {
        var span = ViewSpan;
        ruler.PlayheadX = span > 0 && _viewModel.DurationMs > 0 ? (_viewModel.PositionMs / 1000 - _viewStart) / span * ruler.ActualWidth : double.NaN;
    }

    /// <summary>
    /// While a video plays with the bars zoomed in, the view turns the page when the playhead reaches its right
    /// edge, so that what is playing stays in sight.
    /// </summary>
    private void FollowPlayhead()
    {
        if (_viewFit || !_viewModel.IsPlaying || _isScrubbing)
            return;

        var (at, span) = (_viewModel.PositionMs / 1000, ViewSpan);
        if (at > _viewStart + span || at < _viewStart)
        {
            _viewStart = at - span * 0.05;
            ApplyTimelineView(announce: false);
        }
    }

    // ----- The ruler, the scrollbar and the zoom controls, as their tabs make them -----

    private void TimelineRuler_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TimelineRuler ruler || _rulers.Contains(ruler))
            return;

        _rulers.Add(ruler);
        ruler.SeekRequested += seconds =>
        {
            if (_viewModel.DurationMs > 0)
                _viewModel.PositionMs = Math.Clamp(seconds * 1000, 0, _viewModel.DurationMs);
        };
        ruler.ZoomRequested += (factor, x) => ZoomTimeline(factor, ruler.ActualWidth > 0 ? x / ruler.ActualWidth : 0.5);
        ruler.PanRequested += pixels => PanTimeline(ruler.ActualWidth > 0 ? pixels / ruler.ActualWidth * ViewSpan : 0);
        ruler.MarkerMenuRequested += index =>
        {
            if (index < _viewModel.Markers.Count)
                ShowMarkerMenu(ruler, _viewModel.Markers[index]);
        };
        ruler.SizeChanged += (_, _) => PlaceRulerPlayhead(ruler);
        UpdateTimelineChrome();
    }

    private void TimelineRuler_Unloaded(object sender, RoutedEventArgs e)
    {
        // Left registered: a tab that is switched away from is unloaded and loaded again, with the same ruler.
    }

    private void TimelineScroll_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollBar bar && !_timelineScrollBars.Contains(bar))
        {
            _timelineScrollBars.Add(bar);
            UpdateTimelineChrome();
        }
    }

    private void TimelineScroll_Unloaded(object sender, RoutedEventArgs e)
    {
    }

    private void TimelineScroll_Scroll(object sender, ScrollEventArgs e)
    {
        if (_showingView || _viewFit || sender is not ScrollBar bar)
            return;

        _viewStart = bar.Value;
        ApplyTimelineView(announce: false);
    }

    private void TimelineZoomSlider_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Slider slider && !_zoomSliders.Contains(slider))
        {
            _zoomSliders.Add(slider);
            UpdateTimelineChrome();
        }
    }

    private void TimelineZoomSlider_Unloaded(object sender, RoutedEventArgs e)
    {
    }

    private void TimelineZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_showingView || _viewModel is null)
            return;

        var extent = TimelineExtent;
        if (extent <= 0)
            return;

        var span = extent * Math.Pow(Math.Min(MinViewSpan, extent) / extent, Math.Clamp(e.NewValue, 0, 1));
        ZoomAboutPlayhead(ViewSpan / span);
    }

    private void TimelineZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAboutPlayhead(1.6);

    private void TimelineZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAboutPlayhead(1 / 1.6);

    private void TimelineZoomFit_Click(object sender, RoutedEventArgs e) => FitTimeline();

    /// <summary>Lines the rulers and the scrollbars up with the time bars, once the layout has settled.</summary>
    private void ScheduleChromeAlign()
    {
        if (_chromeAlignQueued)
            return;

        _chromeAlignQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _chromeAlignQueued = false;
            AlignTimelineChrome();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Each ruler and scrollbar is placed over (or under) the time bars of its own tab: as far in from the left as
    /// the bars begin, and as wide as they are. The bars of one tab all span the same stretch of the screen (the
    /// controls beside them share one width), so any of them that is on screen will do. A ruler is always shown (4.3E):
    /// with no bar on screen to line up with (no tracks yet, or every row scrolled away), it keeps where it was; a
    /// scrollbar with no bars under it is hidden.
    /// </summary>
    private void AlignTimelineChrome()
    {
        foreach (var element in _rulers.Cast<FrameworkElement>().Concat(_timelineScrollBars))
        {
            if (element.Parent is not FrameworkElement parent || Ancestor<DockPanel>(element) is not { } tab)
                continue;


            var track = _clipTracks.FirstOrDefault(t => t is { IsVisible: true, ActualWidth: > 0 } && tab.IsAncestorOf(t));
            if (track is null || !parent.IsAncestorOf(element))
            {
                if (element is TimelineRuler ruler)
                    KeepRulerShown(ruler, parent);
                else
                    element.Visibility = Visibility.Hidden;
                continue;
            }

            var left = track.TranslatePoint(new Point(0, 0), parent).X;
            var right = Math.Max(parent.ActualWidth - left - track.ActualWidth, 0);
            var margin = new Thickness(Math.Max(left, 0), element.Margin.Top, right, element.Margin.Bottom);
            if (element.Margin != margin)
                element.Margin = margin;
            element.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// A ruler with no time bar on screen to line up with stays shown (4.3E): where it was last lined up, or, before it ever
    /// was (a tab that has had no tracks yet), just after the zoom buttons that share its row, so it never covers them.
    /// </summary>
    private static void KeepRulerShown(TimelineRuler ruler, FrameworkElement parent)
    {
        if (ruler.Margin.Left <= 0 && parent is Panel row && row.Children.OfType<StackPanel>().FirstOrDefault() is { ActualWidth: > 0 } buttons)
            ruler.Margin = new Thickness(buttons.ActualWidth + RulerGap, ruler.Margin.Top, 0, ruler.Margin.Bottom);
        ruler.Visibility = Visibility.Visible;
    }

    private static T? Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var at = VisualTreeHelper.GetParent(element); at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is T found)
                return found;
        }

        return null;
    }

    /// <summary>Stretches the master timeline to the time bars' view and slides it to their place, while that is switched on; otherwise it fills its place as it always did.</summary>
    private void UpdateMasterStretch()
    {
        var (seconds, span) = (_viewModel.DurationMs / 1000, ViewSpan);
        if (!_viewModel.SyncMasterTimeline || _viewFit || seconds <= 0 || span <= 0 || MasterTimelineViewport.ActualWidth <= 0)
        {
            (MasterTimelineContent.Width, MasterTimelineContent.HorizontalAlignment, MasterTimelineContent.Margin) = (double.NaN, HorizontalAlignment.Stretch, new Thickness(0));
            return;
        }

        var viewport = MasterTimelineViewport.ActualWidth;
        MasterTimelineContent.HorizontalAlignment = HorizontalAlignment.Left;
        MasterTimelineContent.Width = viewport * seconds / span;
        MasterTimelineContent.Margin = new Thickness(-_viewStart / span * viewport, 0, 0, 0);
    }

    private void MasterTimelineViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMasterStretch();

    // ----- Snapping -----

    /// <summary>
    /// What a dragged or dropped clip snaps to, in seconds: the start and the end of every clip on the timeline (the
    /// main video's, the layers', the sound clips'), the start and end of the sequence, and the playhead. Nothing
    /// while snapping is off.
    /// </summary>
    private IReadOnlyList<double> SnapTimes()
    {
        if (!_viewModel.SnapClips)
            return [];

        var sequence = _viewModel.DurationMs / 1000;
        var times = new List<double> { 0, sequence, _viewModel.PositionMs / 1000 };
        foreach (var clip in _viewModel.GetMainClips())
            times.AddRange([clip.Start, clip.End]);
        foreach (var layer in _viewModel.Layers.Where(l => l.HasTiming && !l.IsDeleted))
        {
            var (from, to) = layer.GetSpan(sequence);
            times.AddRange([from, to]);
        }

        times.AddRange(_viewModel.Markers.Select(m => m.Seconds));
        return times;
    }

    /// <summary>A moment moved onto the nearest one to snap to, when that is within a few pixels of a bar; as it was otherwise.</summary>
    private double SnapTime(double seconds, FrameworkElement bar)
    {
        var perPixel = bar.ActualWidth > 0 ? ViewSpan / bar.ActualWidth : 0;
        if (perPixel <= 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return seconds;

        var (best, nearest) = (seconds, TimelineTrack.SnapPixels * 1.5 * perPixel);
        foreach (var time in SnapTimes())
        {
            if (Math.Abs(time - seconds) < nearest)
                (best, nearest) = (time, Math.Abs(time - seconds));
        }

        return best;
    }

    // ----- Dropping files on the time bars -----

    /// <summary>
    /// Where a file being dragged over the Layers or Audio tab would land: the row it is over (null for none), the
    /// time bar it is over, and the moment along the bars (snapped). Beside the bars (over a row's name), the moment
    /// is the end of that row's last clip, so a file dropped on a name follows what is already there.
    /// </summary>
    private (Layer? Row, TimelineTrack? Bar, double Seconds) FindDropPlace(DragEventArgs e, FrameworkElement scope)
    {
        var point = e.GetPosition(scope);
        var hit = scope.InputHitTest(point) as DependencyObject;
        var bar = hit is null ? null : hit as TimelineTrack ?? Ancestor<TimelineTrack>(hit);
        Layer? row = null;
        for (var at = hit; at is not null && row is null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is ListBoxItem { DataContext: Layer layer })
                row = layer;
        }

        // Over no bar: measured against one of the tab's bars all the same, since they all span the same stretch.
        var reference = bar ?? _clipTracks.FirstOrDefault(t => t is { IsVisible: true, ActualWidth: > 0 } && scope.IsAncestorOf(t));
        double seconds;
        if (reference is not null && e.GetPosition(reference).X is var x && x >= 0 && x <= reference.ActualWidth)
            seconds = SnapTime(Math.Max(TimeAt(reference, x), 0), reference);
        else if (row is { HasTiming: true, IsEmptyTrack: false })
            seconds = _viewModel.GetTrackClips(row).Max(c => c.GetSpan(_viewModel.DurationMs / 1000).End);
        else
            seconds = 0;

        // Inside a clip of the row, the moment stays where it was let go: the new clip goes on a track of its own
        // there (MainViewModel.PlaceClip), never over the clip. Snapping gives the clip's end, for clips end to end.
        return (row, bar, seconds);
    }

    // The bar showing where a dragged file would land, and the moment it shows.
    private TimelineTrack? _dropMarkBar;

    /// <summary>Marks, on the bar under the pointer, where a file dragged over the timeline would land.</summary>
    private void ShowDropMark(DragEventArgs e)
    {
        TimelineTrack? bar = null;
        var seconds = 0.0;
        if (_viewModel.HasSource && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            foreach (var scope in new FrameworkElement?[] { LayersList, AudioPanel })
            {
                if (scope is not { IsVisible: true })
                    continue;

                var at = e.GetPosition(scope);
                if (at.X < 0 || at.Y < 0 || at.X > scope.ActualWidth || at.Y > scope.ActualHeight)
                    continue;

                var place = FindDropPlace(e, scope);
                (bar, seconds) = (place.Bar, place.Seconds);
                break;
            }
        }

        if (!ReferenceEquals(bar, _dropMarkBar))
            ClearDropMark();
        if (bar is null)
            return;

        _dropMarkBar = bar;
        bar.DropX = seconds * bar.ActualWidth / Math.Max(ViewSpan, 1e-6) - bar.ViewOffsetX;
    }

    private void ClearDropMark()
    {
        if (_dropMarkBar is { } bar)
            bar.DropX = double.NaN;
        _dropMarkBar = null;
    }

    // ----- Markers -----

    private static readonly Brush MarkerLineBrush = FrozenMarkerLine();

    private static Brush FrozenMarkerLine()
    {
        var brush = new SolidColorBrush(Color.FromArgb(150, 0x7C, 0xD9, 0x92));
        brush.Freeze();
        return brush;
    }

    /// <summary>The markers on the master timeline: a flag at the top and a faint line down, each answering a click (go there) and a right-click (its menu).</summary>
    private void DrawTimelineMarkers()
    {
        MarkerCanvas.Children.Clear();
        var (duration, width, height) = (_viewModel.DurationMs, MarkerCanvas.ActualWidth, MarkerCanvas.ActualHeight);
        if (duration <= 0 || width <= 0)
            return;

        foreach (var marker in _viewModel.Markers)
        {
            var x = marker.Seconds * 1000 / duration * width;
            var line = new System.Windows.Shapes.Rectangle { Width = 1, Height = Math.Max(height, 1), Fill = MarkerLineBrush, IsHitTestVisible = false };
            Canvas.SetLeft(line, Math.Round(x));
            MarkerCanvas.Children.Add(line);

            var flag = new System.Windows.Shapes.Path
            {
                Data = TimelineRuler.MarkerFlag, Fill = TimelineRuler.MarkerBrush, Cursor = Cursors.Hand,
                ToolTip = $"{marker.Name}  ({TimeDisplay.Format(marker.Seconds)})\nClick to go to it; right-click to rename or delete it.",
            };
            Canvas.SetLeft(flag, Math.Round(x) + 0.5);
            Canvas.SetTop(flag, 0);
            flag.MouseLeftButtonDown += (_, e) =>
            {
                _viewModel.PositionMs = marker.Seconds * 1000;
                e.Handled = true;
            };
            flag.MouseRightButtonUp += (_, e) =>
            {
                ShowMarkerMenu(flag, marker);
                e.Handled = true;
            };
            MarkerCanvas.Children.Add(flag);
        }
    }

    /// <summary>A marker's menu: its name (typed in place), go to it, delete it, delete them all.</summary>
    private void ShowMarkerMenu(FrameworkElement target, TimelineMarker marker)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        var renaming = false;
        AddRenameItem(menu, marker.Name, name =>
        {
            // One step of Undo for the whole rename, taken at its first keystroke.
            if (!renaming)
            {
                _viewModel.RenameMarkerStarted(marker);
                renaming = true;
            }

            _viewModel.RenameMarker(marker, name);
        });

        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add($"Go to {marker.Name}", () => _viewModel.PositionMs = marker.Seconds * 1000);
        Add("Delete Marker", () => _viewModel.RemoveMarker(_viewModel.Markers.FirstOrDefault(m => Math.Abs(m.Seconds - marker.Seconds) < 1e-6) ?? marker));
        menu.Items.Add(new Separator());
        Add("Delete All Markers", _viewModel.ClearMarkers);
        menu.IsOpen = true;
    }

    private void OnMarkersChanged()
    {
        DrawTimelineMarkers();
        UpdateTimelineChrome();
    }
}
