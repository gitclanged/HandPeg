using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Edit Clips on Video Preview (4.1.1-B): the clips arranged on the player itself, as in the layout pane. Over the
/// picture, in the window the video surface keeps above the video, each track's clip on screen at the playhead has a
/// box where it is drawn: nearly see-through, so a click on a clip selects it (and a drag moves it straight away).
/// Only the selected clip's box is outlined and has the handles (corner grip, rotation handle, anchor dot), and only
/// while paused: the video is never cluttered with the others. A click on the video away from it hides the outline
/// until another clip is selected. Moving, resizing, turning and the anchor go through the same code as the layout
/// pane's (MoveTrackLayer, ResizeTrackLayer, WireGizmo). Shown over the composite (not while a rectangle is drawn). The caption box
/// is one of them while captions are on (5.0-Verify, the user's bug 3): on top, moved and sized like the Canvas's (no turn,
/// no anchor), selected by a press on it or on any caption on the timeline.
/// </summary>
public partial class MainWindow
{
    private readonly List<(LayerBox Box, Layer Clip, Action Place)> _playerBoxes = [];
    private readonly List<(Layer Layer, PropertyChangedEventHandler Handler)> _playerSubscriptions = [];
    private Adorner? _playerGizmo;
    private Rect _playerFrame;
    private double _playerScale;

    // A click on the video away from the selected layer hides its outline, until a layer is selected again.
    private bool _playerOutlineHidden;

    private static readonly SolidColorBrush PlayerHitBrush = FrozenBrush(Color.FromArgb(0x01, 0, 0, 0));

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void InitializePlayerLayout()
    {
        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.KeyLayer):
                    _playerOutlineHidden = false;
                    UpdatePlayerSelection();

                    // Another clip selected may be another box for the Canvas (4.4).
                    if (_viewModel.IsArrangeActive && !SameArrangeClips())
                        RedrawLayout();
                    break;
                case nameof(MainViewModel.PositionMs):
                    // Another clip may be on screen now: rebuilt only when the set of clips shown is not the same. The
                    // Canvas's boxes are each track's clip at the playhead too (4.4). Not under a drag: after it.
                    if (_dragHeld)
                        _redrawAfterDrag = true;
                    else
                        RedrawForClipsShown();
                    break;
                case nameof(MainViewModel.EditLayersOnPlayer)
                    or nameof(MainViewModel.IsPlaying) or nameof(MainViewModel.AutoCaptions) or nameof(MainViewModel.HasSource)
                    or nameof(MainViewModel.FrameWidth) or nameof(MainViewModel.FrameHeight)
                    or nameof(MainViewModel.IsInteractiveCropActive) or nameof(MainViewModel.DrawTargetLayer):
                    RedrawPlayerLayout();
                    break;
            }
        };
        _viewModel.Layers.CollectionChanged += (_, _) => RedrawPlayerLayout();

        // A drag of a box, a gizmo or a slider (in any window): while it lasts, the boxes are not rebuilt under the pointer as
        // the playhead brings other clips, and the Crop boxes keep standing for the clip they were taken from (4.4); both catch
        // up when it ends. The seek bar's and the scroll bars' drags hold nothing.
        EventManager.RegisterClassHandler(typeof(Thumb), Thumb.DragStartedEvent, new DragStartedEventHandler((sender, _) => HoldForDrag(sender, true)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Thumb), Thumb.DragCompletedEvent, new DragCompletedEventHandler((sender, _) => HoldForDrag(sender, false)), handledEventsToo: true);

        // A slider or a number box bound straight to a value that shapes a clip's edge, in any window (Layer Properties, the
        // mask tools): while the pointer holds it, the player magnifies that clip's edge (the loupe, 5.0-Verify, the user's
        // bug 6). The Effects pane's cards say so themselves (their values are bound to the cards).
        EventManager.RegisterClassHandler(typeof(Slider), PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((sender, _) => HoldBoundEdge(sender, true)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Slider), PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((sender, _) => HoldBoundEdge(sender, false)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Slider), Mouse.LostMouseCaptureEvent, new MouseEventHandler((sender, _) => HoldBoundEdge(sender, false)), handledEventsToo: true);
        NumericInput.DragHeld += (box, held) => HoldBoundEdge(box, held);

        // Ping-to-Mark (5.2C): Alt + a click on the picture (pressed and let go without a drag: Alt + a drag still moves a clip without
        // snapping) drops a highlight at the playhead, where on the picture it was clicked kept on it.
        PlayerOverlay.PreviewMouseLeftButtonDown += (_, e) => _altPressAt = Keyboard.Modifiers == ModifierKeys.Alt && !_explodedUi ? e.GetPosition(PlayerOverlay) : null;
        PlayerOverlay.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_altPressAt is not { } pressed)
                return;
            _altPressAt = null;
            var at = e.GetPosition(PlayerOverlay);
            if (Keyboard.Modifiers != ModifierKeys.Alt || Math.Abs(at.X - pressed.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(at.Y - pressed.Y) > SystemParameters.MinimumVerticalDragDistance || PictureFocalAt(at) is not { } focal)
                return;
            RunGuarded("Ping-to-Mark", () => _viewModel.PingToMark(focal.X, focal.Y));
        };

        // A right-click on the picture that does not move: the player's menu (5.4.4-Final, MainWindow.PlayerMenu.cs).
        HookPlayerContextMenu();
    }

    // Where an Alt press on the player was (Ping-to-Mark), while it is held.
    private Point? _altPressAt;

    /// <summary>A point of the player (its overlay's) as a place on the picture: fractions of the output frame across and down; null off the picture.</summary>
    private Point? PictureFocalAt(Point at)
    {
        var (width, height) = (PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight);
        var (frameWidth, frameHeight) = ((double)_viewModel.FrameWidth, (double)_viewModel.FrameHeight);
        if (width <= 0 || height <= 0 || frameWidth <= 0 || frameHeight <= 0)
            return null;

        // The picture as the GPU engine fits it into the player: the output frame, letterboxed (as RedrawPlayerLayout lays the boxes).
        var scale = Math.Min(width / frameWidth, height / frameHeight);
        var (left, top) = ((width - frameWidth * scale) / 2, (height - frameHeight * scale) / 2);
        var (x, y) = ((at.X - left) / (frameWidth * scale), (at.Y - top) / (frameHeight * scale));
        return x is >= 0 and <= 1 && y is >= 0 and <= 1 ? new Point(x, y) : null;
    }

    // Whether the edge being tuned was begun by a bound slider or box (HoldBoundEdge): only its own letting go ends it.
    private bool _boundEdgeHeld;

    private void HoldBoundEdge(object sender, bool held)
    {
        if (!held)
        {
            if (_boundEdgeHeld)
            {
                _boundEdgeHeld = false;
                _viewModel.EndEdgeTuning();
            }

            return;
        }

        var bound = sender is Slider ? RangeBase.ValueProperty : TextBox.TextProperty;
        if (sender is not FrameworkElement control || System.Windows.Data.BindingOperations.GetBindingExpression(control, bound) is not { ResolvedSource: Layer layer } binding)
            return;

        var kind = binding.ResolvedSourcePropertyName switch
        {
            nameof(Layer.FeatherRadius) or nameof(Layer.CornerRadius) => EdgeTuningKind.Feather,
            nameof(Layer.MaskGrow) or nameof(Layer.MaskOffsetX) or nameof(Layer.MaskOffsetY) => EdgeTuningKind.Mask,
            nameof(Layer.ChromaSimilarity) or nameof(Layer.ChromaBlend) => EdgeTuningKind.ChromaKey,
            _ => EdgeTuningKind.None,
        };
        if (kind == EdgeTuningKind.None)
            return;

        _boundEdgeHeld = true;
        _viewModel.BeginEdgeTuning(layer, kind);
    }

    // A drag holding the boxes and the Crop boxes' clip (HoldForDrag), and whether the playhead brought other clips during it.
    private bool _dragHeld, _redrawAfterDrag;

    private void HoldForDrag(object sender, bool started)
    {
        if (sender is not Thumb thumb || ReferenceEquals(thumb.TemplatedParent, TimelineSlider) || thumb.TemplatedParent is ScrollBar || started == _dragHeld)
            return;

        _dragHeld = started;
        _viewModel.HoldCropTarget(started);
        if (!started && _redrawAfterDrag)
        {
            // After the drag's own handlers have finished with its box.
            _redrawAfterDrag = false;
            Dispatcher.BeginInvoke(RedrawForClipsShown, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>The player's and the Canvas's boxes rebuilt when the clips on screen are not those they show.</summary>
    private void RedrawForClipsShown()
    {
        if (PlayerLayoutCanvas.Visibility == Visibility.Visible && !SamePlayerClips())
            RedrawPlayerLayout();
        if (_viewModel.IsArrangeActive && !SameArrangeClips())
            RedrawLayout();
    }

    private void PlayerLayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawPlayerLayout();

    /// <summary>Whether layers are edited on the player now: the frame engine (or the captions) on, paused, nothing else drawn on the video.</summary>
    private bool PlayerLayoutActive =>
        _viewModel.EditLayersOnPlayer && _viewModel.CanEditLayout && _viewModel.HasSource
        && !_viewModel.IsPlaying && CurrentOverlayMode == OverlayMode.None && _viewModel.FrameWidth > 0 && _viewModel.FrameHeight > 0;

    /// <summary>
    /// Each track's clip on screen at the playhead (shown, a picture): the boxes the player has (4.3D); and last, over them all as
    /// the captions are drawn, the caption box while captions are on (5.0-Verify).
    /// </summary>
    private List<Layer> PlayerClips()
    {
        var at = _viewModel.PositionMs / 1000;
        var total = _viewModel.SequenceSeconds;
        List<Layer> clips =
        [
            .. _viewModel.Layers.Where(l => l is { IsAudio: false, IsEmptyTrack: false, IsHidden: false, IsCaptions: false } && l.IsPicture)
                .Where(l => l.GetSpan(total) is var (start, end) && at >= start - 0.0005 && at < end && _viewModel.IsPictureDrawn(l, at))
                .GroupBy(l => l.TrackId).Select(t => t.First()),
        ];
        if (_viewModel.AutoCaptions)
            clips.Add(_viewModel.CaptionLayer);
        return clips;
    }

    /// <summary>What the player outlines: the captions while they are what is selected (they are never the key layer), else the key layer.</summary>
    private Layer PlayerSelected => _selectedClip is Layer { IsCaptions: true } captions ? captions : _viewModel.ActiveKeyLayer;

    private bool SamePlayerClips()
    {
        var clips = PlayerClips();
        return clips.Count == _playerBoxes.Count && clips.Zip(_playerBoxes).All(p => ReferenceEquals(p.First, p.Second.Clip));
    }

    private void RedrawPlayerLayout()
    {
        ClearPlayerLayout();
        var active = PlayerLayoutActive;
        PlayerLayoutCanvas.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active || PlayerLayoutCanvas.ActualWidth <= 0 || PlayerLayoutCanvas.ActualHeight <= 0)
            return;

        // The picture as the GPU engine fits it into the player: the output frame, letterboxed.
        var (frameWidth, frameHeight) = ((double)_viewModel.FrameWidth, (double)_viewModel.FrameHeight);
        _playerScale = Math.Min(PlayerLayoutCanvas.ActualWidth / frameWidth, PlayerLayoutCanvas.ActualHeight / frameHeight);
        _playerFrame = new Rect((PlayerLayoutCanvas.ActualWidth - frameWidth * _playerScale) / 2, (PlayerLayoutCanvas.ActualHeight - frameHeight * _playerScale) / 2,
            frameWidth * _playerScale, frameHeight * _playerScale);

        foreach (var clip in PlayerClips())
            AddPlayerBox(clip);
        UpdatePlayerSelection();
    }

    private void AddPlayerBox(Layer clip)
    {
        var box = new LayerBox
        {
            Layer = clip,
            Style = (Style)FindResource("PlayerLayerThumb"),
            Cursor = Cursors.SizeAll,
            ToolTip = $"{clip.Name}: click to select it; drag to move it (Alt: no snapping). Double-click to center it. Selected, a click with no drag picks the clip under it.",
            Takes = (self, at) => LayerBox.TakesPoint(self, HeldPlayerBox, at, IsDrawnNow),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, $"{clip.Name} on the video");

        // A press selects the layer; the same press drags it.
        var wasHeld = false;
        box.PreviewMouseLeftButtonDown += (_, _) =>
        {
            _clickThrough?.Stop();
            wasHeld = ReferenceEquals(HeldPlayerBox, box);
            _playerOutlineHidden = false;
            if (!ReferenceEquals(PlayerSelected, clip) || HeldLayer is null)
                SelectClip(clip);
            else
                UpdatePlayerSelection();
        };
        // One undo step for the drag, taken as the box first moves: a press that only selects the layer takes none (DragStep).
        var moveStep = new DragStep(_viewModel, $"move {clip.Name}");
        box.DragStarted += (_, _) => moveStep.Pressed();
        box.DragDelta += (_, e) =>
        {
            moveStep.Changing();
            MoveTrackLayer(clip, e.HorizontalChange / _playerScale, e.VerticalChange / _playerScale, _playerFrame, null);
        };
        box.DragCompleted += (_, e) =>
        {
            // A click with no drag on the selected layer, on a place other layers show too: the next one down is what was meant.
            if (wasHeld && !e.Canceled && Math.Abs(e.HorizontalChange) + Math.Abs(e.VerticalChange) < 2)
                ClickThrough([.. _playerBoxes.Select(b => b.Box)], box, PlayerLayoutCanvas, next => SelectClip(next.Layer));
        };
        box.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            box.CancelDrag();
            _viewModel.Checkpoint($"center {clip.Name}");
            CenterTrackLayer(clip);
            e.Handled = true;
        };

        void Place()
        {
            var (x, y, width, height) = TrackLayerRect(clip);
            box.Width = Math.Max(width * _playerScale, 6);
            box.Height = Math.Max(height * _playerScale, 6);
            Canvas.SetLeft(box, _playerFrame.X + x * _playerScale);
            Canvas.SetTop(box, _playerFrame.Y + y * _playerScale);
            var angle = clip.HasFilters ? clip.Rotation : 0;
            (box.RenderTransformOrigin, box.RenderTransform) = (new Point(clip.AnchorX, clip.AnchorY), Math.Abs(angle % 360) < 0.05 ? Transform.Identity : new RotateTransform(angle));
            OutlinePlayerBox(box, clip, !_playerOutlineHidden && ReferenceEquals(clip, PlayerSelected));
            if (ReferenceEquals(_playerGizmo?.AdornedElement, box))
                _playerGizmo!.InvalidateArrange();
        }

        PlayerLayoutCanvas.Children.Add(box);
        _playerBoxes.Add((box, clip, Place));
        Place();

        PropertyChangedEventHandler handler = (_, _) => Place();
        clip.PropertyChanged += handler;
        _playerSubscriptions.Add((clip, handler));
    }

    /// <summary>
    /// The layer a press inside its box stays with, on the player and on the Canvas: what the user has selected and can see of it. With
    /// nothing selected (the panes then show a clip by default), no box holds on to anything.
    /// </summary>
    private Layer? HeldLayer => !_playerOutlineHidden && (_selectedClip is Layer || _playheadVideoSelected) ? PlayerSelected : null;

    private LayerBox? HeldPlayerBox
    {
        get
        {
            if (HeldLayer is not { } held)
                return null;

            foreach (var (box, clip, _) in _playerBoxes)
            {
                if (ReferenceEquals(clip, held))
                    return box;
            }

            return null;
        }
    }

    // Whether a layer's picture is drawn at the playhead: what is not cannot be clicked or dragged.
    private bool IsDrawnNow(Layer layer) => _viewModel.IsPictureDrawn(layer, _viewModel.PositionMs / 1000);

    private System.Windows.Threading.DispatcherTimer? _clickThrough;

    // How long a click waits before it passes to the layer under it: the second press of a double-click (center it) is the box's own.
    private static readonly TimeSpan ClickThroughWait = TimeSpan.FromMilliseconds(300);

    /// <summary>After a click with no drag on the selected box: the next box down there is chosen, unless that click turns out to be half a double-click.</summary>
    /// <param name="boxes">The surface's boxes, bottom first.</param>
    private void ClickThrough(IReadOnlyList<LayerBox> boxes, LayerBox from, FrameworkElement surface, Action<LayerBox> choose)
    {
        if (LayerBox.NextDown(boxes, from, surface, Mouse.GetPosition(surface), IsDrawnNow) is not { } box)
            return;

        _clickThrough?.Stop();
        _clickThrough = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = ClickThroughWait };
        var timer = _clickThrough;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (box.IsVisible)
                choose(box);
        };
        timer.Start();
    }

    /// <summary>Outlines the selected layer's box and gives it the handles; every other box is left see-through.</summary>
    private void UpdatePlayerSelection()
    {
        if (_playerGizmo is not null)
        {
            AdornerLayer.GetAdornerLayer(_playerGizmo.AdornedElement)?.Remove(_playerGizmo);
            _playerGizmo = null;
        }

        var selected = PlayerSelected;
        var shown = false;
        foreach (var (box, clip, _) in _playerBoxes)
        {
            var on = !_playerOutlineHidden && ReferenceEquals(clip, selected);
            OutlinePlayerBox(box, clip, on);
            if (!on || AdornerLayer.GetAdornerLayer(box) is not { } layer)
                continue;

            var name = clip.Name;
            if (clip.IsCaptions)
            {
                // The caption box is not turned and has no anchor: only its corner grip, as on the Canvas.
                var grip = new ResizeAdorner(box, (Style)FindResource("LayerGrip"), name);
                var resizeStep = new DragStep(_viewModel, $"resize {name}");
                grip.ResizeStarted += (_, _) => resizeStep.Pressed();
                grip.ResizeDelta += (_, e) =>
                {
                    resizeStep.Changing();
                    ResizeTrackLayer(clip, e.HorizontalChange / _playerScale, e.VerticalChange / _playerScale);
                };
                _playerGizmo = grip;
            }
            else
            {
                var gizmo = new LayerGizmoAdorner(box, (Style)FindResource("LayerGrip"), name, () => new Point(clip.AnchorX, clip.AnchorY));
                WireGizmo(gizmo, box, clip, name, PlayerLayoutCanvas, () => TrackLayerRect(clip), _playerScale, () => _playerFrame);
                _playerGizmo = gizmo;
            }

            layer.Add(_playerGizmo);
            shown = true;
        }

        // With an outline shown, the canvas takes the clicks on the rest of the video (to hide it); otherwise they pass
        // through to the video (a see-through pixel of the window over it takes no click).
        PlayerLayoutCanvas.Background = shown ? PlayerHitBrush : null;
    }

    // The tilted clips' outlines over the player (5.2B, the user's Q4), by their boxes: made the first time a box's clip is tilted.
    private readonly Dictionary<LayerBox, System.Windows.Shapes.Polygon> _playerTiltOutlines = [];

    /// <summary>
    /// Outlines a clip's box over the player while it is the selected one (<paramref name="on"/>): the box's own border for a flat clip; for
    /// a tilted one (5.2B) the four corners the GPU lays it at, in perspective, and no flat border (its handles on those corners, 5.2C).
    /// </summary>
    private void OutlinePlayerBox(LayerBox box, Layer clip, bool on)
    {
        var corners = TiltedCorners(clip, _playerFrame, _playerScale);
        box.BorderThickness = new Thickness(on && corners is null ? 1.5 : 0);
        _playerTiltOutlines.TryGetValue(box, out var outline);
        if (PlaceTiltOutline(PlayerLayoutCanvas, box, outline, corners, on, null, box.BorderBrush ?? Brushes.White, 1.5, null) is { } placed)
            _playerTiltOutlines[box] = placed;
    }

    // A click on the video away from every layer's box: the outline goes until a layer is selected again.
    private void PlayerLayoutCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PlayerLayoutCanvas))
            return;

        _playerOutlineHidden = true;
        UpdatePlayerSelection();
        e.Handled = true;
    }

    private void ClearPlayerLayout()
    {
        if (_playerGizmo is not null)
        {
            AdornerLayer.GetAdornerLayer(_playerGizmo.AdornedElement)?.Remove(_playerGizmo);
            _playerGizmo = null;
        }

        foreach (var (layer, handler) in _playerSubscriptions)
            layer.PropertyChanged -= handler;
        _playerSubscriptions.Clear();
        foreach (var (box, _, _) in _playerBoxes)
            PlayerLayoutCanvas.Children.Remove(box);
        _playerBoxes.Clear();
        foreach (var outline in _playerTiltOutlines.Values)
            PlayerLayoutCanvas.Children.Remove(outline);
        _playerTiltOutlines.Clear();
        PlayerLayoutCanvas.Background = null;
    }
}
