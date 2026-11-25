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
/// Edit Clips on Video Preview: on-player layout editing, mirroring the Canvas pane. In the video surface's overlay window, each
/// track's visible clip at the playhead gets a near-transparent hit box (click selects, drag moves immediately). Only the selected
/// clip's box shows an outline and handles (corner grip, rotation, anchor), and only while paused; clicking empty picture hides the
/// outline until the next selection. Move, resize, rotate and anchor reuse the Canvas code (MoveTrackLayer, ResizeTrackLayer,
/// WireGizmo). Hidden while a rectangle is drawn. With captions on, the caption box is topmost, moved/sized like the Canvas's (no
/// rotation or anchor), and selected by a press on it or on any timeline caption.
/// </summary>
public partial class MainWindow
{
    private readonly List<(LayerBox Box, Layer Clip, Action Place)> _playerBoxes = [];
    private readonly List<(Layer Layer, PropertyChangedEventHandler Handler)> _playerSubscriptions = [];
    private Adorner? _playerGizmo;
    private Rect _playerFrame;

    // Snap guides, equal-spacing callouts and 1:1 badges of a drag on the player (Pass 5.6.5); null while no boxes are up.
    private LayoutGuidesView? _playerGuides;
    private double _playerScale;

    // Set by a click on empty picture: hides the selection outline until the next selection.
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
                    UpdateLayerXRay();

                    // A new selection may change the Canvas's box set.
                    if (_viewModel.IsArrangeActive && !SameArrangeClips())
                        RedrawLayout();
                    break;
                case nameof(MainViewModel.PositionMs):
                    // Rebuild player and Canvas boxes only when the visible clip set changed; during a drag, defer until it
                    // completes; during a held playhead scrub, skip the per-frame commits and rebuild once on release
                    // (MainWindow.Scrub.cs ReleaseScrubGate), so cut boundaries never rebuild LayerBoxes and hit masks at 60 Hz.
                    if (ScrubDefersLayout)
                        _scrubState.RedrawOwed = true;
                    else if (_dragHeld)
                        _redrawAfterDrag = true;
                    else
                        RedrawForClipsShown();
                    if (!ScrubDefersLayout && !_viewModel.IsPlaying)
                        UpdateLayerXRay();
                    break;
                case nameof(MainViewModel.EditLayersOnPlayer)
                    or nameof(MainViewModel.IsPlaying) or nameof(MainViewModel.AutoCaptions) or nameof(MainViewModel.HasSource)
                    or nameof(MainViewModel.FrameWidth) or nameof(MainViewModel.FrameHeight)
                    or nameof(MainViewModel.IsInteractiveCropActive) or nameof(MainViewModel.DrawTargetLayer):
                    RedrawPlayerLayout();
                    UpdateLayerXRay();
                    break;
            }
        };
        _viewModel.Layers.CollectionChanged += (_, _) => RedrawPlayerLayout();
        _viewModel.LayerVisibilityChanged += UpdateLayerXRay;

        // Cutout tight bounds and the automatic anchor follow every mask edit (MainViewModel.Cutouts.cs; subscriptions only).
        _viewModel.WatchTightBounds();

        // Any Thumb drag (box, gizmo or slider, any window) freezes box rebuilds and the Crop target until it completes, then both
        // catch up. Seek bar and scroll bar thumbs are exempt (DragHold.Exempt).
        EventManager.RegisterClassHandler(typeof(Thumb), Thumb.DragStartedEvent, new DragStartedEventHandler((sender, _) => HoldForDrag(sender, true)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Thumb), Thumb.DragCompletedEvent, new DragCompletedEventHandler((sender, _) => HoldForDrag(sender, false)), handledEventsToo: true);

        // Sliders/number boxes declaring a clip edge (NumericInput.EdgeKind: Layer Properties, mask tools) show the edge loupe on the
        // player while held. Effects pane cards report their own edges (their values bind to the cards).
        NumericInput.EdgeHeld += HoldBoundEdge;

        // Ping-to-Mark: Alt+click on the picture (no drag; Alt+drag still moves a clip without snapping) adds a highlight at the playhead
        // with the clicked focal point.
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

        // Stationary right-click on the picture opens the player context menu (MainWindow.PlayerMenu.cs).
        HookPlayerContextMenu();
    }

    // Overlay position of a pending Alt press (Ping-to-Mark); null otherwise.
    private Point? _altPressAt;

    /// <summary>Maps an overlay point to normalized output-frame coordinates (0..1); null outside the picture.</summary>
    private Point? PictureFocalAt(Point at)
    {
        var (width, height) = (PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight);
        var (frameWidth, frameHeight) = ((double)_viewModel.FrameWidth, (double)_viewModel.FrameHeight);
        if (width <= 0 || height <= 0 || frameWidth <= 0 || frameHeight <= 0)
            return null;

        // Letterboxed output frame as the GPU engine fits it (same fit RedrawPlayerLayout uses for boxes).
        var scale = Math.Min(width / frameWidth, height / frameHeight);
        var (left, top) = ((width - frameWidth * scale) / 2, (height - frameHeight * scale) / 2);
        var (x, y) = ((at.X - left) / (frameWidth * scale), (at.Y - top) / (frameHeight * scale));
        return x is >= 0 and <= 1 && y is >= 0 and <= 1 ? new Point(x, y) : null;
    }

    // True when edge tuning was started by a bound slider/box (HoldBoundEdge); only that control's release ends it.
    private bool _boundEdgeHeld;

    private void HoldBoundEdge(FrameworkElement control, EdgeTuningKind kind, bool held)
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

        if (kind == EdgeTuningKind.None || control.DataContext is not Layer layer)
            return;

        _boundEdgeHeld = true;
        _viewModel.BeginEdgeTuning(layer, kind);
    }

    // Drag freeze state (HoldForDrag) and whether the visible clip set may have changed during it.
    private bool _dragHeld, _redrawAfterDrag;

    private void HoldForDrag(object sender, bool started)
    {
        if (sender is not Thumb thumb || DragHold.GetExempt(thumb) || started == _dragHeld)
            return;

        _dragHeld = started;
        _viewModel.HoldCropTarget(started);
        if (!started && _redrawAfterDrag)
        {
            // Deferred so the drag's own completion handlers finish with its box first.
            _redrawAfterDrag = false;
            Dispatcher.BeginInvoke(RedrawForClipsShown, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Rebuilds player and Canvas boxes when the visible clip set differs from what they show.</summary>
    private void RedrawForClipsShown()
    {
        if (PlayerLayoutCanvas.Visibility == Visibility.Visible && !SamePlayerClips())
            RedrawPlayerLayout();
        if (_viewModel.IsArrangeActive && !SameArrangeClips())
            RedrawLayout();
    }

    private void PlayerLayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawPlayerLayout();

    /// <summary>True when on-player layer editing is active: enabled, layout editable, source loaded, paused, no other overlay mode.</summary>
    private bool PlayerLayoutActive =>
        _viewModel.EditLayersOnPlayer && _viewModel.CanEditLayout && _viewModel.HasSource
        && !_viewModel.IsPlaying && CurrentOverlayMode == OverlayMode.None && _viewModel.FrameWidth > 0 && _viewModel.FrameHeight > 0;

    /// <summary>
    /// Clips that get player boxes: each track's first visible, drawn picture clip at the playhead, then (topmost, matching caption
    /// draw order) the caption layer while captions are on.
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

    /// <summary>Layer the player outlines: the caption layer when selected (never the key layer), otherwise the active key layer.</summary>
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

        // Letterboxed output frame as the GPU engine fits it into the player.
        var (frameWidth, frameHeight) = ((double)_viewModel.FrameWidth, (double)_viewModel.FrameHeight);
        _playerScale = Math.Min(PlayerLayoutCanvas.ActualWidth / frameWidth, PlayerLayoutCanvas.ActualHeight / frameHeight);
        _playerFrame = new Rect((PlayerLayoutCanvas.ActualWidth - frameWidth * _playerScale) / 2, (PlayerLayoutCanvas.ActualHeight - frameHeight * _playerScale) / 2,
            frameWidth * _playerScale, frameHeight * _playerScale);

        foreach (var clip in PlayerClips())
            AddPlayerBox(clip);

        // Topmost: snap guides of a drag (only motion-path dots would be hit-testable; the player shows none).
        _playerGuides = new LayoutGuidesView { Width = PlayerLayoutCanvas.ActualWidth, Height = PlayerLayoutCanvas.ActualHeight };
        System.Windows.Automation.AutomationProperties.SetName(_playerGuides, "Player snap guides");
        PlayerLayoutCanvas.Children.Add(_playerGuides);
        UpdatePlayerSelection();
    }

    private void AddPlayerBox(Layer clip)
    {
        var box = new LayerBox
        {
            Layer = clip,
            Style = (Style)FindResource("PlayerLayerThumb"),
            Cursor = Cursors.SizeAll,
            ToolTip = clip.IsText ? $"{clip.Name}: drag to move (Alt: no snap); double-click edits text" : $"{clip.Name}: drag to move (Alt: no snap); double-click centers",
            Takes = (self, at) => LayerBox.TakesPoint(self, HeldPlayerBox, at, IsDrawnNow),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, $"{clip.Name} on the video");

        // Press selects the layer; the same press can start a drag.
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
        // One undo step per drag, taken on first movement; a select-only press records none (DragStep).
        var moveStep = new DragStep(_viewModel, $"move {clip.Name}");
        box.DragStarted += (_, _) =>
        {
            moveStep.Pressed();
            _snapGate.Reset();
        };
        box.DragDelta += (_, e) =>
        {
            moveStep.Changing();
            MoveTrackLayer(clip, e.HorizontalChange / _playerScale, e.VerticalChange / _playerScale, _playerFrame, _playerGuides);
        };
        box.DragCompleted += (_, e) =>
        {
            _playerGuides?.ShowGuides([]);

            // Drag-free click on the already-selected layer where others overlap: cycle to the next layer down.
            if (wasHeld && !e.Canceled && Math.Abs(e.HorizontalChange) + Math.Abs(e.VerticalChange) < 2)
                ClickThrough([.. _playerBoxes.Select(b => b.Box)], box, PlayerLayoutCanvas, next => SelectClip(next.Layer));
        };
        box.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            box.CancelDrag();
            e.Handled = true;

            // A text clip edits its words in place; any other clip centres.
            if (clip.IsText)
            {
                BeginTextEdit(box, clip);
                return;
            }

            _viewModel.Checkpoint($"center {clip.Name}");
            CenterTrackLayer(clip);
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
    /// Layer that keeps presses inside its box (player and Canvas): the explicit, visible selection. With nothing explicitly selected (panes
    /// then show a default clip), no box holds presses.
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

    // True when the layer is drawn at the playhead; undrawn layers are not hit-testable.
    private bool IsDrawnNow(Layer layer) => _viewModel.IsPictureDrawn(layer, _viewModel.PositionMs / 1000);

    private System.Windows.Threading.DispatcherTimer? _clickThrough;

    // Click-through delay, so a double-click's second press (center) stays with the original box.
    private static readonly TimeSpan ClickThroughWait = TimeSpan.FromMilliseconds(300);

    /// <summary>After a drag-free click on the selected box, selects the next box down at that point unless a double-click follows.</summary>
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

    /// <summary>Outlines the selected layer's box and attaches its handles; all other boxes stay transparent.</summary>
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
                // Caption box: corner grip only (no rotation or anchor), as on the Canvas.
                var grip = new ResizeAdorner(box, (Style)FindResource("LayerGrip"), name);
                var resizeStep = new DragStep(_viewModel, $"resize {name}");
                grip.ResizeStarted += (_, _) => resizeStep.Pressed();
                grip.ResizeDelta += (_, e) =>
                {
                    resizeStep.Changing();
                    ResizeTrackLayer(clip, e.HorizontalChange / _playerScale, e.VerticalChange / _playerScale, surfaceScale: _playerScale);
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

        // With an outline shown, the canvas hit-tests empty picture (to hide it); otherwise a null background lets clicks pass
        // through to the video.
        PlayerLayoutCanvas.Background = shown ? PlayerHitBrush : null;
    }

    // Perspective outlines for tilted clips, keyed by box; created lazily on first tilt.
    private readonly Dictionary<LayerBox, System.Windows.Shapes.Polygon> _playerTiltOutlines = [];

    /// <summary>
    /// Outlines a clip's player box while selected (<paramref name="on"/>): the box border for flat clips; for tilted clips, a polygon
    /// through the four GPU-projected corners (handles sit on those corners) and no flat border.
    /// </summary>
    private void OutlinePlayerBox(LayerBox box, Layer clip, bool on)
    {
        // A Cutout with visible-pixel bounds is outlined by its gizmo on them, not by its whole box.
        var corners = TiltedCorners(clip, _playerFrame, _playerScale);
        box.BorderThickness = new Thickness(on && corners is null && (clip.IsCaptions || TightRectOf(clip) is null) ? 1.5 : 0);
        _playerTiltOutlines.TryGetValue(box, out var outline);
        if (PlaceTiltOutline(PlayerLayoutCanvas, box, outline, corners, on, null, box.BorderBrush ?? Brushes.White, 1.5, null) is { } placed)
            _playerTiltOutlines[box] = placed;
    }

    // Click on empty picture: hide the outline until the next selection.
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
        if (_playerGuides is not null)
            PlayerLayoutCanvas.Children.Remove(_playerGuides);
        _playerGuides = null;
        PlayerLayoutCanvas.Background = null;
    }
}
