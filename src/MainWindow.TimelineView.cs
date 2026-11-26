using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Timeline view range -----
// Timeline surface, ruler and Keyframes pane share one view: _viewStart seconds, ViewSpan seconds wide. TimelineExtent runs past the
// sequence end so clips can be dropped or dragged after the last one (the sequence grows to fit). Fitted (default) shows the whole
// extent and tracks growth; zoomed keeps seconds-per-pixel while ruler, scroll bar, Shift+wheel or middle-drag pan. Ctrl+wheel over
// tracks or wheel over the ruler zooms about the pointer; buttons and slider zoom about the playhead. The Overview Scrubber follows
// when "Sync Sequence Overview Scrubber Stretch" is on (Views menu). Flicks and fast wheels coast (MomentumScroller); a press on the
// surface, ruler or scroll bar, a playhead page turn, or a zoom stops the coast.
public partial class MainWindow
{
    /// <summary>Minimum view span in seconds (a few frames, each wide enough to grab).</summary>
    private const double MinViewSpan = 0.5;

    /// <summary>Ruler span in seconds for an empty timeline (rulers always show ticks).</summary>
    private const double EmptyRulerSeconds = 60;

    private double _viewStart, _viewSpan;
    private bool _viewFit = true;

    // View chrome registered by the timeline pane (RegisterTimelineChrome): rulers, scroll bars, zoom sliders.
    private readonly List<TimelineRuler> _rulers = [];
    private readonly List<ScrollBar> _timelineScrollBars = [];
    private readonly List<ExpandablePillSlider> _zoomSliders = [];
    private bool _showingView;

    /// <summary>Scrollable extent in seconds: sequence duration plus max(12%, 5 s) drop room; 0 when empty.</summary>
    private double TimelineExtent
    {
        get
        {
            var seconds = _viewModel.DurationMs / 1000;
            return seconds <= 0 ? 0 : seconds + Math.Max(seconds * 0.12, 5);
        }
    }

    /// <summary>Visible span in seconds (the full extent when fitted).</summary>
    private double ViewSpan
    {
        get
        {
            var extent = TimelineExtent;
            return _viewFit || _viewSpan <= 0 || _viewSpan >= extent ? extent : Math.Max(_viewSpan, Math.Min(MinViewSpan, extent));
        }
    }

    /// <summary>Log-scaled zoom 0 (fitted) to 1 (MinViewSpan), shown by the zoom sliders.</summary>
    private double ZoomLevel
    {
        get
        {
            var extent = TimelineExtent;
            return extent <= MinViewSpan ? 0 : Math.Clamp(Math.Log(extent / ViewSpan) / Math.Log(extent / MinViewSpan), 0, 1);
        }
    }

    /// <summary>Sets the view to <paramref name="start"/> + <paramref name="span"/> seconds (clamped; a full-extent span means fitted).</summary>
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

    /// <summary>Zooms by <paramref name="factor"/> (> 1 = in) keeping the time at <paramref name="atFraction"/> of the width fixed.</summary>
    private void ZoomTimeline(double factor, double atFraction)
    {
        // Deferred during timeline drags (wheel, keys, buttons): a drag commits in the pixel scale it started with, so the zoom is
        // accumulated and applied on drag end (Timeline_DragEnded).
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

    // Zoom accumulated during a drag, applied on release.
    private (double Factor, double AtFraction)? _zoomOwed;

    /// <summary>Applies the zoom deferred by a drag.</summary>
    private void TakeOwedZoom()
    {
        if (_zoomOwed is { } owed)
        {
            _zoomOwed = null;
            ZoomTimeline(owed.Factor, owed.AtFraction);
        }
    }

    /// <summary>Zooms about the playhead when visible, else about the view centre.</summary>
    private void ZoomAboutPlayhead(double factor)
    {
        var span = ViewSpan;
        if (span <= 0)
            return;

        var fraction = (_viewModel.PositionMs / 1000 - _viewStart) / span;
        ZoomTimeline(factor, fraction is >= 0 and <= 1 ? fraction : 0.5);
    }

    /// <summary>Pans a zoomed view by <paramref name="seconds"/> (positive = later) without a status message.</summary>
    private void PanTimeline(double seconds) => PanTimeline(seconds, announce: false);

    /// <summary>Pans a zoomed view by <paramref name="seconds"/> (positive = later); <paramref name="announce"/> posts the new range.</summary>
    private void PanTimeline(double seconds, bool announce)
    {
        if (_viewFit || Math.Abs(seconds) < 1e-9)
            return;

        _viewStart += seconds;
        ApplyTimelineView(announce);
    }

    /// <summary>Fits the whole timeline (also the state after loading a video).</summary>
    private void FitTimeline(bool announce = true)
    {
        // Interactive (announced) fits are ignored during drags like zooms; load-time fits always apply.
        if (announce && UnifiedTimeline.Surface.IsDragging)
            return;

        (_viewFit, _viewStart, _viewSpan) = (true, 0, 0);
        ApplyTimelineView(announce);
    }

    /// <summary>Clamps and applies the view to tracks (relaid only on scale change), rulers, scroll bars, zoom and the Keyframes pane.</summary>
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
    /// Updates surface, rulers, scroll bars and zoom sliders from a single clamped view, so a shortened sequence (crossfade, ripple) moves
    /// all of them together (never the ruler alone until the next scroll).
    /// </summary>
    private void UpdateTimelineChrome()
    {
        ClampView();
        ShowTimelineView();
        var (extent, span, sequence) = (TimelineExtent, ViewSpan, _viewModel.DurationMs / 1000);
        _showingView = true;
        try
        {
            // Empty timeline: rulers still show a minute of ticks, all shaded as drop room.
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

    // Ruler playhead x at the playhead, or at a leading scrub time in seconds (MainWindow.Scrub.cs).
    private void PlaceRulerPlayhead(TimelineRuler ruler, double? seconds = null)
    {
        var span = ViewSpan;
        var at = seconds ?? _viewModel.PositionMs / 1000;
        ruler.PlayheadX = span > 0 && _viewModel.DurationMs > 0 ? (at - _viewStart) / span * ruler.ActualWidth : double.NaN;
    }

    /// <summary>
    /// During zoomed playback, pages the view when the playhead leaves it so playback stays visible; disabled when
    /// AppSettings.TimelineFollowsPlayhead (Follow Playhead During Playback) is off.
    /// </summary>
    private void FollowPlayhead()
    {
        if (_viewFit || !_viewModel.IsPlaying || IsScrubHeld ||!Services.AppSettings.Current.TimelineFollowsPlayhead)
            return;

        var (at, span) = (_viewModel.PositionMs / 1000, ViewSpan);
        if (at > _viewStart + span || at < _viewStart)
        {
            // Page turn: stop any coast so the two never fight over the view.
            StopTimelineMomentum();
            _viewStart = at - span * 0.05;
            ApplyTimelineView(announce: false);
        }
    }

    /// <summary>Stops all view coasts in place: the surface (rows included) and every ruler.</summary>
    private void StopTimelineMomentum()
    {
        UnifiedTimeline.Surface.StopMomentum();
        foreach (var ruler in _rulers)
            ruler.StopMomentum();
    }

    // ----- Ruler, scroll bar and zoom slider -----

    /// <summary>
    /// Registers the timeline pane's ruler, scroll bar and zoom slider (once, at window creation): the ruler seeks, zooms, pans and opens
    /// marker menus; the bar pans; the slider zooms about the playhead.
    /// </summary>
    private void RegisterTimelineChrome(TimelineRuler ruler, ScrollBar bar, ExpandablePillSlider zoom)
    {
        _rulers.Add(ruler);
        ruler.SeekRequested += seconds => ScrubPlayheadTo(seconds * 1000);
        ruler.SeekEnded += EndScrub;
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

        // Pill readout shows the zoom factor like the status line ("2.4x"), not the 0..1 slider value.
        zoom.ReadoutFor = _ => ZoomReadout(TimelineExtent, ViewSpan);

        // Touch-to-stop: each control stops its own coast on press; ruler and surface stop each other's; the scroll bar stops both
        // (handledEventsToo, since its thumb and buttons handle their presses).
        ruler.PreviewMouseDown += (_, _) => UnifiedTimeline.Surface.StopMomentum();
        UnifiedTimeline.Surface.PreviewMouseDown += (_, _) => ruler.StopMomentum();
        bar.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => StopTimelineMomentum()), handledEventsToo: true);
        HookTimelineSidewaysWheel();
    }

    // ----- Horizontal wheel (WM_MOUSEHWHEEL) -----

    private bool _sidewaysWheelHooked;

    /// <summary>
    /// Touchpad two-finger horizontal scroll or tilt wheel pans with momentum. WPF does not surface WM_MOUSEHWHEEL, so a dedicated hook
    /// (not TitleBarHook or FrameHook, whose results are pinned) is added after SourceInitialized. Over timeline tracks or ruler it pans the
    /// timeline; elsewhere it goes to the first visible ISidewaysWheel under the pointer (Subtitle Studio strip, Media Browser filmstrip
    /// cutter), docked or in a side window (which has the same hook); otherwise unhandled.
    /// </summary>
    private void HookTimelineSidewaysWheel()
    {
        if (_sidewaysWheelHooked)
            return;

        _sidewaysWheelHooked = true;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TimelineSidewaysWheel);
    }

    // Timeline row input state (Pass 5.6.5), in one object (golden shell-budget).
    private readonly TimelineInputState _timelineInput = new();

    private sealed class TimelineInputState
    {
        // Wheel gesture memory over the timeline rows (TouchpadWheel: a touchpad gesture keeps its kind across both axes).
        public readonly TouchpadWheel Wheel = new();

        // Alt+wheel row height: the row being stepped (its live height is _rowHeightDrag) and the rest timer that commits it.
        public TimelineRow? RowStepped;
        public DispatcherTimer? RowStepCommit;

        // Ghost Playhead: the playhead (ms) to snap back to; null while no ghost is shown.
        public double? GhostHomeMs;

        // Clip whose ducking gesture has taken its Undo step (until release).
        public Layer? DuckShaping;

        // Spring-Zoom (Pass 5.7.1): the view the spring left (fitted, start, span), restored on release; and true while a spring view is
        // applied, so rows are rebuilt at its scale even mid-drag (the surface re-seats the drag on them).
        public (bool Fit, double Start, double Span)? SpringHome;
        public bool SpringRelay;
    }

    private IntPtr TimelineSidewaysWheel(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (TouchpadWheel.WmMouseWheel or TouchpadWheel.WmMouseHWheel))
            return IntPtr.Zero;

        // Touchpad over the rows, either axis: 1:1 two-axis scroll, pinch zoom, Alt heights. A mouse wheel goes on to WPF (OnMouseWheel).
        if (UnifiedTimeline.Surface is { IsVisible: true, IsMouseOver: true } rows && _timelineInput.Wheel.Read(msg, wParam, lParam) is { Touchpad: true } touch)
        {
            rows.TakeTouchpadWheel(touch, rows.PointFromScreen(touch.ScreenPixels));
            handled = true;
            return TouchpadWheel.HandledResult(touch.Horizontal);
        }

        if (msg != WmMouseHWheel)
            return IntPtr.Zero;

        // Positive = right (later). Return TRUE, or Windows re-sends it as legacy tilt-wheel scroll clicks.
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
    /// First visible <see cref="ISidewaysWheel"/> walking up from Mouse.DirectlyOver (visual parents, then logical where the visual tree
    /// ends: popups, content elements). Null for none; allocation-free.
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

    // Visible timeline surface when the pointer is over it or a ruler; else null.
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

    /// <summary>Zoom pill readout: extent / span ("2.4x"); "1x" when undefined.</summary>
    internal static string ZoomReadout(double extent, double span) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(extent > 0 && span > 0 ? extent / span : 1):0.#}x");

    /// <summary>When SyncMasterTimeline is on and zoomed, stretches and offsets the Overview Scrubber to match the view; else it fills its viewport.</summary>
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
    /// Snap targets in seconds: every timed clip's start/end, sequence start/end, playhead, markers, and detected audio beats excluding
    /// those of clips carried by the drag (<paramref name="moving"/>: move or slip). Empty when snapping is off.
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

        // Audio beats (Detect & Snap to Audio Beats) in sequence time, skipping clips being moved or slipped (video or unlinked audio
        // block), whose stale beat positions would otherwise snap.
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

    /// <summary>Snaps <paramref name="seconds"/> to the nearest target within 1.5x SnapPixels (<paramref name="perPixel"/> s/px), else unchanged.</summary>
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

    // ----- File drops on the timeline -----

    /// <summary>
    /// Drop target for files over the timeline, or null when off the tracks or nothing is open: row (null below all rows) and snapped
    /// time. On a header, time is the track's last clip end (playhead for an empty track) so drops append. Inside a clip the drop time is
    /// kept and MainViewModel.PlaceClip puts the new clip on its own track (never over the clip); snapping offers the clip end.
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

        // The captions row is generated from speech and accepts no media: treat a drop there as below all rows (new track).
        if (row is { Kind: TimelineRowKind.Captions })
            (row, onHeader) = (null, false);

        return (row, Math.Max(seconds, 0), onHeader);
    }

    /// <summary>
    /// Multiple files on a header or with Alt = Stack Drop (one track each, same start); otherwise Sequence Drop (end to end on one track).
    /// </summary>
    private static bool IsStackDrop(DragEventArgs e, bool onHeader, int count) =>
        count > 1 && (onHeader || e.KeyStates.HasFlag(DragDropKeyStates.AltKey));

    /// <summary>
    /// Track a dropped file targets: the row's track; null (new track) for background, captions (remapped by FindDropPlace, including
    /// collapsed-stack strips) and below all rows.
    /// </summary>
    private static Layer? DropTrackOf(TimelineRow? row) =>
        row is { Kind: not TimelineRowKind.Captions, Data: Layer { IsCaptions: false, IsBackground: false } track } ? track : null;

    /// <summary>Shows the drop indicator: row, time, file count and stack vs sequence mode.</summary>
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

    /// <summary>Draws Overview Scrubber markers: flag plus faint line; click seeks, right-click opens the marker menu.</summary>
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

    /// <summary>Marker menu: inline rename, Go To, Delete, Delete All.</summary>
    private void ShowMarkerMenu(FrameworkElement target, TimelineMarker marker)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        var renaming = false;
        AddRenameItem(menu, marker.Name, name =>
        {
            // One Undo step per rename session, opened at the first keystroke.
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

// ----- Timeline gestures host (Pass 5.6.5 L3; the surface side is the gestures section of UnifiedTimelineControl.Parenting.cs) -----
// Alt+wheel sizes one track (live rows, one Undo step once the wheel rests); a gap double-click ripple-closes it (CloseGapAt); the Ghost
// Playhead skims the live composite at a dragged edge while paused and snaps back on release; Z toggles Spring-Zoom mid-drag; inline
// quick-edit icons act on a clip's effects, one effect or a crossfade; the amber ducking curve writes Layer.DuckOverrideKeys; a gain-line
// double-click runs Auto-Level Speech (-14 LUFS, measured in the background); Alt+drop of one file on a clip swaps its media in place.
public partial class MainWindow
{
    /// <summary>Wires the surface's gesture events (InitializeUnifiedTimeline).</summary>
    private void InitializeTimelineGestures(UnifiedTimelineControl surface)
    {
        surface.RowHeightStepped += Timeline_RowHeightStepped;
        surface.GapDoubleClicked += (row, seconds) =>
        {
            if (row.Data is Layer track)
                RunGuarded("closing a gap", () => _viewModel.CloseGapAt(track, seconds));
        };
        surface.GhostPlayheadMoved += Timeline_GhostPlayhead;
        surface.SpringViewRequested += Timeline_SpringViewRequested;
        InitializeTimelineSeams(surface);
    }

    // ----- Spring-Zoom view (Pass 5.7.1) -----

    /// <summary>
    /// A Spring-Zoom step: shows the asked view at once (no status line), rows rebuilt at its scale even mid-drag; Home puts back the view
    /// the spring left (a fitted view stays fitted, so a sequence grown by the drop is shown whole).
    /// </summary>
    private void Timeline_SpringViewRequested(SpringView view)
    {
        if (view.Home)
        {
            if (_timelineInput.SpringHome is not { } home)
                return;
            _timelineInput.SpringHome = null;
            (_viewFit, _viewStart, _viewSpan) = home;
            ApplySpringView();
            return;
        }

        var extent = TimelineExtent;
        if (extent <= 0 || !(view.Span > 0) || !double.IsFinite(view.Start))
            return;

        _timelineInput.SpringHome ??= (_viewFit, _viewStart, _viewSpan);
        var span = Math.Clamp(view.Span, Math.Min(MinViewSpan, extent), extent);
        _viewFit = span >= extent - 1e-6;
        (_viewStart, _viewSpan) = (_viewFit ? 0 : view.Start, span);
        ApplySpringView();
    }

    // Applies a spring view: chrome, rows (relaid at the new scale even mid-drag) and the Keyframes pane.
    private void ApplySpringView()
    {
        _timelineInput.SpringRelay = true;
        try
        {
            ApplyTimelineView(announce: false);
        }
        finally
        {
            _timelineInput.SpringRelay = false;
        }
    }

    /// <summary>Z (TakeSeamKey): toggles Spring-Zoom during a drag that spring-zooms; repeats are swallowed so Z never also steps edits.</summary>
    partial void TakeSpringZoomKey(bool repeat, ref bool handled)
    {
        var surface = UnifiedTimeline.Surface;
        if (!surface.CanSpringZoom)
            return;

        handled = true;
        if (!repeat)
            surface.SpringZoomKey();
    }

    // ----- Alt+wheel track height -----

    private void Timeline_RowHeightStepped(TimelineRow row, int steps)
    {
        if (HeightTrackOf(row) is null)
            return;
        if (_timelineInput.RowStepped is { } other && !Equals(other.Key, row.Key))
            CommitRowStep();

        // The global Track Height's pixels per notch, within the row kind's range.
        var (lowest, tallest) = row.Kind == TimelineRowKind.Audio ? (LowestSoundRow, TallestSoundRow) : (LowestPictureRow, TallestPictureRow);
        var perNotch = TrackHeightStepsPerNotch * (TallestPictureRow - LowestPictureRow) / 49;
        var current = _rowHeightDrag is { } live && Equals(live.Key, row.Key) ? live.Height : row.Height;
        _rowHeightDrag = (row.Key, Math.Clamp(current + steps * perNotch, lowest, tallest));
        _timelineInput.RowStepped = row;
        ScheduleClipRedraw();
        _timelineInput.RowStepCommit ??= NewRowStepTimer();
        _timelineInput.RowStepCommit.Stop();
        _timelineInput.RowStepCommit.Start();
    }

    private DispatcherTimer NewRowStepTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) => CommitRowStep();
        return timer;
    }

    // The wheel rested (or another row was stepped): the stepped height becomes the track's own, one Undo step.
    private void CommitRowStep()
    {
        _timelineInput.RowStepCommit?.Stop();
        if (_timelineInput.RowStepped is not { } row)
            return;

        _timelineInput.RowStepped = null;
        if (_rowHeightDrag is not { } live || !Equals(live.Key, row.Key))
            return;

        var height = RowHeightOf(live.Key, null, row.Kind);
        _rowHeightDrag = null;
        if (HeightTrackOf(row) is { } track)
            RunGuarded("a track's height", () => _viewModel.SetTrackHeight(track, height));
        ScheduleClipRedraw();
    }

    // ----- Ghost Playhead -----

    private void Timeline_GhostPlayhead(double seconds)
    {
        if (double.IsNaN(seconds))
        {
            if (_timelineInput.GhostHomeMs is { } home)
            {
                _timelineInput.GhostHomeMs = null;
                _viewModel.PositionMs = Math.Clamp(home, 0, _viewModel.DurationMs);
            }

            return;
        }

        // Paused only: during playback the transport keeps its place.
        if (_viewModel.IsPlaying || !_viewModel.HasSource)
            return;

        _timelineInput.GhostHomeMs ??= _viewModel.PositionMs;
        _viewModel.PositionMs = Math.Clamp(seconds * 1000, 0, _viewModel.DurationMs);
    }

    // ----- Quick-edit icons -----

    /// <summary>A clip block's icons: its effects ("fx N") or its crossfade (Edit, Remove).</summary>
    private void ClipQuickEdit(Layer clip, QuickEditTarget target, QuickEditVerb verb)
    {
        if (LiveClip(clip) is not { } live)
            return;

        RunGuarded("a quick edit", () =>
        {
            switch (target, verb)
            {
                case (QuickEditTarget.Transition, QuickEditVerb.Remove):
                    _viewModel.SetCrossfade(live, 0);
                    break;
                case (_, QuickEditVerb.Edit):
                    ShowClipIn(live, keyframes: false);
                    break;
                case (_, QuickEditVerb.Keyframes):
                    ShowClipIn(live, keyframes: true);
                    break;
                case (QuickEditTarget.Effects, QuickEditVerb.Toggle):
                    var cards = BadgeCards(live);
                    var on = !cards.Exists(card => card.Enabled);
                    foreach (var card in cards)
                        _viewModel.SetEffectOn(live, card, on);
                    break;
                case (QuickEditTarget.Effects, QuickEditVerb.Remove):
                    foreach (var card in BadgeCards(live))
                        _viewModel.RemoveEffect(live, card);
                    break;
            }
        });
        ScheduleClipRedraw();
    }

    /// <summary>An effect lane block's icons: open it, its keys, On/Off, remove.</summary>
    private void EffectQuickEdit(Layer clip, EffectInstance effect, QuickEditVerb verb)
    {
        if (LiveClip(clip) is not { } live)
            return;

        RunGuarded("a quick edit of an effect", () =>
        {
            switch (verb)
            {
                case QuickEditVerb.Edit:
                    ShowClipIn(live, keyframes: false);
                    break;
                case QuickEditVerb.Keyframes:
                    ShowClipIn(live, keyframes: true);
                    break;
                case QuickEditVerb.Toggle:
                    _viewModel.SetEffectOn(live, effect, !effect.Enabled);
                    break;
                default:
                    _viewModel.RemoveEffect(live, effect);
                    break;
            }
        });
        ScheduleClipRedraw();
    }

    // Selects the clip and shows the Effects (or Keyframes) pane for it.
    private void ShowClipIn(Layer clip, bool keyframes)
    {
        SelectClip(clip);
        if (keyframes)
            _viewModel.ShowKeyframesPane = true;
        else
            _viewModel.ShowEffectsPane = true;
    }

    // The cards counted by its "fx N" badge.
    private List<EffectInstance> BadgeCards(Layer clip)
    {
        var cutout = CountsAsCutout(_viewModel, clip);
        var builtIns = AppSettings.Current.ShowBuiltInMaskEffectLanes;
        return clip.IsAudio || clip.IsCaptions ? [] : clip.Effects.Where(effect => ShowsCard(effect, cutout, builtIns)).ToList();
    }

    // ----- Ducking curve -----

    /// <summary>Automatic duck depth drawn under the hand-drawn keys (the track's Duck Amount, -15 dB unless set).</summary>
    private static double DuckDepthOf(Models.Timeline.TimelineTrackModel? track) =>
        Math.Clamp(track?.DuckAmountDb is { } db && double.IsFinite(db) ? -Math.Abs(db) : -15, Layer.DuckOverrideFloorDb, 0);

    // Live keys while dragging; the gesture's first change takes the Undo step, release ends it.
    private void DuckEdited(Layer clip, IReadOnlyList<Keyframe> keys, bool done)
    {
        if (LiveClip(clip) is not { } live)
            return;

        if (!ReferenceEquals(_timelineInput.DuckShaping, live))
        {
            _viewModel.Checkpoint(keys.Count < live.DuckOverrideKeys.Count ? $"remove a ducking point of {live.Name}" : $"shape the ducking of {live.Name}");
            _timelineInput.DuckShaping = live;
        }

        using (_viewModel.OwnLookScope())
            live.SetDuckOverrideKeys(keys);
        if (done)
        {
            _timelineInput.DuckShaping = null;
            ScheduleClipRedraw();
        }
    }

    // ----- Auto-level -----

    // Gain-line double-click: Auto-Level Speech, as the clip menu's item (the clip's sound measured in the background, its gain set for
    // -14 LUFS in one Undo step); nothing while that clip is still being measured.
    private void AutoLevelClip(Layer clip)
    {
        if (LiveClip(clip) is { } live && _viewModel.CanAutoLevelSpeech(live))
            _ = AutoLevelSpeechAsync(live);
    }

    // ----- Media swap in place (Alt+drop of one file on a clip) -----

    /// <summary>The clip view on <paramref name="track"/> under <paramref name="seconds"/>; null for none.</summary>
    private Layer? ClipViewAt(Layer track, double seconds) =>
        _viewModel.GetTrackClips(track).FirstOrDefault(clip => !clip.IsLoading && _viewModel.ClipOf(clip) is { } model && model.Start <= seconds && model.End > seconds);

    /// <summary>Probes the file, then swaps it into the clip (and its Cutouts) keeping place, length, look and effects; kinds must match.</summary>
    private async Task SwapDroppedMediaAsync(Layer clip, string file)
    {
        // The drop's own project: a tab switch during the probe (a duplicate tab shares clip ids) or a reload drops the swap.
        var (document, generation) = (_viewModel.Document, _viewModel.ProjectGeneration);
        var info = await MediaProbe.ProbeAsync(file, CancellationToken.None);
        if (!ReferenceEquals(_viewModel.Document, document) || _viewModel.ProjectGeneration != generation)
            return;

        var name = Path.GetFileName(file);
        if (info is null || !(info.DurationSeconds > 0))
        {
            _viewModel.StatusText = $"{name} could not be read, so nothing was swapped.";
            return;
        }

        if (clip.IsAudio ? info.Audio.Count == 0 : info.Video is null)
        {
            _viewModel.StatusText = clip.IsAudio ? $"{name} has no sound to swap in." : $"{name} has no picture to swap in.";
            return;
        }

        if (LiveClip(clip) is { } live)
            RunGuarded("a media swap", () => _viewModel.SwapClipMedia(live, file, info.DurationSeconds, 0, info.Video is null ? 0 : 1, info.Audio.Count));
    }
}

// ----- Cut seams, Ctrl+drag clones and Top & Tail (Pass 5.7.1 L1; the surface side is the seams section of UnifiedTimelineControl.DragOut.cs) -----
// A selected cut seam takes Ctrl+T (a crossfade of the default transition length), Delete (heals a through-edit, else removes the crossfade)
// and comma / period (rolls the cut a frame), ahead of bound shortcuts; Esc lets it go. The Seam menu (MainWindow.TrackMenus.cs) calls the
// same methods. Ctrl+drag on a clip clones it where it is dropped; Top & Tail ripple-trims the selected clip's start or end to the playhead.
public partial class MainWindow
{
    /// <summary>Wires the surface's seam, clone and region-menu queries (InitializeTimelineGestures).</summary>
    private void InitializeTimelineSeams(UnifiedTimelineControl surface)
    {
        surface.ThroughEdit = (left, right) => LayerOfBlock(left) is { } a && LayerOfBlock(right) is { } b && _viewModel.IsThroughEdit(a, b);
        surface.CloneRequested += (_, block, _, seconds) => RunGuarded("cloning a clip", () => CloneBlock(block, seconds));
        surface.SeamSelected += seam =>
        {
            if (seam is not null)
                _viewModel.StatusText = SeamStatus(seam);
        };
    }

    /// <summary>The clip a block shows (a clip's own sound lane counts as its clip); null for captions, effects and the like.</summary>
    private static Layer? LayerOfBlock(TimelineBlock block) => block.Clip switch
    {
        Layer layer => layer,
        LayerSound sound => sound.Layer,
        _ => null,
    };

    /// <summary>Status line for a selected seam: what its keys do there.</summary>
    internal static string SeamStatus(TimelineSeamTarget seam) => seam switch
    {
        { IsThroughEdit: true } => "Cut selected: a through-edit. Delete heals it; Ctrl+T crossfades; comma and period move it a frame.",
        { CrossfadeSeconds: > 0.01 } => string.Create(CultureInfo.InvariantCulture,
            $"Cut selected, crossfading over {seam.CrossfadeSeconds:0.00} s. Delete removes the crossfade; comma and period move the cut a frame."),
        _ => "Cut selected. Ctrl+T crossfades it; comma and period move it a frame.",
    };

    /// <summary>
    /// Keys for a selected cut seam (TakeSeamKey, ahead of bound shortcuts such as Ctrl+T New Tab and Delete): Ctrl+T crossfade, Delete or
    /// Backspace heal, comma / period nudge a frame (repeats step on), Esc deselect. Never while typing. True when consumed.
    /// </summary>
    private bool TakeCutSeamKey(KeyEventArgs e, Key key)
    {
        var surface = UnifiedTimeline.Surface;
        if (surface.SelectedSeam is not { } seam || surface.IsDragging || !surface.IsVisible
            || Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem)
        {
            return false;
        }

        // A key the focused control owns stays its own (Delete in Keyframes rows or the rules grid; review F12).
        var (act, repeats) = SeamKeyAction(key, Keyboard.Modifiers);
        if (act is null || KeyBelongsToFocus(KeybindManager.ChordOf(e), Keyboard.FocusedElement))
            return false;

        e.Handled = true;
        if (!e.IsRepeat || repeats)
            RunGuarded("an edit at a cut", () => RunSeamAction(act.Value, seam));
        return true;
    }

    /// <summary>What a key does to a selected seam (and whether it repeats while held); null for a key it leaves alone (pure; goldens).</summary>
    internal static (SeamAction? Action, bool Repeats) SeamKeyAction(Key key, ModifierKeys modifiers) => (key, modifiers) switch
    {
        (Key.T, ModifierKeys.Control) => (SeamAction.Crossfade, false),
        (Key.Delete or Key.Back, ModifierKeys.None) => (SeamAction.Heal, false),
        (Key.OemComma, ModifierKeys.None) => (SeamAction.NudgeEarlier, true),
        (Key.OemPeriod, ModifierKeys.None) => (SeamAction.NudgeLater, true),
        (Key.Escape, ModifierKeys.None) => (SeamAction.Deselect, false),
        _ => (null, false),
    };

    /// <summary>The selected-seam actions.</summary>
    internal enum SeamAction { Crossfade, Heal, NudgeEarlier, NudgeLater, Deselect }

    private void RunSeamAction(SeamAction action, TimelineSeamTarget seam)
    {
        switch (action)
        {
            case SeamAction.Crossfade:
                CrossfadeSeam(seam, null);
                break;
            case SeamAction.Heal:
                HealSeam(seam);
                break;
            case SeamAction.NudgeEarlier:
                NudgeSeam(seam, -1);
                break;
            case SeamAction.NudgeLater:
                NudgeSeam(seam, 1);
                break;
            default:
                UnifiedTimeline.Surface.SelectSeam(null);
                break;
        }
    }

    /// <summary>Ctrl+T on a seam (and the Seam menu): a crossfade of <paramref name="seconds"/> (default: Settings' transition length) across the cut.</summary>
    internal void CrossfadeSeam(TimelineSeamTarget seam, double? seconds)
    {
        if (LayerOfBlock(seam.Right) is not { } view || LiveClip(view) is not { } right)
            return;

        var length = seconds ?? Math.Max(AppSettings.Current.DefaultTransitionSeconds, 0.04);
        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"seam: crossfade {length:0.00} s"));
        _viewModel.SetCrossfade(right, length);
        ScheduleClipRedraw();
    }

    /// <summary>Delete on a seam: heals a through-edit into one clip; else removes the crossfade across it; else says why nothing changed.</summary>
    internal void HealSeam(TimelineSeamTarget seam)
    {
        if (LayerOfBlock(seam.Left) is not { } leftView || LayerOfBlock(seam.Right) is not { } rightView
            || LiveClip(leftView) is not { } left || LiveClip(rightView) is not { } right)
        {
            return;
        }

        if (_viewModel.IsThroughEdit(left, right))
        {
            CrashReporter.Note("timeline", "seam: heal a through-edit");
            if (_viewModel.HealThroughEdit(left, right))
                UnifiedTimeline.Surface.SelectSeam(null);
        }
        else if (seam.CrossfadeSeconds > 0.01)
        {
            _viewModel.SetCrossfade(right, 0);
        }
        else
        {
            _viewModel.StatusText = "That cut joins two different takes: only a through-edit (one take cut in two) heals.";
        }

        ScheduleClipRedraw();
    }

    /// <summary>Comma / period on a seam: rolls the cut one Sequence frame earlier (negative) or later; one Undo step per press.</summary>
    internal void NudgeSeam(TimelineSeamTarget seam, int frames)
    {
        if (LayerOfBlock(seam.Left) is not { } leftView || LayerOfBlock(seam.Right) is not { } rightView
            || LiveClip(leftView) is not { } left || LiveClip(rightView) is not { } right)
        {
            return;
        }

        var fps = _viewModel.SequenceFps > 0 ? _viewModel.SequenceFps : 30;
        _viewModel.RollCut(left, right, right.StartTime + frames / fps);
        ScheduleClipRedraw();
    }

    /// <summary>The Seam menu's curve choice: equal power or linear for the crossfade across the cut (one Undo step).</summary>
    internal void SetSeamCurve(TimelineSeamTarget seam, CrossfadeCurve curve)
    {
        if (LayerOfBlock(seam.Left) is { } leftView && LayerOfBlock(seam.Right) is { } rightView && LiveClip(leftView) is { } left && LiveClip(rightView) is { } right)
            _viewModel.SetCrossfadeCurve(left, right, curve);
    }

    /// <summary>Ctrl+drag release: a clone of the block's clip (with its family) starting at <paramref name="seconds"/>, selected.</summary>
    private void CloneBlock(TimelineBlock block, double seconds)
    {
        if (LayerOfBlock(block) is not { } view || LiveClip(view) is not { } clip)
            return;

        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"clone (Ctrl+drag) to {seconds:0.000} s"));
        _viewModel.CloneClipTo(clip, seconds);
        ScheduleClipRedraw();
    }

    /// <summary>Top &amp; Tail: ripple-trims the selected clip's start (top) or end (tail) to the playhead, closing the gap.</summary>
    private void TopOrTailSelectedClip(bool start)
    {
        var selected = _selectedClip switch
        {
            Layer layer => layer,
            LayerSound sound => sound.Layer,
            _ => null,
        };
        if (selected is null || LiveClip(selected) is not { } clip)
        {
            _viewModel.StatusText = "Select a clip on the Timeline, then top or tail it at the playhead.";
            return;
        }

        CrashReporter.Note("timeline", start ? "top: ripple trim start to playhead" : "tail: ripple trim end to playhead");
        _viewModel.RippleTrimToPlayhead(clip, start, _viewModel.PositionMs / 1000);
        ScheduleClipRedraw();
    }
}
