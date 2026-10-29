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

namespace HandPegApp;

// ----- Canvas: the overlay drawn on the video (crop, layer target) and arranging layers on the output frame -----
public partial class MainWindow
{
    // ----- Drawing on the video -----
    // One canvas lies over the video and serves three jobs, one at a time:
    //   Crop        drag a rectangle to set the Top/Bottom/Left/Right crop;
    //   DrawTarget  drag a rectangle to mark where a layer is in the source.
    // Arranging the layers on the output frame has a pane of its own beside the player; see further down.

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

    // What the layout pane draws over its boxes: snap guides and the selected clip's motion path (see LayoutGuidesView).
    private LayoutGuidesView? _layoutGuides;
    private Rect _layoutFrame;

    // Puts the centre video's box where the view model says it is; set while Arrange is showing.
    private Action? _placeCenter;
    private Point? _dragStart;

    private OverlayMode CurrentOverlayMode =>
        _viewModel.IsInteractiveCropActive ? OverlayMode.Crop
        : _viewModel.DrawTargetLayer is not null ? OverlayMode.DrawTarget
        : OverlayMode.None;

    /// <summary>Where the picture actually is inside the player: the video is letterboxed to keep its shape.</summary>
    private Rect GetDisplayedVideoArea(out double scale)
    {
        scale = 0;
        double sourceWidth = _viewModel.SourceWidth, sourceHeight = _viewModel.SourceHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0 || CropCanvas.ActualWidth <= 0 || CropCanvas.ActualHeight <= 0)
            return Rect.Empty;

        scale = Math.Min(CropCanvas.ActualWidth / sourceWidth, CropCanvas.ActualHeight / sourceHeight);
        var size = new Size(sourceWidth * scale, sourceHeight * scale);
        return new Rect(new Point((CropCanvas.ActualWidth - size.Width) / 2, (CropCanvas.ActualHeight - size.Height) / 2), size);
    }

    /// <summary>Shows or hides the canvas for the mode the view model is now in.</summary>
    private void UpdateOverlayMode()
    {
        var mode = CurrentOverlayMode;
        if (mode != OverlayMode.None && _viewModel.SourceWidth <= 0)
        {
            _viewModel.StatusText = "Load a video before drawing on it.";
            (_viewModel.IsInteractiveCropActive, _viewModel.DrawTargetLayer) = (false, null);
            return;
        }

        CropCanvas.Visibility = mode == OverlayMode.None ? Visibility.Collapsed : Visibility.Visible;
        ApplyLiveFilter();


        // Red for the crop, gold for a UI layer, so it is clear what is being drawn.
        var colour = mode == OverlayMode.DrawTarget ? Colors.Gold : Colors.Red;
        CropRectangle.Stroke = Frozen(colour);
        CropRectangle.Fill = Frozen(Color.FromArgb(0x18, colour.R, colour.G, colour.B));

        _viewModel.StatusText = mode switch
        {
            OverlayMode.Crop => "Drag on the video to draw the crop. Double-click inside the rectangle to centre it.",
            OverlayMode.DrawTarget => $"Drag on the video to mark where {_viewModel.DrawTargetLayer!.Name} is.",

            _ => _viewModel.StatusText,
        };

        // Let the canvas take its size before anything is placed on it.
        Dispatcher.BeginInvoke(RedrawOverlay, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CropCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawOverlay();

    private void RedrawOverlay()
    {
        switch (CurrentOverlayMode)
        {
            case OverlayMode.Crop:
                var crop = _viewModel.GetCropRect();
                ShowSourceRectangle(crop.Left, crop.Top, crop.Width, crop.Height);
                break;

            case OverlayMode.DrawTarget when _viewModel.DrawTargetLayer is { } element:
                var (x, y, width, height) = element.GetSourceRect(_viewModel.SourceWidth, _viewModel.SourceHeight);
                ShowSourceRectangle(x, y, width, height);
                break;


            default:
                CropRectangle.Visibility = Visibility.Collapsed;
                break;
        }
    }

    /// <summary>Draws a rectangle given in source pixels at the matching place on the displayed video.</summary>
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

        // Double-click inside the crop rectangle: keep its size, centre it on the frame.
        if (e.ClickCount == 2 && mode == OverlayMode.Crop)
        {
            var current = new Rect(Canvas.GetLeft(CropRectangle), Canvas.GetTop(CropRectangle), CropRectangle.Width, CropRectangle.Height);
            if (CropRectangle.Visibility == Visibility.Visible && current.Contains(point))
            {
                // The crop is in percent of the source, so centring is sharing each pair out equally.
                var horizontal = _viewModel.CropLeft + _viewModel.CropRight;
                var vertical = _viewModel.CropTop + _viewModel.CropBottom;
                (_viewModel.CropLeft, _viewModel.CropRight) = (horizontal / 2, horizontal / 2);
                (_viewModel.CropTop, _viewModel.CropBottom) = (vertical / 2, vertical / 2);
            }

            _dragStart = null;
            return;
        }

        // Marking a layer's area again is a step of Undo.
        if (mode == OverlayMode.DrawTarget && _viewModel.DrawTargetLayer is { } target)
            _viewModel.Checkpoint($"redraw the area of {target.Name}");

        _dragStart = ClampTo(area, point);
        CropCanvas.CaptureMouse();
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;

        var area = GetDisplayedVideoArea(out var scale);
        if (area.IsEmpty)
            return;

        var drawn = new Rect(start, ClampTo(area, e.GetPosition(CropCanvas)));

        // A click with a trembling hand is not a rectangle.
        if (drawn.Width < 4 || drawn.Height < 4)
            return;

        // Even numbers of pixels: most encoders need even frame sizes.
        var left = Even((drawn.Left - area.Left) / scale);
        var top = Even((drawn.Top - area.Top) / scale);

        if (_viewModel.DrawTargetLayer is { } element)
        {
            // Kept as fractions of the frame, which is all the drawn rectangle really says.
            (element.SourceX, element.SourceY) = ((drawn.Left - area.Left) / area.Width, (drawn.Top - area.Top) / area.Height);
            (element.SourceWidth, element.SourceHeight) = (drawn.Width / area.Width, drawn.Height / area.Height);
            RedrawOverlay();
        }
        else
        {
            // Source pixels here; the view model keeps them as percentages of the source.
            _viewModel.SetCropFromPixels(left, top, Even((area.Right - drawn.Right) / scale), Even((area.Bottom - drawn.Bottom) / scale));
        }
    }

    private void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragStart is not null;
        _dragStart = null;
        CropCanvas.ReleaseMouseCapture();

        // Marking a layer is a one-shot job: once the rectangle is drawn, the canvas goes away again.
        if (wasDragging && _viewModel.DrawTargetLayer is { } element)
        {
            _viewModel.DrawTargetLayer = null;
            var (x, y, width, height) = element.GetSourceRect(_viewModel.SourceWidth, _viewModel.SourceHeight);
            _viewModel.StatusText = $"{element.Name}: {width} x {height} at {x}, {y} in the source.";
        }
    }

    // ----- The Canvas (the layout pane until 4.2): arranging layers on the output frame -----

    /// <summary>Opens or closes the Canvas, following its box in Tabs and Panes: it is shown or hidden where the arrangement has it.</summary>
    /// <param name="announce">Whether to say how the pane is used: when it has just been opened, not when the panes are laid out again.</param>
    private void UpdateLayoutPane(bool announce = true)
    {
        var opening = _viewModel.IsArrangeActive && !LayoutPane.IsVisible;
        _dock?.ScheduleRebuild();
        if (opening && announce)
            _viewModel.StatusText = "Drag the layers into place on the Canvas. Drag a corner handle to resize; double-click a layer to center it.";

        // Let the canvas take its size before anything is placed on it.
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
    /// Draws the output frame, in whatever shape Resolution &amp; Cropping on the Video tab gives it, with a box
    /// for each layer: the sharp centre video and the layers when the engine is on, and the caption box
    /// when auto-captions are. Every box is dragged to move its layer, and carries a resize handle on its
    /// corner. The boxes and the sliders show the same numbers: moving either moves the other.
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

        // The stage: the output frame itself.
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

        // The sharp video first, so that it lies under the layers as it does in the output (none while its picture is hidden).
        // Its track's box, as every track has one (4.3D): its clips share the place the Center controls give them, and the
        // box moves, sizes, turns and anchors them as any track's box does its clips.
        if (_viewModel.FrameEngine && !_viewModel.MainLayer.IsHidden)
        {
            var main = _viewModel.MainLayer;
            _placeCenter = AddArrangeLayer(
                "Center Video", (Style)FindResource("CenterThumb"), dataContext: null, frame, scale,
                () => TrackLayerRect(main),
                (deltaX, deltaY) => MoveTrackLayer(main, deltaX, deltaY, _layoutFrame, _layoutGuides),
                (deltaX, deltaY) => ResizeTrackLayer(main, deltaX, deltaY),
                reset: () => _viewModel.ResetCenterVideoCommand.Execute(null),
                rotation: () => main.HasFilters ? main.Rotation : 0,
                gizmo: main);

            // Turned in its dialog, the box turns with it.
            PropertyChangedEventHandler mainHandler = (_, _) => _placeCenter?.Invoke();
            _viewModel.MainLayer.PropertyChanged += mainHandler;
            _arrangeSubscriptions.Add((_viewModel.MainLayer, mainHandler));
        }

        // Last, so that it lies on top of everything, as the captions do in the output.
        if (_viewModel.AutoCaptions)
        {
            var captions = _viewModel.CaptionLayer;
            var placeCaptions = AddArrangeLayer(
                "Subtitles", (Style)FindResource("CaptionThumb"), captions, frame, scale,
                () => captions.GetOutputRect(frameWidth, frameHeight, 0, 0),
                (deltaX, deltaY) =>
                {
                    captions.PositionX = Math.Clamp(captions.PositionX + deltaX / frameWidth, 0, 1);
                    captions.PositionY = Math.Clamp(captions.PositionY + deltaY / frameHeight, 0, 1);
                },
                (deltaX, deltaY) =>
                {
                    captions.SizeWidth = Math.Clamp(captions.SizeWidth + deltaX / frameWidth, 0.05, 1);
                    captions.SizeHeight = Math.Clamp(captions.SizeHeight + deltaY / frameHeight, 0.03, 1);
                },
                reset: () =>
                {
                    captions.PositionX = Math.Clamp((1 - captions.SizeWidth) / 2, 0, 1);
                    captions.PositionY = Math.Clamp((1 - captions.SizeHeight) / 2, 0, 1);
                });

            PropertyChangedEventHandler captionHandler = (_, _) => placeCaptions();
            captions.PropertyChanged += captionHandler;
            _arrangeSubscriptions.Add((captions, captionHandler));
        }

        // One box per track: its clips share a place, a size and a look. A hidden track has none. The main video's is the
        // Center Video box above (its place is the Center controls'), never a box of its own.
        foreach (var element in _viewModel.FrameEngine ? _viewModel.Layers.Where(l => l is { IsAudio: false, IsEmptyTrack: false, IsMainVideo: false }).GroupBy(l => l.TrackId)
                     .Where(t => t.Any(c => !c.IsHidden)).Select(t => t.First()).ToList() : [])
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

        // Over everything: the snap guides and the selected clip's motion path (its dots answer the pointer, nothing else).
        _layoutFrame = frame;
        _layoutGuides = new LayoutGuidesView { Width = LayoutCanvas.ActualWidth, Height = LayoutCanvas.ActualHeight };
        _layoutGuides.DotMoved += (time, moved) => _viewModel.MoveMotionKey(_viewModel.ActiveKeyLayer, time, moved.X / scale / frameWidth, moved.Y / scale / frameHeight);
        _layoutGuides.DotClicked += time => _viewModel.PositionMs = Math.Clamp((_viewModel.ActiveKeyLayer.StartTime + time) * 1000, 0, Math.Max(_viewModel.DurationMs, 0));
        _arrangeVisuals.Add(_layoutGuides);
        LayoutCanvas.Children.Add(_layoutGuides);
        RefreshMotionPath();
    }

    /// <summary>
    /// A dragged box held to the guides it comes near: its left edge, middle or right edge to the frame's middle or a
    /// safe margin (5% action-safe, 10% title-safe) or edge, and the same down the frame, within six pixels on screen.
    /// The guides it is held to are drawn while it is.
    /// </summary>
    /// <param name="frame">The output frame on the surface the box is dragged on, in its units.</param>
    /// <param name="view">Where the guides are drawn; none over the player (4.1.1-B), which only snaps.</param>
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

    // ----- A track's layer moved, resized, centred and turned: the same in the layout pane and over the player -----

    // The main video's track is placed by the Center controls (4.3D): its box is theirs, and moving or sizing it moves them,
    // which every clip of the main video follows, as a track's clips follow its box.
    private (int X, int Y, int Width, int Height) TrackLayerRect(Layer element) => element.IsMainVideo
        ? _viewModel.GetCenterRect()
        : element.GetOutputRect(_viewModel.FrameWidth, _viewModel.FrameHeight, _viewModel.SourceWidth, _viewModel.SourceHeight);

    /// <summary>Moves a track's layer by a distance in output pixels, snapping to the guides of the frame it is dragged on.</summary>
    private void MoveTrackLayer(Layer element, double deltaX, double deltaY, Rect frame, LayoutGuidesView? guides)
    {
        if (element.IsMainVideo)
        {
            var (frameW, frameH) = (_viewModel.FrameWidth, _viewModel.FrameHeight);
            var (left, top, width, height) = _viewModel.GetCenterRect();
            var (toX, toY) = ((left + deltaX) / frameW, (top + deltaY) / frameH);
            if (AppSettings.Current.SnapLayoutGuides && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                (toX, toY) = SnapToGuides(toX, toY, (double)width / frameW, (double)height / frameH, frame, guides);
            _viewModel.MoveCenter(toX * frameW - left, toY * frameH - top);
            return;
        }

        // Output pixels to fractions of the frame. Setting them moves the sliders, and the box follows them.
        // A layer may be dragged off the frame: what is outside is cut off, and the frame stays as it is.
        // Near the frame's middle or a safe margin, an edge or the middle of the box snaps to it (not with Alt).
        var (frameWidth, frameHeight) = (_viewModel.FrameWidth, _viewModel.FrameHeight);
        var (x, y) = (element.PositionX + deltaX / frameWidth, element.PositionY + deltaY / frameHeight);
        if (AppSettings.Current.SnapLayoutGuides && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            (x, y) = SnapToGuides(x, y, element.SizeWidth, (double)TrackLayerRect(element).Height / frameHeight, frame, guides);
        element.PositionX = Math.Clamp(x, -2, 2);
        element.PositionY = Math.Clamp(y, -2, 2);
    }

    /// <summary>Grows a track's layer by a distance in output pixels, dragged by its corner.</summary>
    private void ResizeTrackLayer(Layer element, double deltaX, double deltaY)
    {
        if (element.IsMainVideo)
        {
            _viewModel.ResizeCenter(deltaX);
            return;
        }

        var (frameWidth, frameHeight) = (_viewModel.FrameWidth, _viewModel.FrameHeight);

        // Ctrl: dragged by its corner, the opposite corner is what stays put.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            element.FromCorner(() =>
            {
                element.SizeWidth = Math.Clamp(element.SizeWidth + deltaX / frameWidth, 0.02, 4);

                // Locked, the height follows the width by itself; unlocked, it is dragged separately.
                if (!element.LockAspectRatio)
                    element.SizeHeight = Math.Clamp(element.SizeHeight + deltaY / frameHeight, 0.01, 4);
            });
            return;
        }

        // Otherwise about the anchor point, which stays where it is: the corner follows the pointer, so the size
        // grows by the pointer's move over the corner's share of it.
        element.SizeWidth = Math.Clamp(element.SizeWidth + deltaX / frameWidth / Math.Max(1 - element.AnchorX, 0.1), 0.02, 4);
        if (!element.LockAspectRatio)
            element.SizeHeight = Math.Clamp(element.SizeHeight + deltaY / frameHeight / Math.Max(1 - element.AnchorY, 0.1), 0.01, 4);
    }

    /// <summary>A double-click on a track's box: the layer keeps its size and goes to the middle of the frame.</summary>
    private void CenterTrackLayer(Layer element)
    {
        if (element.IsMainVideo)
        {
            (_viewModel.CenterOffsetX, _viewModel.CenterOffsetY) = (0, 0);
            return;
        }

        var (frameWidth, frameHeight) = (_viewModel.FrameWidth, _viewModel.FrameHeight);
        var (_, _, width, height) = TrackLayerRect(element);
        element.PositionX = (frameWidth - width) / 2.0 / frameWidth;
        element.PositionY = (frameHeight - height) / 2.0 / frameHeight;
    }

    /// <summary>
    /// The rotation handle and the anchor dot of a track's box, on whatever surface the box lies (the layout pane's
    /// canvas or the player's): the angle from the anchor point to the pointer, and the anchor moved inside the box.
    /// </summary>
    private void WireGizmo(LayerGizmoAdorner adorner, FrameworkElement box, Layer gizmo, string name, UIElement surface,
        Func<(int X, int Y, int Width, int Height)> getRect, Action<double, double> resize, double scale)
    {
        adorner.ResizeStarted += (_, _) => _viewModel.Checkpoint($"resize {name}");
        adorner.ResizeDelta += (_, e) => resize(e.HorizontalChange / scale, e.VerticalChange / scale);

        // The rotation handle: the angle from the anchor point to the pointer, the handle pointing up at 0 degrees.
        adorner.TurnStarted += () => _viewModel.Checkpoint($"turn {name}");
        adorner.TurnDelta += () =>
        {
            if (!gizmo.HasFilters)
                return;

            var anchor = box.TranslatePoint(new Point(gizmo.AnchorX * box.Width, gizmo.AnchorY * box.Height), surface);
            var pointer = Mouse.GetPosition(surface);
            var degrees = Math.Atan2(pointer.Y - anchor.Y, pointer.X - anchor.X) * 180 / Math.PI + 90;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                degrees = Math.Round(degrees / 15) * 15;
            degrees = Math.Round(degrees > 180 ? degrees - 360 : degrees, 1);
            gizmo.Rotation = degrees;
            adorner.ShowAngle(degrees);
        };

        // The anchor dot: where the pointer is in the (unturned) box; the layer stays where it is on the frame. It snaps
        // to the middle, the edges and the corners (not with Alt).
        adorner.AnchorStarted += () => _viewModel.Checkpoint($"move the anchor point of {name}");
        adorner.AnchorDelta += () =>
        {
            var pointer = Mouse.GetPosition(box);
            var (x, y) = (pointer.X / Math.Max(box.Width, 1), pointer.Y / Math.Max(box.Height, 1));
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                double Snap(double value, double pixels) => new[] { 0, 0.5, 1 }.FirstOrDefault(s => Math.Abs(value - s) * pixels <= 6, value);
                (x, y) = (Snap(x, box.Width), Snap(y, box.Height));
            }

            var (_, _, width, height) = getRect();
            gizmo.MoveAnchor(x, y, width, height, _viewModel.FrameWidth, _viewModel.FrameHeight);
        };
    }

    /// <summary>
    /// The selected clip's motion path in the layout pane: where its anchor point goes between its first and last place
    /// keyframes (sampled along their easing), with a dot at each, to drag (the keyframe's place moves with it) or click
    /// (the playhead goes to it). Nothing while the clip's place has no keyframes, or Settings hides the path.
    /// </summary>
    private void RefreshMotionPath()
    {
        if (_layoutGuides is not { } view)
            return;

        var clip = _viewModel.ActiveKeyLayer;
        if (!AppSettings.Current.ShowMotionPath || !_viewModel.FrameEngine || !clip.IsPicture || clip.IsHidden
            || !(clip.HasKeys(KeyProperty.X) || clip.HasKeys(KeyProperty.Y)))
        {
            view.ShowPath([], []);
            return;
        }

        var times = clip.GetKeys(KeyProperty.X).Concat(clip.GetKeys(KeyProperty.Y)).Select(k => k.Time).Distinct().Order().ToList();
        var (frame, height) = (_layoutFrame, clip.HeightPerWidth);
        Point At(double time)
        {
            // The anchor point on the frame: from the corner's keyframes, or (a place with none) where the anchor holds still.
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
    /// Adds one draggable, resizable layer to the canvas: the same for the centre video, a piece of the
    /// video and an image. Returns the action that puts its box where the layer now is.
    /// </summary>
    /// <param name="getRect">The layer's place on the output frame, in output pixels.</param>
    /// <param name="move">Moves the layer by a distance in output pixels.</param>
    /// <param name="resize">Grows the layer by a distance in output pixels.</param>
    /// <param name="reset">What a double-click does: puts the layer back in the middle of the frame.</param>
    /// <param name="gizmo">A track's layer: its box gets the rotation handle and the anchor point, turns about that point, and nudges with the arrow keys.</param>
    private Action AddArrangeLayer(
        string name, Style style, object? dataContext, Rect frame, double scale,
        Func<(int X, int Y, int Width, int Height)> getRect, Action<double, double> move, Action<double, double> resize, Action reset, Func<double>? rotation = null,
        Layer? gizmo = null)
    {
        var box = new Thumb
        {
            Style = style,
            DataContext = dataContext,
            Cursor = Cursors.SizeAll,
            ToolTip = gizmo is null
                ? "Drag to move. Drag the corner handle to resize. Double-click to center."
                : "Drag to move (it snaps to the middle and the safe margins; Alt: no snapping). The corner handle resizes about the anchor point, the round handle turns it, the gold dot is the anchor. Arrow keys nudge it a pixel (Shift: ten). Double-click to center.",
        };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        box.DragStarted += (_, _) => _viewModel.Checkpoint($"move {name}");
        box.DragDelta += (_, e) => move(e.HorizontalChange / scale, e.VerticalChange / scale);
        box.DragCompleted += (_, _) => _layoutGuides?.ShowGuides([]);

        // Chosen with the pointer, the arrow keys nudge it (see MainWindow_PreviewKeyDown): a pixel of the output, ten with Shift.
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

            // The second press of a double-click also starts a drag. It is called off first: the box is
            // about to jump out from under the pointer, and a drag would carry it straight back.
            box.CancelDrag();
            _viewModel.Checkpoint($"center {name}");
            reset();
            e.Handled = true;
        };

        // The gizmo (a track's box): told to lay its handles out again when the anchor or the box changes.
        LayerGizmoAdorner? gizmoAdorner = null;

        void Place()
        {
            var (x, y, width, height) = getRect();
            box.Width = Math.Max(width * scale, 6);
            box.Height = Math.Max(height * scale, 6);
            Canvas.SetLeft(box, frame.X + x * scale);
            Canvas.SetTop(box, frame.Y + y * scale);

            // Shown turned as the layer is, about its anchor point. (It is still dragged along the frame's own axes.)
            var angle = rotation?.Invoke() ?? 0;
            var origin = gizmo is null ? new Point(0.5, 0.5) : new Point(gizmo.AnchorX, gizmo.AnchorY);
            (box.RenderTransformOrigin, box.RenderTransform) = (origin, Math.Abs(angle % 360) < 0.05 ? Transform.Identity : new RotateTransform(angle));
            gizmoAdorner?.InvalidateArrange();
        }

        _arrangeVisuals.Add(box);
        // The caption box was made first so that its handlers exist, but it belongs on top: layers go under it.
        var captionBox = LayoutCanvas.Children.OfType<Thumb>().FirstOrDefault(t => t.DataContext is Layer { IsCaptions: true });
        if (captionBox is not null && !ReferenceEquals(dataContext, captionBox.DataContext))
            LayoutCanvas.Children.Insert(LayoutCanvas.Children.IndexOf(captionBox), box);
        else
            LayoutCanvas.Children.Add(box);
        Place();

        // The handle is an adorner on the box: it stays on the corner however the box moves or grows.
        if (AdornerLayer.GetAdornerLayer(box) is { } layer && gizmo is null)
        {
            var adorner = new ResizeAdorner(box, (Style)FindResource("LayerGrip"), name);
            adorner.ResizeStarted += (_, _) => _viewModel.Checkpoint($"resize {name}");
            adorner.ResizeDelta += (_, e) => resize(e.HorizontalChange / scale, e.VerticalChange / scale);
            layer.Add(adorner);
            _arrangeAdorners.Add(adorner);
        }
        else if (AdornerLayer.GetAdornerLayer(box) is { } gizmoLayer && gizmo is not null)
        {
            var adorner = gizmoAdorner = new LayerGizmoAdorner(box, (Style)FindResource("LayerGrip"), name, () => new Point(gizmo.AnchorX, gizmo.AnchorY));
            WireGizmo(adorner, box, gizmo, name, LayoutCanvas, getRect, resize, scale);
            gizmoLayer.Add(adorner);
            _arrangeAdorners.Add(adorner);
        }

        return Place;
    }

    /// <summary>A layout box's arrow-key nudge: what it moves (its name, for Undo), and how, in pixels of the output.</summary>
    private sealed record LayoutNudge(string Name, Action<double, double> Move);

    private void ClearArrangeVisuals()
    {
        _placeCenter = null;
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
}
