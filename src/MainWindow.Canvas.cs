using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

// ----- Canvas: video overlay drawing (crop, layer target) and layer arrangement on the output frame -----
public partial class MainWindow
{
    // ----- Drawing on the video -----
    // One overlay canvas over the video, used in one mode at a time:
    //   Crop        drag a rectangle to set the Top/Bottom/Left/Right crop;
    //   DrawTarget  drag a rectangle to mark a layer's source region.
    // Layer arrangement on the output frame uses the separate Canvas pane (below).

    private enum OverlayMode
    {
        None,
        Crop,
        DrawTarget,
        Arrange,
    }

    private readonly List<UIElement> _arrangeVisuals = [];
    private readonly List<(Layer Layer, PropertyChangedEventHandler Handler)> _arrangeSubscriptions = [];
    private readonly List<Adorner> _arrangeAdorners = [];

    // Canvas pane overlay above the boxes: snap guides and the selected clip's motion path (LayoutGuidesView).
    private LayoutGuidesView? _layoutGuides;
    private Rect _layoutFrame;

    private Point? _dragStart;

    // Pending DrawTarget Undo step, taken when the first rectangle is drawn; null once taken or when none.
    private string? _drawTargetStep;

    private OverlayMode CurrentOverlayMode =>
        _viewModel.IsInteractiveCropActive ? OverlayMode.Crop
        : _viewModel.DrawTargetLayer is not null ? OverlayMode.DrawTarget
        : OverlayMode.None;

    /// <summary>
    /// Source size shown while drawing (MainViewModel.ShownSourceClip): the marked region's video during DrawTarget, else the Crop
    /// boxes' clip.
    /// </summary>
    private (int Width, int Height) ShownPictureSize() =>
        _viewModel.DrawTargetLayer is { IsVideoFile: true, ImageWidth: > 0, ImageHeight: > 0 } piece ? (piece.ImageWidth, piece.ImageHeight) : _viewModel.CropPictureSize;

    /// <summary>Letterboxed picture rect inside the player.</summary>
    private Rect GetDisplayedVideoArea(out double scale)
    {
        scale = 0;
        var (sourceWidth, sourceHeight) = ShownPictureSize();
        if (sourceWidth <= 0 || sourceHeight <= 0 || CropCanvas.ActualWidth <= 0 || CropCanvas.ActualHeight <= 0)
            return Rect.Empty;

        scale = Math.Min(CropCanvas.ActualWidth / sourceWidth, CropCanvas.ActualHeight / sourceHeight);
        var size = new Size(sourceWidth * scale, sourceHeight * scale);
        return new Rect(new Point((CropCanvas.ActualWidth - size.Width) / 2, (CropCanvas.ActualHeight - size.Height) / 2), size);
    }

    /// <summary>Shows or hides the overlay canvas for the view model's current mode.</summary>
    private void UpdateOverlayMode()
    {
        var mode = CurrentOverlayMode;
        if (mode != OverlayMode.None && ShownPictureSize().Width <= 0)
        {
            _viewModel.StatusText = "Put a video on the timeline (and select it) before drawing on it.";
            (_viewModel.IsInteractiveCropActive, _viewModel.DrawTargetLayer) = (false, null);
            return;
        }

        CropCanvas.Visibility = mode == OverlayMode.None ? Visibility.Collapsed : Visibility.Visible;

        // Player shows the clip's source while drawing, the composite otherwise (PushGpuState).
        ScheduleGpuState();

        // Red for crop, gold for a UI layer target.
        var colour = mode == OverlayMode.DrawTarget ? Colors.Gold : Colors.Red;
        CropRectangle.Stroke = Frozen(colour);
        CropRectangle.Fill = Frozen(Color.FromArgb(0x18, colour.R, colour.G, colour.B));

        _viewModel.StatusText = mode switch
        {
            OverlayMode.Crop => "Drag on the video to draw the crop. Double-click inside the rectangle to centre it.",
            OverlayMode.DrawTarget => $"Drag on the video to mark where {_viewModel.DrawTargetLayer!.Name} is.",

            _ => _viewModel.StatusText,
        };

        // Defer placement until the canvas has been measured.
        Dispatcher.BeginInvoke(RedrawOverlay, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CropCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawOverlay();

    private void RedrawOverlay()
    {
        switch (CurrentOverlayMode)
        {
            case OverlayMode.Crop:
                // Crop of the Crop boxes' clip (per-clip crop).
                var crop = _viewModel.GetTargetCropRect();
                ShowSourceRectangle(crop.Left, crop.Top, crop.Width, crop.Height);
                break;

            case OverlayMode.DrawTarget when _viewModel.DrawTargetLayer is { } element:
                var (x, y, width, height) = RegionRect(element);
                ShowSourceRectangle(x, y, width, height);
                break;


            default:
                CropRectangle.Visibility = Visibility.Collapsed;
                break;
        }
    }

    /// <summary>
    /// A region's source rect in source pixels: the crop-kept area for a file-cut piece, or the legacy source rectangle.
    /// </summary>
    private (int X, int Y, int Width, int Height) RegionRect(Layer element)
    {
        var (pictureWidth, pictureHeight) = ShownPictureSize();
        if (!element.IsVideoFile)
            return element.GetSourceRect(pictureWidth, pictureHeight);

        var (left, top, right, bottom) = Layer.HeldCrop(element.CropLeft, element.CropTop, element.CropRight, element.CropBottom);
        return ((int)Math.Round(left * pictureWidth), (int)Math.Round(top * pictureHeight),
            (int)Math.Round((1 - left - right) * pictureWidth), (int)Math.Round((1 - top - bottom) * pictureHeight));
    }

    /// <summary>Draws a source-pixel rectangle at its position on the displayed (letterboxed) video.</summary>
    private void ShowSourceRectangle(int left, int top, int width, int height)
    {
        var area = GetDisplayedVideoArea(out var scale);
        if (area.IsEmpty || width <= 0 || height <= 0)
        {
            CropRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        CropRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(CropRectangle, area.X + Math.Max(left, 0) * scale);
        Canvas.SetTop(CropRectangle, area.Y + Math.Max(top, 0) * scale);
        CropRectangle.Width = width * scale;
        CropRectangle.Height = height * scale;
    }

    private void CropCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var mode = CurrentOverlayMode;
        var area = GetDisplayedVideoArea(out _);
        if (area.IsEmpty || mode == OverlayMode.None)
            return;

        var point = e.GetPosition(CropCanvas);

        // Double-click inside the crop rect: keep size, centre on the frame.
        if (e.ClickCount == 2 && mode == OverlayMode.Crop)
        {
            var current = new Rect(Canvas.GetLeft(CropRectangle), Canvas.GetTop(CropRectangle), CropRectangle.Width, CropRectangle.Height);
            if (CropRectangle.Visibility == Visibility.Visible && current.Contains(point))
            {
                // Crop is in source percent: centring splits each opposite pair equally.
                var horizontal = _viewModel.CropLeft + _viewModel.CropRight;
                var vertical = _viewModel.CropTop + _viewModel.CropBottom;
                (_viewModel.CropLeft, _viewModel.CropRight) = (horizontal / 2, horizontal / 2);
                (_viewModel.CropTop, _viewModel.CropBottom) = (vertical / 2, vertical / 2);
            }

            _dragStart = null;
            return;
        }

        // Re-marking a layer region or drawing a crop is one Undo step, taken on the first actual change (a plain click takes none).
        _drawTargetStep = mode == OverlayMode.DrawTarget && _viewModel.DrawTargetLayer is { } target ? $"redraw the area of {target.Name}" : null;
        if (mode == OverlayMode.Crop)
            _viewModel.BeginCropDrag();

        _dragStart = ClampTo(area, point);

        // Capture loss also ends the drag, or later crop changes would get no Undo step (unsubscribe before subscribe: once only).
        CropCanvas.LostMouseCapture -= CropCanvas_LostMouseCapture;
        CropCanvas.LostMouseCapture += CropCanvas_LostMouseCapture;
        CropCanvas.CaptureMouse();
    }

    private void CropCanvas_LostMouseCapture(object sender, MouseEventArgs e) => _viewModel.EndCropDrag();

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;

        var area = GetDisplayedVideoArea(out var scale);
        if (area.IsEmpty)
            return;

        var drawn = new Rect(start, ClampTo(area, e.GetPosition(CropCanvas)));

        // Ignore jitter-sized rectangles.
        if (drawn.Width < 4 || drawn.Height < 4)
            return;

        // Round to even pixels (most encoders require even dimensions).
        var left = Even((drawn.Left - area.Left) / scale);
        var top = Even((drawn.Top - area.Top) / scale);

        if (_viewModel.DrawTargetLayer is { } element)
        {
            if (_drawTargetStep is { } step)
            {
                _drawTargetStep = null;
                _viewModel.Checkpoint(step);
            }

            // Stored as picture fractions: a file-cut piece sets its crop (its box takes the resulting shape); a legacy piece sets its
            // source rectangle.
            var (fromX, fromY) = ((drawn.Left - area.Left) / area.Width, (drawn.Top - area.Top) / area.Height);
            var (toX, toY) = ((drawn.Right - area.Left) / area.Width, (drawn.Bottom - area.Top) / area.Height);

            // One look batch per pointer move (one TimelineChanged, one command) for the four edges, as gizmo drags do.
            using (_viewModel.BatchLookWrites())
            {
                if (element.IsVideoFile)
                {
                    element.LockAspectRatio = true;
                    (element.CropLeft, element.CropTop, element.CropRight, element.CropBottom) = Layer.HeldCrop(fromX, fromY, 1 - toX, 1 - toY);
                }
                else
                {
                    (element.SourceX, element.SourceY) = (fromX, fromY);
                    (element.SourceWidth, element.SourceHeight) = (toX - fromX, toY - fromY);
                }
            }

            RedrawOverlay();
        }
        else
        {
            // Source pixels here; the view model stores source percentages. One look batch per move, as above.
            using (_viewModel.BatchLookWrites())
                _viewModel.SetCropFromPixels(left, top, Even((area.Right - drawn.Right) / scale), Even((area.Bottom - drawn.Bottom) / scale));
        }
    }

    private void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragStart is not null;
        _dragStart = null;
        CropCanvas.ReleaseMouseCapture();
        _viewModel.EndCropDrag();

        // DrawTarget is one-shot: hide the canvas after the rectangle is drawn.
        if (wasDragging && _viewModel.DrawTargetLayer is { } element)
        {
            var (x, y, width, height) = RegionRect(element);
            _viewModel.DrawTargetLayer = null;
            _viewModel.StatusText = $"{element.Name}: {width} x {height} at {x}, {y} in its video's picture.";
        }
    }

    // ----- Canvas pane: arranging layers on the output frame -----

    /// <summary>Shows or hides the Canvas per its Tabs and Panes checkbox, in place in the arrangement.</summary>
    /// <param name="announce">Post a usage hint (on explicit open, not on relayout).</param>
    private void UpdateLayoutPane(bool announce = true)
    {
        var opening = _viewModel.IsArrangeActive && !LayoutPane.IsVisible;
        _dock?.ScheduleRebuild();
        if (opening && announce)
            _viewModel.StatusText = "Drag the clips into place on the Canvas. Drag a corner handle to resize; double-click a clip to center it.";

        // Defer placement until the canvas has been measured.
        Dispatcher.BeginInvoke(RedrawLayout, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void LayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawLayout();

    private void RedrawLayout()
    {
        ClearArrangeVisuals();
        if (_viewModel.IsArrangeActive)
            BuildArrangeVisuals();
    }

    /// <summary>
    /// Builds the output-frame stage (Export Resolution aspect) with one draggable, resizable box per track clip plus the caption box
    /// when captions exist. Boxes and inspector sliders are two-way bound to the same values.
    /// </summary>
    private void BuildArrangeVisuals()
    {
        if (LayoutCanvas.ActualWidth <= 0 || LayoutCanvas.ActualHeight <= 0)
            return;

        var (frameWidth, frameHeight) = (_viewModel.FrameWidth, _viewModel.FrameHeight);
        var scale = Math.Min(LayoutCanvas.ActualWidth / frameWidth, LayoutCanvas.ActualHeight / frameHeight);
        var frame = new Rect(
            (LayoutCanvas.ActualWidth - frameWidth * scale) / 2,
            (LayoutCanvas.ActualHeight - frameHeight * scale) / 2,
            frameWidth * scale,
            frameHeight * scale);

        // Stage = output frame.
        var stage = new System.Windows.Shapes.Rectangle
        {
            Width = frame.Width,
            Height = frame.Height,
            Fill = StageBrush,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(stage, frame.X);
        Canvas.SetTop(stage, frame.Y);
        _arrangeVisuals.Add(stage);
        LayoutCanvas.Children.Add(stage);

        // Caption box added last so it is topmost, matching output order.
        if (_viewModel.AutoCaptions)
        {
            // Same move/size/centre logic as on the player (TrackLayerRect handles the caption box).
            var captions = _viewModel.CaptionLayer;
            var placeCaptions = AddArrangeLayer(
                "Subtitles", (Style)FindResource("CaptionThumb"), captions, frame, scale,
                () => TrackLayerRect(captions),
                (deltaX, deltaY) => MoveTrackLayer(captions, deltaX, deltaY, _layoutFrame, _layoutGuides),
                (deltaX, deltaY) => ResizeTrackLayer(captions, deltaX, deltaY),
                reset: () => CenterTrackLayer(captions));

            PropertyChangedEventHandler captionHandler = (_, _) => placeCaptions();
            captions.PropertyChanged += captionHandler;
            _arrangeSubscriptions.Add((captions, captionHandler));
        }

        // One box per visible track: its clip at the playhead, else its first visible clip (per-clip placement). Rebuilt when the
        // playhead reaches a different clip (SameArrangeClips).
        _arrangeClips.Clear();
        CollectArrangeClips(_arrangeClips);
        foreach (var element in _arrangeClips.ToArray())
        {
            var place = AddArrangeLayer(
                element.Name, (Style)FindResource("LayerThumb"), element, frame, scale,
                () => TrackLayerRect(element),
                (deltaX, deltaY) => MoveTrackLayer(element, deltaX, deltaY, _layoutFrame, _layoutGuides),
                (deltaX, deltaY) => ResizeTrackLayer(element, deltaX, deltaY),
                reset: () => CenterTrackLayer(element),
                rotation: () => element.HasFilters ? element.Rotation : 0,
                gizmo: element);

            PropertyChangedEventHandler handler = (_, _) => place();
            element.PropertyChanged += handler;
            _arrangeSubscriptions.Add((element, handler));
        }

        // Topmost: snap guides and the motion path (only its dots are hit-testable).
        _layoutFrame = frame;
        _layoutGuides = new LayoutGuidesView { Width = LayoutCanvas.ActualWidth, Height = LayoutCanvas.ActualHeight };
        _layoutGuides.DotMoved += (time, moved) => _viewModel.MoveMotionKey(_viewModel.ActiveKeyLayer, time, moved.X / scale / frameWidth, moved.Y / scale / frameHeight);
        _layoutGuides.DotClicked += time => _viewModel.PositionMs = Math.Clamp((_viewModel.ActiveKeyLayer.StartTime + time) * 1000, 0, Math.Max(_viewModel.DurationMs, 0));
        _arrangeVisuals.Add(_layoutGuides);
        LayoutCanvas.Children.Add(_layoutGuides);
        RefreshMotionPath();
    }

    /// <summary>
    /// Snaps a dragged box's left/centre/right (and top/middle/bottom) to the frame centre, safe margins (5% action, 10% title) or edges
    /// within 6 screen pixels, drawing the active guides.
    /// </summary>
    /// <param name="frame">Output frame on the drag surface, in surface units.</param>
    /// <param name="view">Guide host; null over the player (snaps without guides).</param>
    private static (double X, double Y) SnapToGuides(double x, double y, double width, double height, Rect frame, LayoutGuidesView? view)
    {
        double[] lines = [0, 0.05, 0.1, 0.5, 0.9, 0.95, 1];
        var guides = new List<(Point From, Point To)>();

        double Snap(double start, double size, double pixels, bool across)
        {
            var reach = pixels > 0 ? 6 / pixels : 0;
            var (best, line) = (double.MaxValue, double.NaN);
            foreach (var guide in lines)
            {
                foreach (var part in new[] { 0, 0.5, 1 })
                {
                    var off = guide - (start + size * part);
                    if (Math.Abs(off) <= reach && Math.Abs(off) < Math.Abs(best))
                        (best, line) = (off, guide);
                }
            }

            if (double.IsNaN(line))
                return start;

            guides.Add(across
                ? (new Point(frame.X + line * frame.Width, frame.Y), new Point(frame.X + line * frame.Width, frame.Bottom))
                : (new Point(frame.X, frame.Y + line * frame.Height), new Point(frame.Right, frame.Y + line * frame.Height)));
            return start + best;
        }

        var snapped = (Snap(x, width, frame.Width, across: true), Snap(y, height, frame.Height, across: false));
        view?.ShowGuides(guides);
        return snapped;
    }

    // Clips the Canvas boxes were built for (one per track), plus a scratch list for comparison.
    private readonly List<Layer> _arrangeClips = [], _arrangeScratch = [];

    /// <summary>
    /// Per track: the clip drawn at the playhead, else its first drawn clip; none for hidden tracks or hidden/transparent clips. Single
    /// allocation-free pass over track-ordered views (MainViewModel.SyncViews); runs on every playhead move.
    /// </summary>
    private void CollectArrangeClips(List<Layer> into)
    {
        into.Clear();
        var (at, total) = (_viewModel.PositionMs / 1000, _viewModel.SequenceSeconds);
        var track = int.MinValue;
        Layer? pick = null;
        var onScreen = false;
        foreach (var layer in _viewModel.Layers)
        {
            if (layer is not { IsAudio: false, IsEmptyTrack: false, IsHidden: false })
                continue;

            // Evaluate the costly drawn test only when it can change the pick (stop after a track's playhead clip; before that only a
            // playhead clip can replace its first drawn clip): O(L + T*C) per playhead move instead of testing every layer.
            var same = layer.TrackId == track;
            if (same && onScreen)
                continue;
            var hit = layer.GetSpan(total) is var (start, end) && at >= start - 0.0005 && at < end;
            if ((same && !hit) || !_viewModel.IsPictureDrawn(layer, at))
                continue;

            if (!same)
            {
                if (pick is not null)
                    into.Add(pick);
                (track, pick, onScreen) = (layer.TrackId, layer, false);
            }

            if (!onScreen && hit)
                (pick, onScreen) = (layer, true);
        }

        if (pick is not null)
            into.Add(pick);
    }

    /// <summary>True if the existing boxes still match the clips that would be picked now (after playhead or selection changes).</summary>
    private bool SameArrangeClips()
    {
        CollectArrangeClips(_arrangeScratch);
        if (_arrangeScratch.Count != _arrangeClips.Count)
            return false;
        for (var i = 0; i < _arrangeScratch.Count; i++)
        {
            if (!ReferenceEquals(_arrangeScratch[i], _arrangeClips[i]))
                return false;
        }

        return true;
    }

    // ----- Layer move/resize/centre/rotate (shared by Canvas pane and player overlay) -----

    // Each clip's box is its own placement. The caption box is its corner and size on the frame, clamped inside it.
    private (int X, int Y, int Width, int Height) TrackLayerRect(Layer element) =>
        element.GetOutputRect(_viewModel.FrameWidth, _viewModel.FrameHeight, element.IsCaptions ? 0 : _viewModel.SourceWidth, element.IsCaptions ? 0 : _viewModel.SourceHeight);

    /// <summary>Moves a track layer by an output-pixel delta, snapping to frame guides.</summary>
    private void MoveTrackLayer(Layer element, double deltaX, double deltaY, Rect frame, LayoutGuidesView? guides)
    {
        // Convert to frame fractions (sliders update; the box follows). Layers may extend off-frame (clipped, frame unchanged). Edges or
        // centre snap to the frame centre or safe margins unless Alt is held.
        var (frameWidth, frameHeight) = (_viewModel.FrameWidth, _viewModel.FrameHeight);
        var (x, y) = (element.PositionX + deltaX / frameWidth, element.PositionY + deltaY / frameHeight);
        if (AppSettings.Current.SnapLayoutGuides && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            (x, y) = SnapToGuides(x, y, element.SizeWidth, (double)TrackLayerRect(element).Height / frameHeight, frame, guides);
        var (least, most) = element.IsCaptions ? (0.0, 1.0) : (-2.0, 2.0);

        // One look batch per pointer move (one TimelineChanged, one command), however many properties the move writes.
        using (_viewModel.BatchLookWrites())
        {
            element.PositionX = Math.Clamp(x, least, most);
            element.PositionY = Math.Clamp(y, least, most);
        }
    }

    /// <summary>
    /// Resizes a track layer by an output-pixel delta of the dragged <paramref name="corner"/> in its unrotated frame (bottom-right unless
    /// a tilted clip's other corner grip is used).
    /// </summary>
    /// <param name="surfaceScale">Drag surface units per output pixel (Canvas pane or player); 0 (3D view) snaps to no guide.</param>
    private void ResizeTrackLayer(Layer element, double deltaX, double deltaY, GripCorner corner = GripCorner.BottomRight, double surfaceScale = 0)
    {
        var (frameWidth, frameHeight) = (_viewModel.FrameWidth, _viewModel.FrameHeight);

        // One look batch per pointer move (one TimelineChanged, one command) for every size/position write below.
        using var batch = _viewModel.BatchLookWrites();

        // Caption box: width and height follow the pointer from its corner, clamped to the frame.
        if (element.IsCaptions)
        {
            element.SizeWidth = Math.Clamp(element.SizeWidth + deltaX / frameWidth, 0.05, 1);
            element.SizeHeight = Math.Clamp(element.SizeHeight + deltaY / frameHeight, 0.03, 1);
            return;
        }

        // Current height: its own, or width * aspect when the shape is locked.
        var follows = element.LockAspectRatio;
        var height = follows || element.SizeHeight <= 0 ? element.SizeWidth * element.HeightPerWidth : element.SizeHeight;
        var fromOpposite = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // Tilted clips keep their drawn opposite corner fixed (TiltedCorners tilt); flat clips have zero tilt. Aspect-locked boxes take the
        // pointer's move along the corner's diagonal (DiagonalDelta), so vertical, horizontal and diagonal drags all resize smoothly.
        var (tiltX, tiltY) = DrawnTilted(element) ? (Layer.Tilt(element.TiltX), Layer.Tilt(element.TiltY)) : (0.0, 0.0);
        // The diagonal runs from the fixed point (the anchor, or the opposite corner with Ctrl) to the dragged corner, with
        // ResizedFromCorner's anchor shares, so the box scales by the projection alone (no amplification near the moving edge).
        var (right, bottom) = LayerGizmoAdorner.SidesOf(corner);
        var (shareX, shareY) = fromOpposite ? (1.0, 1.0)
            : (right == 1 ? Math.Max(1 - element.AnchorX, 0.1) : Math.Max(element.AnchorX, 0.1), bottom == 1 ? Math.Max(1 - element.AnchorY, 0.1) : Math.Max(element.AnchorY, 0.1));
        var (moveX, moveY) = follows
            ? DiagonalDelta(corner, element.SizeWidth, height, deltaX / frameWidth, deltaY / frameHeight, frameWidth, frameHeight, shareX, shareY)
            : (deltaX / frameWidth, deltaY / frameHeight);
        var resized = ResizedFromCorner(element.PositionX, element.PositionY, element.SizeWidth, height, element.AnchorX, element.AnchorY, corner,
            moveX, moveY, follows, element.HeightPerWidth, fromOpposite, frameWidth, frameHeight, element.Rotation, tiltX, tiltY);

        // Guide snapping (Alt: none) of the dragged corner's moving edges only, never the box centre; flat, unrotated boxes.
        if (surfaceScale > 0 && frameWidth > 0 && frameHeight > 0 && AppSettings.Current.SnapLayoutGuides && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
            && tiltX == 0 && tiltY == 0 && (!element.HasFilters || Math.Abs(element.Rotation % 360) < 0.05))
        {
            var (fixedX, fixedY) = fromOpposite ? (right == 1 ? 0.0 : 1.0, bottom == 1 ? 0.0 : 1.0) : (element.AnchorX, element.AnchorY);
            resized = SnappedCorner(resized, corner, fixedX, fixedY, follows, element.HeightPerWidth, 6 / (frameWidth * surfaceScale), 6 / (frameHeight * surfaceScale));
        }

        // Ctrl: opposite corner stays fixed (left/top corners shift the position as size changes; tilted clips also shift so the drawn
        // corner stays put).
        if (fromOpposite)
        {
            element.FromCorner(() =>
            {
                element.SizeWidth = resized.Width;

                // Locked: height follows width; unlocked: independent.
                if (!follows)
                    element.SizeHeight = resized.Height;
                (element.PositionX, element.PositionY) = (resized.X, resized.Y);
            });
            return;
        }

        // Default: resize about the fixed anchor point (Layer.AnchorCenter), corner position derived by ResizedFromCorner.
        element.SizeWidth = resized.Width;
        if (!follows)
            element.SizeHeight = resized.Height;
    }

    /// <summary>
    /// Pure (golden gpufx-tilt-pick): resizes a box (corner + size in frame fractions) by a corner delta (<paramref name="deltaX"/>,
    /// <paramref name="deltaY"/>) in its unrotated frame. About the anchor: size += delta / corner share, with divisor -max(anchor, 0.1) for
    /// left/top corners and 1 - anchor for right/bottom. <paramref name="fromOpposite"/> (Ctrl) fixes the opposite corner instead; for tilted
    /// clips (<paramref name="tiltX"/>/<paramref name="tiltY"/> degrees, <paramref name="rotation"/>, <paramref name="frameWidth"/> x
    /// <paramref name="frameHeight"/>) the drawn corner stays put. With <paramref name="heightFollows"/>, height = width *
    /// <paramref name="heightPerWidth"/>. Width clamped to 0.02..4, height 0.01..4.
    /// </summary>
    internal static (double X, double Y, double Width, double Height) ResizedFromCorner(double x, double y, double width, double height,
        double anchorX, double anchorY, GripCorner corner, double deltaX, double deltaY, bool heightFollows, double heightPerWidth, bool fromOpposite,
        double frameWidth = 0, double frameHeight = 0, double rotation = 0, double tiltX = 0, double tiltY = 0)
    {
        var (right, bottom) = LayerGizmoAdorner.SidesOf(corner);
        if (fromOpposite)
        {
            // Opposite-corner mode: size changes by the delta (negated for left/top); position shifts by left/top growth.
            var grownWidth = Math.Clamp(width + (right == 1 ? deltaX : -deltaX), 0.02, 4);
            var grownHeight = heightFollows ? grownWidth * heightPerWidth : Math.Clamp(height + (bottom == 1 ? deltaY : -deltaY), 0.01, 4);
            var (grownX, grownY) = (right == 1 ? x : x - (grownWidth - width), bottom == 1 ? y : y - (grownHeight - height));

            // Tilted: resizing moves the anchor, so the perspective-drawn opposite corner would shift. The quad translates with position
            // only (Matrix4.Corners about the anchor), so subtract the drawn corner's displacement from position. Flat clips skip this.
            if ((tiltX != 0 || tiltY != 0) && frameWidth > 0 && frameHeight > 0)
            {
                var opposite = (GripCorner)(((int)corner + 2) % 4);
                var before = DrawnCorner(x, y, width, height, anchorX, anchorY, opposite, frameWidth, frameHeight, rotation, tiltX, tiltY);
                var after = DrawnCorner(grownX, grownY, grownWidth, grownHeight, anchorX, anchorY, opposite, frameWidth, frameHeight, rotation, tiltX, tiltY);
                if (float.IsFinite(before.X - after.X) && float.IsFinite(before.Y - after.Y))
                    (grownX, grownY) = (grownX + (before.X - after.X) / frameWidth, grownY + (before.Y - after.Y) / frameHeight);
            }

            return (grownX, grownY, grownWidth, grownHeight);
        }

        var shareX = right == 1 ? Math.Max(1 - anchorX, 0.1) : -Math.Max(anchorX, 0.1);
        var shareY = bottom == 1 ? Math.Max(1 - anchorY, 0.1) : -Math.Max(anchorY, 0.1);
        var newWidth = Math.Clamp(width + deltaX / shareX, 0.02, 4);
        var newHeight = heightFollows ? newWidth * heightPerWidth : Math.Clamp(height + deltaY / shareY, 0.01, 4);
        return (x - (newWidth - width) * anchorX, y - (newHeight - height) * anchorY, newWidth, newHeight);
    }

    /// <summary>
    /// Pure (golden wave-contract-5-6-4a): an aspect-locked corner drag (<paramref name="deltaX"/>, <paramref name="deltaY"/>, frame
    /// fractions) projected onto the dragged corner's outward diagonal of a <paramref name="width"/> x <paramref name="height"/> box, in
    /// frame pixels so the diagonal is the drawn one. A move along the diagonal is unchanged; a vertical or horizontal move resizes by its
    /// share along it (outward grows, inward shrinks). Unknown frame size: fraction units.
    /// </summary>
    internal static (double X, double Y) DiagonalDelta(GripCorner corner, double width, double height, double deltaX, double deltaY,
        double frameWidth, double frameHeight, double shareX = 1, double shareY = 1)
    {
        // shareX/shareY: the fixed point's distance to the dragged corner as fractions of the box (1: the opposite corner).
        var (right, bottom) = LayerGizmoAdorner.SidesOf(corner);
        var (pixelsX, pixelsY) = (frameWidth > 0 ? frameWidth : 1, frameHeight > 0 ? frameHeight : 1);
        var (alongX, alongY) = ((right == 1 ? 1 : -1) * Math.Max(Math.Abs(width * shareX), 1e-6) * pixelsX, (bottom == 1 ? 1 : -1) * Math.Max(Math.Abs(height * shareY), 1e-6) * pixelsY);
        var length = Math.Sqrt(alongX * alongX + alongY * alongY);
        if (!double.IsFinite(length) || length <= 0 || !double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            return (deltaX, deltaY);

        var along = (deltaX * pixelsX * alongX + deltaY * pixelsY * alongY) / length;
        return (along * alongX / length / pixelsX, along * alongY / length / pixelsY);
    }

    /// <summary>
    /// Pure (golden wave-contract-5-6-4a): snaps a single-corner resize's moving edges (never the box centre) to the frame guides (edges,
    /// centre line, 5% and 10% safe margins) within <paramref name="reachX"/>/<paramref name="reachY"/> (frame fractions). The box scales
    /// about its fixed point at (<paramref name="fixedX"/>, <paramref name="fixedY"/>) of its size (the anchor, or the opposite corner with
    /// Ctrl); with <paramref name="heightFollows"/> the nearer edge (in its own reach) decides and the shape is kept.
    /// </summary>
    internal static (double X, double Y, double Width, double Height) SnappedCorner((double X, double Y, double Width, double Height) box, GripCorner corner,
        double fixedX, double fixedY, bool heightFollows, double heightPerWidth, double reachX, double reachY)
    {
        var (right, bottom) = LayerGizmoAdorner.SidesOf(corner);
        var (pointX, pointY) = (box.X + fixedX * box.Width, box.Y + fixedY * box.Height);
        var (shareX, shareY) = (right == 1 ? 1 - fixedX : -fixedX, bottom == 1 ? 1 - fixedY : -fixedY);
        var (width, offX) = NearestGuide(pointX, shareX, pointX + shareX * box.Width, reachX);
        var (height, offY) = NearestGuide(pointY, shareY, pointY + shareY * box.Height, reachY);
        if (heightFollows && heightPerWidth > 0)
        {
            if (!double.IsNaN(height) && (double.IsNaN(width) || offY / reachY < offX / reachX))
                width = height / heightPerWidth;
            if (double.IsNaN(width))
                return box;
            width = Math.Clamp(width, 0.02, 4);
            height = width * heightPerWidth;
        }
        else
        {
            width = double.IsNaN(width) ? box.Width : Math.Clamp(width, 0.02, 4);
            height = double.IsNaN(height) ? box.Height : Math.Clamp(height, 0.01, 4);
        }

        return (pointX - fixedX * width, pointY - fixedY * height, width, height);

        // The size that puts a moving edge (fixed point + share x size) on the nearest guide within reach, and its distance; NaN for none.
        static (double Size, double Off) NearestGuide(double point, double share, double edge, double reach)
        {
            var (size, off) = (double.NaN, double.MaxValue);
            if (Math.Abs(share) < 0.05 || !(reach > 0))
                return (size, off);
            foreach (var line in (ReadOnlySpan<double>)[0, 0.05, 0.1, 0.5, 0.9, 0.95, 1])
            {
                var (distance, wanted) = (Math.Abs(line - edge), (line - point) / share);
                if (distance <= reach && distance < off && wanted > 0)
                    (size, off) = (wanted, distance);
            }

            return (size, off);
        }
    }

    // Drawn (tilted) corner of a box in frame pixels, using TiltedCorners' Matrix4.Corners math on the in-progress box.
    private static (float X, float Y) DrawnCorner(double x, double y, double width, double height, double anchorX, double anchorY, GripCorner corner,
        double frameWidth, double frameHeight, double rotation, double tiltX, double tiltY) =>
        HandPegApp.Compositor.Matrix4.Corners((float)(x * frameWidth), (float)(y * frameHeight), (float)(width * frameWidth), (float)(height * frameHeight),
            (float)rotation, (float)anchorX, (float)anchorY, (float)frameWidth, (float)frameHeight, (float)tiltX, (float)tiltY)[(int)corner];

    /// <summary>Box double-click: centres the layer on the frame, keeping its size.</summary>
    private void CenterTrackLayer(Layer element)
    {
        var (frameWidth, frameHeight) = (_viewModel.FrameWidth, _viewModel.FrameHeight);
        var (_, _, width, height) = TrackLayerRect(element);
        element.PositionX = (frameWidth - width) / 2.0 / frameWidth;
        element.PositionY = (frameHeight - height) / 2.0 / frameHeight;
    }

    // ----- Tilted clip outline -----
    // Tilt X/Y renders a perspective quad no 2D transform can show, so the Canvas and player draw the GPU's four corners (Matrix4.Corners,
    // same math as the scene). The flat box underneath is still the drag target; grips and stalk sit on the drawn corners
    // (LayerGizmoAdorner.TiltedCorners), each corner resizing about the anchor (ResizedFromCorner). Flat clips are unaffected.

    private static readonly Brush TiltOutlineFill = FrozenCanvasBrush(Color.FromArgb(0x99, 0x00, 0x78, 0xD7));

    /// <summary>True if the clip currently renders tilted (gets a perspective outline, corner grips, and drawn-corner Ctrl resize).</summary>
    private static bool DrawnTilted(Layer clip) => clip.HasFilters && (clip.TiltX != 0 || clip.TiltY != 0);

    /// <summary>
    /// GPU-drawn corners (TL, TR, BR, BL) of a tilted clip on a surface with output <paramref name="frame"/> at <paramref name="scale"/>
    /// surface units per output pixel; null if not tilted (or captions).
    /// </summary>
    private PointCollection? TiltedCorners(Layer clip, Rect frame, double scale)
    {
        if (!DrawnTilted(clip) || _viewModel.FrameWidth <= 0 || _viewModel.FrameHeight <= 0)
            return null;

        var (x, y, width, height) = TrackLayerRect(clip);
        var corners = HandPegApp.Compositor.Matrix4.Corners(x, y, width, height, (float)clip.Rotation, (float)clip.AnchorX, (float)clip.AnchorY,
            _viewModel.FrameWidth, _viewModel.FrameHeight, (float)Layer.Tilt(clip.TiltX), (float)Layer.Tilt(clip.TiltY));
        var points = new PointCollection(corners.Length);
        foreach (var (cornerX, cornerY) in corners)
        {
            // A corner behind the camera cannot be projected: draw no outline rather than a wrong one.
            if (!float.IsFinite(cornerX) || !float.IsFinite(cornerY))
                return null;
            points.Add(new Point(frame.X + cornerX * scale, frame.Y + cornerY * scale));
        }

        points.Freeze();
        return points;
    }

    /// <summary>
    /// Shows a tilted clip's outline just above its box (created lazily, never hit-testable), or hides it when <paramref name="corners"/> is
    /// null or not <paramref name="shown"/>. Returns the outline element.
    /// </summary>
    private static System.Windows.Shapes.Polygon? PlaceTiltOutline(Panel surface, LayerBox box, System.Windows.Shapes.Polygon? outline, PointCollection? corners,
        bool shown, Brush? fill, Brush stroke, double thickness, List<UIElement>? made)
    {
        if (corners is null || !shown)
        {
            if (outline is not null)
                outline.Visibility = Visibility.Collapsed;
            return outline;
        }

        if (outline is null)
        {
            outline = new System.Windows.Shapes.Polygon { IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round };
            var at = surface.Children.IndexOf(box);
            surface.Children.Insert(at < 0 ? surface.Children.Count : at + 1, outline);
            made?.Add(outline);
        }

        (outline.Points, outline.Fill, outline.Stroke, outline.StrokeThickness, outline.Visibility) = (corners, fill, stroke, thickness, Visibility.Visible);
        return outline;
    }

    /// <summary>
    /// Attaches corner grips (resize), rotation handle (angle from anchor to pointer) and anchor dot (move within the box) to a track box
    /// on either surface (Canvas pane or player).
    /// </summary>
    private void WireGizmo(LayerGizmoAdorner adorner, FrameworkElement box, Layer gizmo, string name, UIElement surface,
        Func<(int X, int Y, int Width, int Height)> getRect, double scale, Func<Rect> frameOf)
    {
        // Tilted clips: grips on all four drawn corners, stalk on the drawn picture.
        adorner.TiltedCorners = () => TiltedCorners(gizmo, frameOf(), scale);

        // Each handle drag is one Undo step, taken on its first change (DragStep).
        var resizeStep = new DragStep(_viewModel, $"resize {name}");
        adorner.ResizeStarted += _ => resizeStep.Pressed();
        adorner.ResizeDelta += (corner, moved) =>
        {
            resizeStep.Changing();
            ResizeTrackLayer(gizmo, moved.X / scale, moved.Y / scale, corner, scale);
        };

        // Rotation handle: angle from anchor to pointer; 0 degrees = handle pointing up.
        var turnStep = new DragStep(_viewModel, $"turn {name}");
        adorner.TurnStarted += turnStep.Pressed;
        adorner.TurnDelta += () =>
        {
            if (!gizmo.HasFilters)
                return;

            turnStep.Changing();
            var anchor = box.TranslatePoint(new Point(gizmo.AnchorX * box.Width, gizmo.AnchorY * box.Height), surface);
            var pointer = Mouse.GetPosition(surface);
            var degrees = Math.Atan2(pointer.Y - anchor.Y, pointer.X - anchor.X) * 180 / Math.PI + 90;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                degrees = Math.Round(degrees / 15) * 15;
            degrees = Math.Round(degrees > 180 ? degrees - 360 : degrees, 1);
            gizmo.Rotation = degrees;
            adorner.ShowAngle(degrees);
        };

        // Anchor dot: pointer position in the unrotated box; the layer stays put on the frame. Snaps to centre, edges and corners unless Alt.
        var anchorStep = new DragStep(_viewModel, $"move the anchor point of {name}");
        adorner.AnchorStarted += anchorStep.Pressed;
        adorner.AnchorDelta += () =>
        {
            anchorStep.Changing();
            var pointer = Mouse.GetPosition(box);
            var (x, y) = (pointer.X / Math.Max(box.Width, 1), pointer.Y / Math.Max(box.Height, 1));
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                double Snap(double value, double pixels) => new[] { 0, 0.5, 1 }.FirstOrDefault(s => Math.Abs(value - s) * pixels <= 6, value);
                (x, y) = (Snap(x, box.Width), Snap(y, box.Height));
            }

            var (_, _, width, height) = getRect();
            using (_viewModel.BatchLookWrites())
                gizmo.MoveAnchor(x, y, width, height, _viewModel.FrameWidth, _viewModel.FrameHeight);
        };
    }

    /// <summary>
    /// Motion path in the Canvas pane: the anchor's trajectory between first and last position keyframes (sampled along easing) with a
    /// dot per keyframe (drag to move it, click to seek). Hidden without position keyframes or when disabled in Settings.
    /// </summary>
    private void RefreshMotionPath()
    {
        if (_layoutGuides is not { } view)
            return;

        var clip = _viewModel.ActiveKeyLayer;
        if (!AppSettings.Current.ShowMotionPath || !clip.IsPicture || clip.IsHidden
            || !(clip.HasKeys(KeyProperty.X) || clip.HasKeys(KeyProperty.Y)))
        {
            view.ShowPath([], []);
            return;
        }

        var times = clip.GetKeys(KeyProperty.X).Concat(clip.GetKeys(KeyProperty.Y)).Select(k => k.Time).Distinct().Order().ToList();
        var (frame, height) = (_layoutFrame, clip.HeightPerWidth);
        Point At(double time)
        {
            // Anchor on the frame from the position keyframes, or static if none.
            var width = clip.Evaluate(KeyProperty.Scale, time);
            var x = clip.HasKeys(KeyProperty.X) ? clip.Evaluate(KeyProperty.X, time) + clip.AnchorX * width : clip.PositionX + clip.AnchorX * clip.SizeWidth;
            var y = clip.HasKeys(KeyProperty.Y) ? clip.Evaluate(KeyProperty.Y, time) + clip.AnchorY * width * height : clip.PositionY + clip.AnchorY * clip.SizeWidth * height;
            return new Point(frame.X + x * frame.Width, frame.Y + y * frame.Height);
        }

        var path = new List<Point>();
        if (times.Count > 1)
        {
            const int Samples = 64;
            var (first, last) = (times[0], times[^1]);
            for (var i = 0; i <= Samples; i++)
                path.Add(At(first + (last - first) * i / Samples));
        }

        view.ShowPath(path, [.. times.Select(t => (t, At(t)))]);
    }

    /// <summary>
    /// Adds a draggable, resizable box (video, Cutout or image) to the canvas; returns the action that re-syncs the box to the layer.
    /// </summary>
    /// <param name="getRect">Layer rect on the output frame, in output pixels.</param>
    /// <param name="move">Moves the layer by an output-pixel delta.</param>
    /// <param name="resize">Resizes the layer by an output-pixel delta.</param>
    /// <param name="reset">Double-click action (centre on the frame).</param>
    /// <param name="gizmo">Track layer: adds rotation handle and anchor, rotates about the anchor, supports arrow-key nudges.</param>
    private Action AddArrangeLayer(
        string name, Style style, object? dataContext, Rect frame, double scale,
        Func<(int X, int Y, int Width, int Height)> getRect, Action<double, double> move, Action<double, double> resize, Action reset, Func<double>? rotation = null,
        Layer? gizmo = null)
    {
        var box = new LayerBox
        {
            Layer = gizmo ?? dataContext as Layer,
            Style = style,
            DataContext = dataContext,
            Cursor = Cursors.SizeAll,
            Takes = (self, at) => LayerBox.TakesPoint(self, HeldArrangeBox, at, IsDrawnNow),
            ToolTip = gizmo is null
                ? "Drag to move. Drag the corner handle to resize. Double-click to center."
                : "Drag to move (it snaps to the middle and the safe margins; Alt: no snapping). The corner handle resizes about the anchor point, the round handle turns it, the gold dot is the anchor. Arrow keys nudge it a pixel (Shift: ten). Double-click to center.",
        };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        var moveStep = new DragStep(_viewModel, $"move {name}");
        box.DragStarted += (_, _) => moveStep.Pressed();
        box.DragDelta += (_, e) =>
        {
            moveStep.Changing();
            move(e.HorizontalChange / scale, e.VerticalChange / scale);
        };
        var wasHeld = false;
        box.PreviewMouseLeftButtonDown += (_, _) =>
        {
            _clickThrough?.Stop();
            wasHeld = ReferenceEquals(HeldArrangeBox, box);
        };
        box.DragCompleted += (_, e) =>
        {
            _layoutGuides?.ShowGuides([]);

            // As on the player: a no-drag click on the held box cycles to the next box beneath that point.
            if (wasHeld && !e.Canceled && Math.Abs(e.HorizontalChange) + Math.Abs(e.VerticalChange) < 2)
                ClickThrough([.. LayoutCanvas.Children.OfType<LayerBox>()], box, LayoutCanvas, next => next.Focus());
        };

        // Pointer-selected box: arrows nudge 1 output pixel, 10 with Shift (MainWindow_PreviewKeyDown).
        if (gizmo is not null)
        {
            box.Focusable = true;
            box.PreviewMouseLeftButtonDown += (_, _) => box.Focus();
            box.Tag = new LayoutNudge(name, (deltaX, deltaY) =>
            {
                gizmo.PositionX = Math.Clamp(gizmo.PositionX + deltaX / _viewModel.FrameWidth, -2, 2);
                gizmo.PositionY = Math.Clamp(gizmo.PositionY + deltaY / _viewModel.FrameHeight, -2, 2);
            });
        }
        box.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            // Cancel the drag started by the double-click's second press: the box is about to recentre and a drag would pull it back.
            box.CancelDrag();
            _viewModel.Checkpoint($"center {name}");
            reset();
            e.Handled = true;
        };

        // Gizmo re-lays its handles when the anchor or box changes.
        LayerGizmoAdorner? gizmoAdorner = null;

        // Perspective outline, created on first tilt.
        System.Windows.Shapes.Polygon? tiltOutline = null;

        void Place()
        {
            var (x, y, width, height) = getRect();
            box.Width = Math.Max(width * scale, 6);
            box.Height = Math.Max(height * scale, 6);
            Canvas.SetLeft(box, frame.X + x * scale);
            Canvas.SetTop(box, frame.Y + y * scale);

            // Rotated about the anchor like the layer (drags still use frame axes).
            var angle = rotation?.Invoke() ?? 0;
            var origin = gizmo is null ? new Point(0.5, 0.5) : new Point(gizmo.AnchorX, gizmo.AnchorY);
            (box.RenderTransformOrigin, box.RenderTransform) = (origin, Math.Abs(angle % 360) < 0.05 ? Transform.Identity : new RotateTransform(angle));

            // Tilted: show the GPU quad; the flat box (still the drag target) is faint, handles on the drawn corners.
            var corners = gizmo is null ? null : TiltedCorners(gizmo, frame, scale);
            tiltOutline = PlaceTiltOutline(LayoutCanvas, box, tiltOutline, corners, shown: true, TiltOutlineFill, Brushes.White, 1, _arrangeVisuals);
            box.Opacity = corners is null ? 1 : 0.35;
            gizmoAdorner?.InvalidateArrange();
        }

        _arrangeVisuals.Add(box);
        // The caption box was created first (handlers exist) but must stay topmost: insert layers beneath it.
        var captionBox = LayoutCanvas.Children.OfType<Thumb>().FirstOrDefault(t => t.DataContext is Layer { IsCaptions: true });
        if (captionBox is not null && !ReferenceEquals(dataContext, captionBox.DataContext))
            LayoutCanvas.Children.Insert(LayoutCanvas.Children.IndexOf(captionBox), box);
        else
            LayoutCanvas.Children.Add(box);
        Place();

        // Resize handle is an adorner, so it tracks the corner through moves and resizes.
        if (AdornerLayer.GetAdornerLayer(box) is { } layer && gizmo is null)
        {
            var adorner = new ResizeAdorner(box, (Style)FindResource("LayerGrip"), name);
            var resizeStep = new DragStep(_viewModel, $"resize {name}");
            adorner.ResizeStarted += (_, _) => resizeStep.Pressed();
            adorner.ResizeDelta += (_, e) =>
            {
                resizeStep.Changing();
                resize(e.HorizontalChange / scale, e.VerticalChange / scale);
            };
            layer.Add(adorner);
            _arrangeAdorners.Add(adorner);
        }
        else if (AdornerLayer.GetAdornerLayer(box) is { } gizmoLayer && gizmo is not null)
        {
            var adorner = gizmoAdorner = new LayerGizmoAdorner(box, (Style)FindResource("LayerGrip"), name, () => new Point(gizmo.AnchorX, gizmo.AnchorY));
            WireGizmo(adorner, box, gizmo, name, LayoutCanvas, getRect, scale, () => frame);
            gizmoLayer.Add(adorner);
            _arrangeAdorners.Add(adorner);
        }

        return Place;
    }

    /// <summary>Box that owns arrow-key nudges: the last pressed box, else the selected layer's.</summary>
    private LayerBox? HeldArrangeBox
    {
        get
        {
            if (Keyboard.FocusedElement is LayerBox focused && ReferenceEquals(focused.Parent, LayoutCanvas))
                return focused;

            return HeldLayer is { } held ? LayoutCanvas.Children.OfType<LayerBox>().FirstOrDefault(b => ReferenceEquals(b.Layer, held)) : null;
        }
    }

    /// <summary>Arrow-key nudge target: Undo label and move action (output pixels).</summary>
    private sealed record LayoutNudge(string Name, Action<double, double> Move);

    /// <summary>
    /// Defers a drag's Undo step to its first actual change: Thumbs report DragStarted on press, and a select-only press (or a
    /// double-click's first press) must not leave an empty step that makes Ctrl+Z appear to do nothing.
    /// </summary>
    private sealed class DragStep(MainViewModel viewModel, string what)
    {
        private bool _taken;

        /// <summary>Arms the step: the next change opens it.</summary>
        public void Pressed() => _taken = false;

        /// <summary>Called before each drag change; the first one opens the Undo step.</summary>
        public void Changing()
        {
            if (_taken)
                return;

            _taken = true;
            viewModel.Checkpoint(what);
        }
    }

    private void ClearArrangeVisuals()
    {
        _layoutGuides = null;

        foreach (var (element, handler) in _arrangeSubscriptions)
            element.PropertyChanged -= handler;
        _arrangeSubscriptions.Clear();

        foreach (var adorner in _arrangeAdorners)
            AdornerLayer.GetAdornerLayer(adorner.AdornedElement)?.Remove(adorner);
        _arrangeAdorners.Clear();

        foreach (var visual in _arrangeVisuals)
            LayoutCanvas.Children.Remove(visual);
        _arrangeVisuals.Clear();
    }

    // ----- Canvas rubber band -----
    // Dragging on empty Canvas space draws a band; on release, intersected boxes' clips are selected (Ctrl/Shift extend), as on the
    // Timeline. A no-drag click does nothing.

    private Point? _canvasBandFrom;
    private bool _canvasBandAdds;
    private System.Windows.Shapes.Rectangle? _canvasBand;

    private static readonly Brush CanvasBandFill = FrozenCanvasBrush(Color.FromArgb(40, 0x4F, 0xC3, 0xF7));
    private static readonly Brush CanvasBandEdge = FrozenCanvasBrush(Color.FromArgb(200, 0x4F, 0xC3, 0xF7));

    private static Brush FrozenCanvasBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void HookCanvasBand()
    {
        LayoutCanvas.MouseLeftButtonDown += (_, e) =>
        {
            // Empty space only; boxes, handles and motion-path dots handle their own presses.
            if (e.Handled || !ReferenceEquals(e.OriginalSource, LayoutCanvas) || !_viewModel.IsArrangeActive || !LayoutCanvas.CaptureMouse())
                return;

            _canvasBandFrom = e.GetPosition(LayoutCanvas);
            _canvasBandAdds = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            e.Handled = true;
        };
        LayoutCanvas.MouseMove += (_, e) =>
        {
            if (_canvasBandFrom is not { } from)
                return;

            var to = e.GetPosition(LayoutCanvas);
            if (_canvasBand is null)
            {
                if (Math.Abs(to.X - from.X) < 4 && Math.Abs(to.Y - from.Y) < 4)
                    return;
                _canvasBand = new System.Windows.Shapes.Rectangle { Fill = CanvasBandFill, Stroke = CanvasBandEdge, StrokeThickness = 1, IsHitTestVisible = false };
                LayoutCanvas.Children.Add(_canvasBand);
            }

            var band = new Rect(from, to);
            Canvas.SetLeft(_canvasBand, band.X);
            Canvas.SetTop(_canvasBand, band.Y);
            (_canvasBand.Width, _canvasBand.Height) = (band.Width, band.Height);
        };
        LayoutCanvas.MouseLeftButtonUp += (_, e) =>
        {
            if (_canvasBandFrom is not { } from)
                return;

            var drawn = _canvasBand is not null;
            var band = new Rect(from, e.GetPosition(LayoutCanvas));
            EndCanvasBand();
            e.Handled = true;
            if (drawn)
                SelectCanvasBand(band);
        };
        LayoutCanvas.LostMouseCapture += (_, _) => EndCanvasBand();
    }

    private void EndCanvasBand()
    {
        _canvasBandFrom = null;
        if (_canvasBand is { } band)
            LayoutCanvas.Children.Remove(band);
        _canvasBand = null;
        if (LayoutCanvas.IsMouseCaptured)
            LayoutCanvas.ReleaseMouseCapture();
    }

    // Selects clips whose (rotated) boxes intersect the band; the topmost becomes primary.
    private void SelectCanvasBand(Rect band)
    {
        var touched = new List<Layer>();
        foreach (var box in LayoutCanvas.Children.OfType<LayerBox>())
        {
            if (box.Layer is not { } layer || !_viewModel.CanGroup(layer) || box.ActualWidth <= 0 || touched.Contains(layer))
                continue;
            if (box.TransformToAncestor(LayoutCanvas).TransformBounds(new Rect(box.RenderSize)).IntersectsWith(band))
                touched.Add(layer);
        }

        if (touched.Count == 0)
            return;

        var group = _canvasBandAdds ? SelectedGroup().Concat(touched).Distinct().ToList() : touched;
        _rangeAnchor = null;
        _viewModel.SetClipSelection(group);
        SelectClip(touched[^1], keepGroup: true);
        _viewModel.StatusText = group.Count == 1 ? $"{group[0].Name} selected." : $"{group.Count} clips selected on the Canvas: Ctrl+C copies them, Ctrl+D duplicates them.";
    }
}
