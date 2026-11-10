using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HandPegApp.Models;

namespace HandPegApp;

// ----- The timeline's view: which stretch of the sequence it shows -----
// The timeline (its surface and its ruler) and the Keyframes pane show the same stretch of the sequence: from _viewStart
// seconds, ViewSpan seconds across. The timeline reaches a little past the end of the sequence, so that there is room to
// drop a clip after the last one, or drag one there, and the sequence grows to hold it. Fitted (the default), it shows
// all of that and follows the sequence as it grows; zoomed, a second keeps its width, and the ruler, the scroll bar under
// the tracks, Shift + wheel or a middle-button drag move along. Ctrl + wheel over the tracks, or the wheel over the ruler,
// zoom about the pointer; the buttons and the slider about the playhead. The Sequence Overview Scrubber follows when "Sync
// Sequence Overview Scrubber Stretch" is on in the Views menu. A flick or a fast wheel coasts on (5.4C, MomentumScroller): a press
// on the surface, the ruler or the scroll bar, the playhead turning the page, or a zoom stops it.
public partial class MainWindow
{
    /// <summary>The least the time bars can show across, in seconds: a few frames each wide enough to grab.</summary>
    private const double MinViewSpan = 0.5;

    /// <summary>What the rulers show across while the timeline is empty, in seconds (4.3E: a ruler is always shown, with its ticks).</summary>
    private const double EmptyRulerSeconds = 60;

    private double _viewStart, _viewSpan;
    private bool _viewFit = true;

    // The rulers, scroll bars and zoom sliders that show the view (the timeline pane's: RegisterTimelineChrome).
    private readonly List<TimelineRuler> _rulers = [];
    private readonly List<ScrollBar> _timelineScrollBars = [];
    private readonly List<Slider> _zoomSliders = [];
    private bool _showingView;

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
        // Never while something is dragged on the timeline (the wheel, the keys, the buttons): the drag goes on at the scale
        // it began at, and lands where it is drawn (a clip's commit works in the pixels of the rows it was dragged on). What is
        // asked for meanwhile is done as it is let go (Timeline_DragEnded).
        var span = ViewSpan;
        if (span <= 0)
            return;

        if (UnifiedTimeline.Surface.IsDragging)
        {
            _zoomOwed = ((_zoomOwed?.Factor ?? 1) * factor, atFraction);
            return;
        }

        var under = _viewStart + atFraction * span;
        var next = span / factor;
        SetView(under - atFraction * next, next);
    }

    // A zoom asked for under a drag, done when it is let go.
    private (double Factor, double AtFraction)? _zoomOwed;

    /// <summary>The zoom a drag held back, now that it is over.</summary>
    private void TakeOwedZoom()
    {
        if (_zoomOwed is { } owed)
        {
            _zoomOwed = null;
            ZoomTimeline(owed.Factor, owed.AtFraction);
        }
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

    /// <summary>Moves the view along by so many seconds (later, positive), while it is zoomed; nothing said in the status bar.</summary>
    private void PanTimeline(double seconds) => PanTimeline(seconds, announce: false);

    /// <summary>Moves the view along by so many seconds (later, positive), while it is zoomed; <paramref name="announce"/> says where it is now.</summary>
    private void PanTimeline(double seconds, bool announce)
    {
        if (_viewFit || Math.Abs(seconds) < 1e-9)
            return;

        _viewStart += seconds;
        ApplyTimelineView(announce);
    }

    /// <summary>Back to the whole timeline, as a newly loaded video starts.</summary>
    private void FitTimeline(bool announce = true)
    {
        // The user's fit (announced) is not made while something is dragged on the timeline, as a zoom is not; a newly
        // loaded video's always is.
        if (announce && UnifiedTimeline.Surface.IsDragging)
            return;

        (_viewFit, _viewStart, _viewSpan) = (true, 0, 0);
        ApplyTimelineView(announce);
    }

    /// <summary>Keeps the view inside the timeline, and shows it: the tracks (laid out again only when the scale changed), the ruler, the scroll bar, the zoom, the Keyframes pane.</summary>
    private void ApplyTimelineView(bool announce = true)
    {
        UpdateTimelineChrome();
        UpdateMasterStretch();
        DrawKeyTimeline();
        if (!announce)
            return;

        var extent = TimelineExtent;
        _viewModel.StatusText = _viewFit || extent <= 0
            ? "The timeline shows the whole sequence, and room after it to drop clips into (Ctrl + wheel over the tracks, or the wheel over the ruler, zooms)."
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Timeline zoomed {extent / ViewSpan:0.#}x: {TimeDisplay.Format(_viewStart)} to {TimeDisplay.Format(_viewStart + ViewSpan)} (Shift + wheel, the scroll bar or a middle-button drag moves along; \\ fits).");
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

    /// <summary>
    /// Brings the surface, the rulers, the scrollbars and the zoom sliders up to date with the view, all from one view: a sequence that got
    /// shorter (a crossfade, a ripple) moves the view, and the ruler moved without the tracks until the next scroll (5.1-DogfoodPolish).
    /// </summary>
    private void UpdateTimelineChrome()
    {
        ClampView();
        ShowTimelineView();
        var (extent, span, sequence) = (TimelineExtent, ViewSpan, _viewModel.DurationMs / 1000);
        _showingView = true;
        try
        {
            // With nothing on the timeline yet the rulers still read as rulers (4.3E): a minute's ticks, all of it shaded as room to drop clips into.
            var markers = _viewModel.Markers.Select(m => m.Seconds).ToList();
            var highlights = _viewModel.Markers.Where(m => m.Highlight).Select(m => m.Seconds).ToList();
            foreach (var ruler in _rulers)
            {
                ruler.Show(_viewStart, span > 0 ? span : EmptyRulerSeconds, sequence, markers, highlights);
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
    }

    private void PlaceRulerPlayhead(TimelineRuler ruler)
    {
        var span = ViewSpan;
        ruler.PlayheadX = span > 0 && _viewModel.DurationMs > 0 ? (_viewModel.PositionMs / 1000 - _viewStart) / span * ruler.ActualWidth : double.NaN;
    }

    /// <summary>
    /// While a video plays with the bars zoomed in, the view turns the page when the playhead reaches its right
    /// edge, so that what is playing stays in sight; never with Follow Playhead During Playback switched off (5.3,
    /// AppSettings.TimelineFollowsPlayhead: the view stays where the user put it).
    /// </summary>
    private void FollowPlayhead()
    {
        if (_viewFit || !_viewModel.IsPlaying || _isScrubbing || !Services.AppSettings.Current.TimelineFollowsPlayhead)
            return;

        var (at, span) = (_viewModel.PositionMs / 1000, ViewSpan);
        if (at > _viewStart + span || at < _viewStart)
        {
            // The page turns: a coast along the view stops, so the two never pull the view between them (5.4C).
            StopTimelineMomentum();
            _viewStart = at - span * 0.05;
            ApplyTimelineView(announce: false);
        }
    }

    /// <summary>Stops any coast of the timeline's view where it is: the surface's (its rows too) and the rulers' (5.4C).</summary>
    private void StopTimelineMomentum()
    {
        UnifiedTimeline.Surface.StopMomentum();
        foreach (var ruler in _rulers)
            ruler.StopMomentum();
    }

    // ----- The ruler, the scroll bar and the zoom slider -----

    /// <summary>
    /// Has a ruler, a scroll bar and a zoom slider show the view and move it (the timeline pane's, once, as the window is
    /// made): the ruler seeks, zooms, pans and opens a marker's menu; the bar moves along; the slider zooms about the playhead.
    /// </summary>
    private void RegisterTimelineChrome(TimelineRuler ruler, ScrollBar bar, Slider zoom)
    {
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

        _timelineScrollBars.Add(bar);
        bar.Scroll += TimelineScroll_Scroll;
        _zoomSliders.Add(zoom);
        zoom.ValueChanged += TimelineZoomSlider_ValueChanged;

        // Touch-to-stop across the pane (5.4C): each stops its own coast on a press; a press on the one stops the other's, and a press on
        // the scroll bar stops both (handled presses too: the bar's thumb and buttons answer theirs).
        ruler.PreviewMouseDown += (_, _) => UnifiedTimeline.Surface.StopMomentum();
        UnifiedTimeline.Surface.PreviewMouseDown += (_, _) => ruler.StopMomentum();
        bar.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => StopTimelineMomentum()), handledEventsToo: true);
        HookTimelineSidewaysWheel();
    }

    // ----- The sideways wheel over the timeline (5.4C) -----

    private bool _sidewaysWheelHooked;

    /// <summary>
    /// A touchpad's two-finger sideways scroll (or a tilting wheel) moves the timeline's view along, with momentum: WPF raises nothing for
    /// WM_MOUSEHWHEEL, so a hook of its own on the main window reads it (never TitleBarHook or FrameHook, whose answers are pinned), added
    /// once the window's handle is made, and only while the pointer is over the timeline's tracks or ruler does it take the message for the
    /// timeline. Elsewhere (5.4.1A) it goes to the first visible strip under the pointer that scrolls in time (ISidewaysWheel: the Subtitle
    /// Studio's strip, the Media Browser's filmstrip cutter), docked or in a side window (each has this same hook); else it is left alone.
    /// </summary>
    private void HookTimelineSidewaysWheel()
    {
        if (_sidewaysWheelHooked)
            return;

        _sidewaysWheelHooked = true;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TimelineSidewaysWheel);
    }

    private IntPtr TimelineSidewaysWheel(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMouseHWheel)
            return IntPtr.Zero;

        // Positive is to the right: later. Answered TRUE, or Windows sends it on as clicks of an old tilt wheel.
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        var now = MomentumScroller.Now();
        if (TimelineUnderPointer() is { } surface)
            surface.PanWheelAt(delta, now);
        else if (SidewaysWheelUnderPointer() is { } strip)
            strip.SidewaysWheel(delta, now);
        else
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(1);
    }

    /// <summary>
    /// The first visible control that scrolls in time on a sideways swipe (<see cref="ISidewaysWheel"/>) from the element under the pointer
    /// up: its visual parents, its logical ones where the visual tree ends (a popup, a content element). Null for none. Allocates nothing.
    /// </summary>
    internal static ISidewaysWheel? SidewaysWheelUnderPointer()
    {
        for (var node = Mouse.DirectlyOver as DependencyObject; node is not null;
             node = (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is ISidewaysWheel wheel and UIElement { IsVisible: true })
                return wheel;
        }

        return null;
    }

    // The timeline's surface while the pointer is over it or its ruler (and it is shown); null otherwise.
    private UnifiedTimelineControl? TimelineUnderPointer()
    {
        var surface = UnifiedTimeline.Surface;
        if (!surface.IsVisible)
            return null;
        if (surface.IsMouseOver)
            return surface;
        foreach (var ruler in _rulers)
        {
            if (ruler.IsMouseOver)
                return surface;
        }

        return null;
    }

    private void TimelineScroll_Scroll(object sender, ScrollEventArgs e)
    {
        if (_showingView || _viewFit || sender is not ScrollBar bar)
            return;

        StopTimelineMomentum();
        _viewStart = bar.Value;
        ApplyTimelineView(announce: false);
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

    /// <summary>Stretches the Sequence Overview Scrubber to the time bars' view and slides it to their place, while that is switched on; otherwise it fills its place as it always did.</summary>
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
    /// videos', the pictures', the sound clips'), the start and end of the sequence, the playhead and the markers, and the
    /// beats found in clips' sound, less those of the clips whose blocks a drag takes along (<paramref name="moving"/>: a
    /// move's or a slip's, whose beats go with it). Nothing while snapping is off.
    /// </summary>
    private IReadOnlyList<double> SnapTimes(IReadOnlyList<TimelineBlock> moving)
    {
        if (!_viewModel.SnapClips)
            return [];

        var sequence = _viewModel.DurationMs / 1000;
        var times = new List<double> { 0, sequence, _viewModel.PositionMs / 1000 };
        foreach (var layer in _viewModel.Layers.Where(l => l.HasTiming && !l.IsDeleted))
        {
            var (from, to) = layer.GetSpan(sequence);
            times.AddRange([from, to]);
        }

        times.AddRange(_viewModel.Markers.Select(m => m.Seconds));

        // The beats found in clips' sound (5.2B, Detect & Snap to Audio Beats), where they fall on the Sequence: never those of a
        // clip the drag moves or slips (its picture's block or its unlinked sound's), which would catch on where they were.
        if (_viewModel.SnapToBeats)
        {
            HashSet<Guid>? going = null;
            foreach (var block in moving)
            {
                var dragged = block.Clip switch { Layer view => view, LayerSound sound => sound.Layer, _ => null };
                if (_viewModel.ClipOf(dragged) is { } model)
                    (going ??= []).Add(model.Id);
            }

            foreach (var track in _viewModel.Document.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (going?.Contains(clip.Id) != true)
                        ViewModels.MainViewModel.BeatsOnSequence(clip, times);
                }
            }
        }

        return times;
    }

    /// <summary>A moment moved onto the nearest one to snap to, when that is within a few pixels (<paramref name="perPixel"/> seconds each); as it was otherwise.</summary>
    private double SnapTime(double seconds, double perPixel)
    {
        if (perPixel <= 0)
            return seconds;

        var (best, nearest) = (seconds, TimelineMetrics.SnapPixels * 1.5 * perPixel);
        foreach (var time in SnapTimes([]))
        {
            if (Math.Abs(time - seconds) < nearest)
                (best, nearest) = (time, Math.Abs(time - seconds));
        }

        return best;
    }

    // ----- Dropping files on the timeline -----

    /// <summary>
    /// Where files dragged over the timeline would land, or null when they are not over its tracks (or nothing is open):
    /// the row under them (null below every row) and the moment, snapped. Over a row's header, the moment is the end of
    /// that track's last clip, so files dropped on a name follow what is already there (the playhead, on a row with none).
    /// Inside a clip of the row the moment stays where they were let go: a new clip goes on a track of its own there
    /// (MainViewModel.PlaceClip), never over the clip; snapping gives the clip's end, for clips end to end.
    /// </summary>
    private (TimelineRow? Row, double Seconds, bool OnHeader)? FindDropPlace(DragEventArgs e)
    {
        var surface = UnifiedTimeline.Surface;
        if (!_viewModel.HasSource || !surface.IsVisible || surface.ActualWidth <= 0)
            return null;

        var point = e.GetPosition(surface);
        if (point.X < 0 || point.Y < 0 || point.X > surface.ActualWidth || point.Y > surface.ActualHeight)
            return null;

        var (row, seconds, onHeader) = surface.HitTestDrop(point);
        if (onHeader)
        {
            var sequence = _viewModel.DurationMs / 1000;
            seconds = row?.Data is Layer { HasTiming: true, IsEmptyTrack: false, IsCaptions: false, IsBackground: false } track
                ? _viewModel.GetTrackClips(track).Max(c => c.GetSpan(sequence).End)
                : _viewModel.PositionMs / 1000;
        }
        else if (surface.PixelsPerSecond > 0)
        {
            seconds = SnapTime(seconds, 1 / surface.PixelsPerSecond);
        }

        // The captions are drawn from the voice's words, no track media can be put on: what is let go there goes on a new
        // track of its own, as below every row (4.4.0, the user's B8; it once replaced the main video, then was refused).
        if (row is { Kind: TimelineRowKind.Captions })
            (row, onHeader) = (null, false);

        return (row, Math.Max(seconds, 0), onHeader);
    }

    /// <summary>
    /// Several files let go on a row's header, or with Alt held, are a Stack Drop: each on a track of its own, all at the
    /// same moment. Otherwise they are a Sequence Drop: one after another on one track, from the moment let go on.
    /// </summary>
    private static bool IsStackDrop(DragEventArgs e, bool onHeader, int count) =>
        count > 1 && (onHeader || e.KeyStates.HasFlag(DragDropKeyStates.AltKey));

    /// <summary>
    /// The track a file dropped on a row goes beside (or onto): a track's row; null (a new track) for the background and
    /// below every row (FindDropPlace puts the captions, their strip in a collapsed stack too, there).
    /// </summary>
    private static Layer? DropTrackOf(TimelineRow? row) =>
        row is { Kind: not TimelineRowKind.Captions, Data: Layer { IsCaptions: false, IsBackground: false } track } ? track : null;

    /// <summary>Marks, on the timeline, where files dragged over it would land: the row, the moment, how many, stacked or one after another.</summary>
    private void ShowDropMark(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || FindDropPlace(e) is not { } place)
        {
            ClearDropMark();
            return;
        }

        var count = (e.Data.GetData(DataFormats.FileDrop) as string[])?.Length ?? 1;
        UnifiedTimeline.Surface.ShowDropMark(place.Row, place.Seconds, count, IsStackDrop(e, place.OnHeader, count));
    }

    private void ClearDropMark() => UnifiedTimeline.Surface.ClearDropMark();

    // ----- Markers -----

    private static readonly Brush MarkerLineBrush = FrozenMarkerLine();

    private static Brush FrozenMarkerLine()
    {
        var brush = new SolidColorBrush(Color.FromArgb(150, 0x7C, 0xD9, 0x92));
        brush.Freeze();
        return brush;
    }

    /// <summary>The markers on the Sequence Overview Scrubber: a flag at the top and a faint line down, each answering a click (go there) and a right-click (its menu).</summary>
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
        Add("Clear All Markers", _viewModel.ClearMarkers);
        menu.IsOpen = true;
    }

    private void OnMarkersChanged()
    {
        DrawTimelineMarkers();
        UpdateTimelineChrome();
    }
}
