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
/// Edit Layers on Video Preview (4.1.1-B): the layers arranged on the player itself, as in the layout pane. Over the
/// picture, in the window the video surface keeps above the video, each track's layer on screen at the playhead has a
/// box where it is drawn: nearly see-through, so a click on a layer selects it (and a drag moves it straight away).
/// Only the selected layer's box is outlined and has the handles (corner grip, rotation handle, anchor dot), and only
/// while paused: the video is never cluttered with the others. A click on the video away from it hides the outline
/// until another layer is selected. Moving, resizing, turning and the anchor go through the same code as the layout
/// pane's (MoveTrackLayer, ResizeTrackLayer, WireGizmo). Shown in Editor Mode with Live Preview and the frame engine on.
/// </summary>
public partial class MainWindow
{
    private readonly List<(Thumb Box, Layer Clip, Action Place)> _playerBoxes = [];
    private readonly List<(Layer Layer, PropertyChangedEventHandler Handler)> _playerSubscriptions = [];
    private LayerGizmoAdorner? _playerGizmo;
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
                    break;
                case nameof(MainViewModel.PositionMs):
                    // Another clip may be on screen now: rebuilt only when the set of clips shown is not the same.
                    if (PlayerLayoutCanvas.Visibility == Visibility.Visible && !SamePlayerClips())
                        RedrawPlayerLayout();
                    break;
                case nameof(MainViewModel.EditLayersOnPlayer) or nameof(MainViewModel.IsEditorMode) or nameof(MainViewModel.LivePreview)
                    or nameof(MainViewModel.IsPlaying) or nameof(MainViewModel.FrameEngine) or nameof(MainViewModel.HasSource)
                    or nameof(MainViewModel.FrameWidth) or nameof(MainViewModel.FrameHeight)
                    or nameof(MainViewModel.IsInteractiveCropActive) or nameof(MainViewModel.DrawTargetLayer):
                    RedrawPlayerLayout();
                    break;
            }
        };
        _viewModel.Layers.CollectionChanged += (_, _) => RedrawPlayerLayout();
    }

    private void PlayerLayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawPlayerLayout();

    /// <summary>Whether layers are edited on the player now: Editor Mode, Live Preview and the frame engine on, paused, nothing else drawn on the video.</summary>
    private bool PlayerLayoutActive =>
        _viewModel.EditLayersOnPlayer && _viewModel.IsEditorMode && _viewModel.LivePreview && _viewModel.FrameEngine && _viewModel.HasSource
        && !_viewModel.IsPlaying && CurrentOverlayMode == OverlayMode.None && _viewModel.FrameWidth > 0 && _viewModel.FrameHeight > 0;

    /// <summary>Each track's clip on screen at the playhead (shown, a picture): the boxes the player has.</summary>
    private List<Layer> PlayerClips()
    {
        var at = _viewModel.PositionMs / 1000;
        var total = _viewModel.SequenceSeconds;
        return
        [
            .. _viewModel.Layers.Where(l => l is { IsAudio: false, IsEmptyTrack: false, IsHidden: false, IsCaptions: false } && l.IsPicture)
                .Where(l => l.GetSpan(total) is var (start, end) && at >= start - 0.0005 && at < end)
                .GroupBy(l => l.TrackId).Select(t => t.First()),
        ];
    }

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

        // The picture as mpv fits it into the player: the output frame, letterboxed.
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
        var box = new Thumb
        {
            Style = (Style)FindResource("PlayerLayerThumb"),
            Cursor = Cursors.SizeAll,
            ToolTip = $"{clip.Name}: click to select it; drag to move it (Alt: no snapping). Double-click to center it.",
        };
        System.Windows.Automation.AutomationProperties.SetName(box, $"{clip.Name} on the video");

        // A press selects the layer; the same press drags it.
        box.PreviewMouseLeftButtonDown += (_, _) =>
        {
            _playerOutlineHidden = false;
            if (!ReferenceEquals(_viewModel.ActiveKeyLayer, clip))
                SelectClip(clip);
            else
                UpdatePlayerSelection();
        };
        box.DragStarted += (_, _) => _viewModel.Checkpoint($"move {clip.Name}");
        box.DragDelta += (_, e) => MoveTrackLayer(clip, e.HorizontalChange / _playerScale, e.VerticalChange / _playerScale, _playerFrame, null);
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

    /// <summary>Outlines the selected layer's box and gives it the handles; every other box is left see-through.</summary>
    private void UpdatePlayerSelection()
    {
        if (_playerGizmo is not null)
        {
            AdornerLayer.GetAdornerLayer(_playerGizmo.AdornedElement)?.Remove(_playerGizmo);
            _playerGizmo = null;
        }

        var selected = _viewModel.ActiveKeyLayer;
        var shown = false;
        foreach (var (box, clip, _) in _playerBoxes)
        {
            var on = !_playerOutlineHidden && ReferenceEquals(clip, selected);
            box.BorderThickness = new Thickness(on ? 1.5 : 0);
            if (!on || AdornerLayer.GetAdornerLayer(box) is not { } layer)
                continue;

            var name = clip.Name;
            _playerGizmo = new LayerGizmoAdorner(box, (Style)FindResource("LayerGrip"), name, () => new Point(clip.AnchorX, clip.AnchorY));
            WireGizmo(_playerGizmo, box, clip, name, PlayerLayoutCanvas, () => TrackLayerRect(clip), (deltaX, deltaY) => ResizeTrackLayer(clip, deltaX, deltaY), _playerScale);
            layer.Add(_playerGizmo);
            shown = true;
        }

        // With an outline shown, the canvas takes the clicks on the rest of the video (to hide it); otherwise they pass
        // through to the video (a see-through pixel of the window over it takes no click).
        PlayerLayoutCanvas.Background = shown ? PlayerHitBrush : null;
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
        PlayerLayoutCanvas.Background = null;
    }
}
