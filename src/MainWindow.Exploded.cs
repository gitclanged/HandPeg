using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;
using ExplodedCamera = HandPegApp.Compositor.ExplodedCamera;
using Vector2 = System.Numerics.Vector2;

namespace HandPegApp;

// ----- The 3D Exploded View (Pass 5.0-AudioAnd3D, 5.0D) -----
// The player turned into the GPU engine's 3D layer stack: each layer a plane in compositing order, the camera orbiting the
// canvas (GpuPreviewEngine.Exploded.cs, drawn by the worker's SceneRenderer.Exploded.cs). Here: the toggle (the playback
// bar's, its keybind "view.exploded3d", Alt + middle-drag on the flat player), the input over the video, the Z-Spacing
// panel and the layers' label badges, all in the window the video surface keeps above the video (ExplodedLayer, last in
// PlayerOverlay, see-through and taking nothing while the view is flat).
// - Picking: the pointer's ray meets each plane where the worker drew it (ExplodedCamera, from the scene it last drew);
//   the layer selected wins wherever its box is under the pointer, even behind another (so the layer Ctrl + the wheel
//   picked is the one a drag moves), else the nearest plane whose box it lands in. A press selects it, as a press on its
//   box on the flat player does.
// - The left button only ever selects and moves (the user's Q4 / bug 5, after drags of the empty space orbited and
//   presses moved the wrong layer): dragging a plane moves its layer in its plane, through the same code as the player's
//   and the Canvas's boxes (MoveTrackLayer), one undo step for the drag (DragStep); the selected plane's corner handle
//   resizes it (ResizeTrackLayer, in the box's own axes).
// - Shift + dragging a plane lifts it through the stack (a preview the engine draws, GpuPreviewEngine.LiftLayer); let go
//   beyond another track's plane, its track moves past that track (MoveLayerUp / MoveLayerDown, the Layers' own reorder),
//   all of it one undo step (EditTogether), and the Timeline's rows show it at once (5.0-Release: a cut-out moved behind
//   its video is laid under its row when its stack is open, in front above it). Each track has its own place in the stack
//   (GpuScenePlan.PlaceLayers), so the planes keep theirs while layers come and go.
// - The right button (or the middle; Alt + middle from the flat player) orbits, from anywhere; the wheel moves the camera
//   in and out; Ctrl + the wheel selects the layer in front of or behind the selected one, in the stack's order (its plane
//   and badge in the accent); a double-click on the empty space puts the camera back; Esc, the panel's close button or the
//   toggle leaves.
public partial class MainWindow
{
    private enum ExplodedDragKind { None, Orbit, Move, Depth, Resize }

    // Degrees of turn per DIP dragged; the wheel's step (a notch in); DIPs per place in the stack when its depth cannot be
    // seen (looking straight at it); W A S D's turn, degrees per second.
    private const double ExplodedOrbitPerDip = 0.3, ExplodedWheelStep = 0.88, ExplodedDepthFallback = 36, ExplodedNavOrbitRate = 70;

    // Whether the 3D view is on (the user's choice); whether its layer and its frame ticks are up (until the way back has
    // settled); its input hooked once.
    private bool _explodedUi, _explodedTicking, _explodedHooked, _syncingExplodedToggle;

    // The scene last drawn in 3D (picking, the badges), what each of its layers is, and for which plan, moment and keys.
    private readonly ExplodedScene _explodedScene = new();
    private readonly GpuLayerInfo[] _explodedInfos = new GpuLayerInfo[CompositorProtocol.MaxLayers];
    private readonly long[] _explodedInfoKeys = new long[CompositorProtocol.MaxLayers];
    private int _explodedInfoCount;
    private GpuScenePlan? _explodedInfoPlan;

    // The label badges (made as needed, kept), the flat picture's overlays hidden under the 3D view (with their opacity),
    // and the player's own zoom matrix the 3D layer last cancelled.
    private readonly List<(Border Badge, TextBlock Text)> _explodedBadges = [];
    private readonly List<(UIElement Element, double Opacity)> _explodedHidden = [];
    private Matrix _explodedUnder = Matrix.Identity;

    // The drag under way: what it is, its button, where it was pressed and last was, and for a plane its layer, the camera
    // it was pressed under, where on the plane the pointer is, the plane's depth, and how far it is lifted.
    private ExplodedDragKind _explodedDrag;
    private MouseButton _explodedButton;
    private Point _explodedPress, _explodedLast;
    private ExplodedPlane _explodedPlane;
    private Layer? _explodedView;
    private DragStep? _explodedStep;
    private ExplodedCamera _explodedCamera;
    private Vector2 _explodedHit;
    private float _explodedPlaneZ;
    private double _explodedLift;

    private static readonly SolidColorBrush ExplodedBadgeFill = FrozenBrush(Color.FromArgb(0xD8, 0x0E, 0x10, 0x14)),
        ExplodedBadgeEdge = FrozenBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), ExplodedMaskEdge = FrozenBrush(Color.FromArgb(0xFF, 0x19, 0xE6, 0xFF)),
        ExplodedSelectedEdge = FrozenBrush(Color.FromArgb(0xFF, 0xFF, 0xB8, 0x2E)), ExplodedBadgeText = FrozenBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
        ExplodedMutedText = FrozenBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), ExplodedSelectedText = FrozenBrush(Color.FromArgb(0xFF, 0x1A, 0x12, 0x04));

    /// <summary>Whether the 3D view can be turned on or off now (the keybind's test): on the GPU engine, with something open, or to leave it.</summary>
    private bool CanToggleExplodedView => _explodedUi || _gpu is not null && _viewModel.HasSource;

    /// <summary>The keybind "view.exploded3d" and the toggle: the 3D view on, or back to the flat picture.</summary>
    private void ToggleExplodedView() => SetExplodedView(!_explodedUi);

    // ----- On and off -----

    /// <summary>
    /// Into the 3D view (the camera swings out of the flat picture) or out of it (back into the flat picture, the overlays of
    /// the flat player coming back once it is there). Only on the GPU engine, with a video open.
    /// </summary>
    private void SetExplodedView(bool on)
    {
        if (on == _explodedUi)
        {
            SyncExplodedToggle();
            return;
        }

        if (on)
        {
            if (_gpu is not { } engine)
            {
                _viewModel.StatusText = "The 3D Exploded View starts once the GPU preview engine is running: open a video first.";
                SyncExplodedToggle();
                return;
            }

            if (!_viewModel.HasSource || !VideoView.IsVisible)
            {
                _viewModel.StatusText = "Open a video or a project first: the 3D Exploded View shows its layers.";
                SyncExplodedToggle();
                return;
            }

            HookExplodedInput();

            // The flat player's own zoom has no place in 3D: the view starts from the picture fitted, as the camera does.
            (_playerZoom, _playerPanX, _playerPanY) = (0, 0, 0);
            if (_playerView is { } view)
                view.Matrix = Matrix.Identity;
            engine.SetView(0, 0, 0);

            // The camera starts where it always does (the user's Q4: its angle is not remembered); the spacing is the panel's,
            // kept for the session.
            _explodedUi = true;
            engine.ResetCamera();
            engine.ZSpacing = ExplodedSpacing.Value;
            engine.SelectedClipKey = SelectedExplodedKey();
            engine.SetExploded(true);
            ShowExplodedLayer();
            _viewModel.StatusText = "3D Exploded View: right-drag to orbit, the wheel to zoom, Ctrl + the wheel to pick the layer in front or behind; drag a layer to move it in its plane (its corner handle resizes it), Shift + drag to move it through the stack. Esc goes back.";
        }
        else
        {
            EndExplodedDrag(cancelled: true);
            _explodedUi = false;
            _gpu?.SetExploded(false);

            // The panel and the badges go now; the layer keeps taking presses until the picture is flat again (OnExplodedFrame),
            // so nothing hidden under it is clicked during the way back.
            ExplodedPanel.Visibility = Visibility.Collapsed;
            ExplodedBadges.Visibility = Visibility.Collapsed;
            if (_gpu is null)
                HideExplodedLayer();
        }

        SyncExplodedToggle();
    }

    private void ShowExplodedLayer()
    {
        // The flat player's boxes, crop and margins are drawn where the flat picture has things: out of sight in 3D.
        if (_explodedHidden.Count == 0)
        {
            foreach (UIElement child in PlayerOverlay.Children)
            {
                if (ReferenceEquals(child, ExplodedLayer))
                    continue;
                _explodedHidden.Add((child, child.Opacity));
                child.Opacity = 0;
            }
        }

        ExplodedLayer.Background = PlayerHitBrush;
        ExplodedLayer.IsHitTestVisible = true;
        ExplodedPanel.Visibility = Visibility.Visible;
        ExplodedBadges.Visibility = Visibility.Visible;
        if (!_explodedTicking)
        {
            _explodedTicking = true;
            CompositionTarget.Rendering += OnExplodedFrame;
        }
    }

    private void HideExplodedLayer()
    {
        if (_explodedTicking)
        {
            _explodedTicking = false;
            CompositionTarget.Rendering -= OnExplodedFrame;
        }

        ExplodedLayer.Background = null;
        ExplodedLayer.IsHitTestVisible = false;
        ExplodedPanel.Visibility = Visibility.Collapsed;
        ExplodedBadges.Visibility = Visibility.Collapsed;
        foreach (var (element, opacity) in _explodedHidden)
            element.Opacity = opacity;
        _explodedHidden.Clear();
        _explodedScene.Reset();
        _explodedInfoPlan = null;
    }

    private void SyncExplodedToggle()
    {
        if (ExplodedToggle.IsChecked == _explodedUi)
            return;
        _syncingExplodedToggle = true;
        try
        {
            ExplodedToggle.IsChecked = _explodedUi;
        }
        finally
        {
            _syncingExplodedToggle = false;
        }
    }

    private void ExplodedToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingExplodedToggle)
            SetExplodedView(ExplodedToggle.IsChecked == true);
    }

    private void ExplodedSpacing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_gpu is { } engine)
            engine.ZSpacing = e.NewValue;
    }

    private void ExplodedReset_Click(object sender, RoutedEventArgs e) => _gpu?.ResetCamera();

    private void ExplodedClose_Click(object sender, RoutedEventArgs e) => SetExplodedView(false);

    /// <summary>The clip selected (the timeline's selection) as the engine's key; 0 none.</summary>
    private long SelectedExplodedKey() => _selectedClip is Layer { ClipId: var id } && id != Guid.Empty ? GpuHash.Of(id) : 0;

    // ----- Every frame while the 3D view is up -----

    private void OnExplodedFrame(object? sender, EventArgs e)
    {
        var engine = _gpu;

        // The engine gone (fallen back to libmpv) or the video closed: the view is over.
        if (_explodedUi && (engine is null || !_viewModel.HasSource))
        {
            SetExplodedView(false);
            engine = _gpu;
        }

        if (engine is null || !_explodedUi && !engine.IsDrawingExploded)
        {
            HideExplodedLayer();
            return;
        }

        var key = _explodedUi ? SelectedExplodedKey() : 0;
        if (engine.SelectedClipKey != key)
            engine.SelectedClipKey = key;
        KeepExplodedLayerUnzoomed();
        if (_explodedUi && engine.TryGetExplodedScene(_explodedScene))
            PlaceExplodedBadges();
    }

    /// <summary>
    /// The player's own zoom (W and S over the flat player) scales everything over the video; the 3D layer is kept out of
    /// it, so its coordinates are always the window's (the pointer, the badges).
    /// </summary>
    private void KeepExplodedLayerUnzoomed()
    {
        var under = PlayerOverlay.RenderTransform?.Value ?? Matrix.Identity;
        if (under == _explodedUnder)
            return;

        _explodedUnder = under;
        if (under.IsIdentity || !under.HasInverse)
        {
            ExplodedLayer.RenderTransform = Transform.Identity;
            return;
        }

        under.Invert();
        var inverse = new MatrixTransform(under);
        inverse.Freeze();
        ExplodedLayer.RenderTransform = inverse;
    }

    // ----- The layers' names and badges -----

    /// <summary>What each layer of the scene is (GpuScenePlan.LayerInfoAt), asked again only when the plan or its layers changed.</summary>
    private void TakeExplodedInfos(ExplodedScene scene)
    {
        var same = ReferenceEquals(scene.Plan, _explodedInfoPlan) && scene.Count == _explodedInfoCount;
        for (var k = 0; same && k < scene.Count; k++)
            same = _explodedInfoKeys[k] == scene.Planes[k].ClipKey;
        if (same)
            return;

        try
        {
            _explodedInfoCount = Math.Min(scene.Plan.LayerInfoAt(scene.Seconds, _explodedInfos), scene.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Names are a help, never a reason to fail: the planes go unnamed (and unpicked) until the next scene.
            AppLog.Write("3D Exploded View: the layers could not be described", ex);
            _explodedInfoCount = 0;
        }

        _explodedInfoPlan = scene.Plan;
        for (var k = 0; k < scene.Count; k++)
            _explodedInfoKeys[k] = scene.Planes[k].ClipKey;
        _explodedInfoCount = Math.Min(_explodedInfoCount, scene.Count);
    }

    /// <summary>What a plane is: its own entry (the same order as the scene), else the first entry of its clip; null for none.</summary>
    private GpuLayerInfo? ExplodedInfoOf(in ExplodedPlane plane)
    {
        if (plane.Index < _explodedInfoCount && (_explodedInfos[plane.Index].ClipKey == plane.ClipKey || plane.BlurFillOnly))
            return _explodedInfos[plane.Index];
        for (var k = 0; k < _explodedInfoCount; k++)
        {
            if (_explodedInfos[k].ClipKey == plane.ClipKey)
                return _explodedInfos[k];
        }

        return null;
    }

    /// <summary>A badge over each named plane, at its box's top left corner as drawn; masked layers in the outline's cyan, the selected in amber.</summary>
    private void PlaceExplodedBadges()
    {
        var scene = _explodedScene;
        TakeExplodedInfos(scene);
        var camera = scene.Camera;
        var (width, height) = (ExplodedLayer.ActualWidth, ExplodedLayer.ActualHeight);
        if (width <= 0 || height <= 0)
            return;

        var (toX, toY) = (width / Math.Max(scene.OutputWidth, 1), height / Math.Max(scene.OutputHeight, 1));
        var shown = 0;
        for (var k = 0; k < scene.Count; k++)
        {
            ref readonly var plane = ref scene.Planes[k];
            if (ExplodedInfoOf(plane) is not { Name.Length: > 0 } info)
                continue;

            var corner = ExplodedCamera.BoxPoint(plane.X, plane.Y, plane.Width, plane.Height, plane.Rotation, plane.AnchorX, plane.AnchorY, 0, 0);
            var at = camera.Project(corner.X, corner.Y, camera.DepthOf(plane.ZDepth));
            if (!float.IsFinite(at.X) || !float.IsFinite(at.Y))
                continue;

            var (badge, text) = ExplodedBadge(shown++);
            var selected = (plane.Flags & GpuLayerDescriptor.FlagSelected) != 0 && !plane.BlurFillOnly;
            text.Text = selected ? "▲ " + info.Name : info.Name;

            // The selected layer's badge is filled with the accent (its plane's box is too), so a drag's layer is never in doubt;
            // another clip of its track, playing while it is not, has the accent's edge (its plane's fainter outline).
            var selectedTrack = (plane.Flags & GpuLayerDescriptor.FlagSelectedTrack) != 0;
            badge.Background = selected ? ExplodedSelectedEdge : ExplodedBadgeFill;
            badge.BorderBrush = selected || selectedTrack ? ExplodedSelectedEdge : info.HasMask ? ExplodedMaskEdge : ExplodedBadgeEdge;
            text.Foreground = selected ? ExplodedSelectedText : plane.BlurFillOnly ? ExplodedMutedText : ExplodedBadgeText;
            text.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            Panel.SetZIndex(badge, selected ? 1 : 0);
            badge.Opacity = camera.Amount;
            Canvas.SetLeft(badge, Math.Clamp(at.X * toX, 2, Math.Max(width - 60, 2)));
            Canvas.SetTop(badge, Math.Clamp(at.Y * toY - 21, 2, Math.Max(height - 20, 2)));
            badge.Visibility = Visibility.Visible;
        }

        for (var k = shown; k < _explodedBadges.Count; k++)
            _explodedBadges[k].Badge.Visibility = Visibility.Collapsed;

        // The selected plane's corner handle (the resize grip), where its box's far corner is drawn.
        var handle = _explodedHandle ??= MakeExplodedHandle();
        if (_gpu is { } engine && SelectedExplodedHandle(engine, out _, out var grip) && grip.X >= 0 && grip.Y >= 0 && grip.X <= width && grip.Y <= height)
        {
            Canvas.SetLeft(handle, grip.X - handle.Width / 2);
            Canvas.SetTop(handle, grip.Y - handle.Height / 2);
            handle.Opacity = camera.Amount;
            handle.Visibility = Visibility.Visible;
        }
        else
        {
            handle.Visibility = Visibility.Collapsed;
        }
    }

    private Border? _explodedHandle;

    private Border MakeExplodedHandle()
    {
        var handle = new Border
        {
            Width = 10, Height = 10, Background = ExplodedSelectedEdge, BorderBrush = ExplodedSelectedText, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };
        Panel.SetZIndex(handle, 2);
        ExplodedBadges.Children.Add(handle);
        return handle;
    }

    private (Border Badge, TextBlock Text) ExplodedBadge(int index)
    {
        while (_explodedBadges.Count <= index)
        {
            var text = new TextBlock { FontSize = 11, Foreground = ExplodedBadgeText, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
            var badge = new Border
            {
                Child = text, Background = ExplodedBadgeFill, BorderBrush = ExplodedBadgeEdge, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 2), IsHitTestVisible = false,
            };
            ExplodedBadges.Children.Add(badge);
            _explodedBadges.Add((badge, text));
        }

        return _explodedBadges[index];
    }

    // ----- Input -----

    private void ExplodedLayer_Loaded(object sender, RoutedEventArgs e) => HookExplodedInput();

    /// <summary>The 3D view's input, hooked once (when the window over the video first shows, or the view first comes on).</summary>
    private void HookExplodedInput()
    {
        if (_explodedHooked)
            return;
        _explodedHooked = true;

        // Alt + middle on the flat player: on its native window where nothing is over the video, on the window over it where something is.
        VideoView.MessageHook += ExplodedSurfaceMessage;
        PlayerOverlay.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(ExplodedOverlay_PreviewMouseDown));
        ExplodedLayer.MouseDown += ExplodedLayer_MouseDown;
        ExplodedLayer.MouseMove += ExplodedLayer_MouseMove;
        ExplodedLayer.MouseUp += ExplodedLayer_MouseUp;
        ExplodedLayer.MouseWheel += ExplodedLayer_MouseWheel;
        ExplodedLayer.LostMouseCapture += (_, _) => EndExplodedDrag(cancelled: true);

        // After the window's own keys (an open dial or the tour take Esc first).
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(ExplodedWindow_PreviewKeyDown));
    }

    private const int WmMiddleButtonDown = 0x0207;

    // The video's native window: a press there is no WPF input, so Alt + middle is caught from its messages.
    private IntPtr ExplodedSurfaceMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMiddleButtonDown || _explodedUi || _gpu is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return IntPtr.Zero;

        // The point in the window's pixels, as DIPs of the window over it (which lies exactly on it).
        var dpi = VisualTreeHelper.GetDpi(VideoView);
        var (x, y) = ((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
        handled = true;
        RunGuarded("3D view", () => BeginExplodedFromPress(new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY), MouseButton.Middle));
        return IntPtr.Zero;
    }

    // Alt + middle on something over the flat video (a layer's box, the crop).
    private void ExplodedOverlay_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_explodedUi || e.ChangedButton != MouseButton.Middle || _gpu is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return;

        e.Handled = true;
        var at = e.GetPosition(ExplodedLayer);
        RunGuarded("3D view", () => BeginExplodedFromPress(at, MouseButton.Middle));
    }

    /// <summary>Alt + middle on the flat player: into 3D, the press going on as an orbit.</summary>
    private void BeginExplodedFromPress(Point at, MouseButton button)
    {
        SetExplodedView(true);
        if (_explodedUi)
            BeginExplodedDrag(ExplodedDragKind.Orbit, at, button);
    }

    private void ExplodedWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_explodedUi || Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem)
            return;

        // A drag under way is let go first (a plane lifted through the stack goes back); then the view.
        if (_explodedDrag != ExplodedDragKind.None)
            EndExplodedDrag(cancelled: true);
        else
            SetExplodedView(false);
        e.Handled = true;
    }

    private bool InExplodedPanel(object? source)
    {
        for (var at = source as DependencyObject; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, ExplodedPanel))
                return true;
            if (ReferenceEquals(at, ExplodedLayer))
                return false;
        }

        return false;
    }

    private void ExplodedLayer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_gpu is not { } engine || _explodedDrag != ExplodedDragKind.None || InExplodedPanel(e.OriginalSource))
            return;

        var at = e.GetPosition(ExplodedLayer);

        // On the way back to the flat picture the layer still takes presses: only Alt + middle means anything then (back into 3D).
        if (!_explodedUi)
        {
            if (e.ChangedButton == MouseButton.Middle && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                e.Handled = true;
                RunGuarded("3D view", () => BeginExplodedFromPress(at, MouseButton.Middle));
            }

            return;
        }

        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle or MouseButton.Right))
            return;

        e.Handled = true;
        RunGuarded("3D view", () =>
        {
            // The right button and the middle orbit, wherever they are pressed.
            if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
            {
                BeginExplodedDrag(ExplodedDragKind.Orbit, at, e.ChangedButton);
                return;
            }

            // The selected plane's corner handle: a resize (never over a drag on the timeline).
            if (!TimelineDragHeld && e.ClickCount == 1 && ExplodedHandleHit(engine, at, out var handled, out var handledView, out var handledHit))
            {
                BeginExplodedPlaneDrag(handled, handledView, handledHit, ExplodedDragKind.Resize, $"resize {handledView.Name}", at);
                return;
            }

            if (!PickExplodedPlane(engine, at, out var plane, out var view, out var hit))
            {
                // The empty space: a double-click puts the camera back; a press there does nothing else (the left button
                // never orbits: the right one does).
                if (e.ClickCount == 2)
                    engine.ResetCamera();
                return;
            }

            // A press selects the layer, as a press on its box on the flat player does; the same press drags it (not over
            // a drag on the timeline, which nothing may change the project under).
            if (!ReferenceEquals(_selectedClip, view))
                SelectClip(view);
            engine.SelectedClipKey = plane.ClipKey;
            if (TimelineDragHeld || e.ClickCount > 1)
                return;

            var depth = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            BeginExplodedPlaneDrag(plane, view, hit, depth ? ExplodedDragKind.Depth : ExplodedDragKind.Move, $"move {view.Name}", at);
        });
    }

    /// <summary>A plane's drag begins: its layer, the camera it was pressed under, where on the plane the pointer is, one undo step.</summary>
    private void BeginExplodedPlaneDrag(in ExplodedPlane plane, Layer view, Vector2 hit, ExplodedDragKind kind, string step, Point at)
    {
        (_explodedPlane, _explodedView, _explodedCamera, _explodedHit) = (plane, view, _explodedScene.Camera, hit);
        _explodedPlaneZ = _explodedCamera.DepthOf(plane.ZDepth);
        _explodedLift = 0;
        _explodedStep = new DragStep(_viewModel, step);
        _explodedStep.Pressed();
        BeginExplodedDrag(kind, at, MouseButton.Left);
    }

    private void BeginExplodedDrag(ExplodedDragKind kind, Point at, MouseButton button)
    {
        (_explodedDrag, _explodedButton, _explodedPress, _explodedLast) = (kind, button, at, at);
        if (!ExplodedLayer.CaptureMouse())
        {
            // Nothing to follow the drag with (the window over the video is not up): the press was all of it.
            _explodedDrag = ExplodedDragKind.None;
            return;
        }

        // An orbit is drawn every refresh while it is held; a plane's drag holds the clip the Center controls stand for (as
        // a box's drag on the flat player does), so the main video's plane keeps moving the clip it was pressed on.
        if (kind == ExplodedDragKind.Orbit)
            _gpu?.SetOrbiting(true);
        else
            _viewModel.HoldCenterTarget(true);
    }

    private void ExplodedLayer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_explodedDrag == ExplodedDragKind.None || _gpu is not { } engine)
            return;

        var at = e.GetPosition(ExplodedLayer);
        var kind = _explodedDrag;
        RunGuarded("3D view", () =>
        {
            switch (kind)
            {
                case ExplodedDragKind.Orbit:
                    // The stack turns with the pointer: dragged right, its front turns right (the camera goes left); dragged
                    // down, its top comes forward (the camera goes up).
                    var moved = at - _explodedLast;
                    engine.Orbit(-moved.X * ExplodedOrbitPerDip, moved.Y * ExplodedOrbitPerDip);
                    break;
                case ExplodedDragKind.Move:
                    MoveExplodedPlane(at);
                    break;
                case ExplodedDragKind.Resize:
                    ResizeExplodedPlane(at);
                    break;
                case ExplodedDragKind.Depth:
                    LiftExplodedPlane(engine, at);
                    break;
            }
        });
        _explodedLast = at;
    }

    private void ExplodedLayer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_explodedDrag == ExplodedDragKind.None || e.ChangedButton != _explodedButton)
            return;

        e.Handled = true;
        RunGuarded("3D view", () => EndExplodedDrag(cancelled: false));
    }

    private void ExplodedLayer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_explodedUi || _gpu is not { } engine)
            return;

        e.Handled = true;

        // Ctrl: the layer in front of the selected one (a notch up) or behind it (down) becomes the selected one.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (_explodedDrag == ExplodedDragKind.None)
                RunGuarded("3D view", () => CycleExplodedSelection(engine, e.Delta > 0 ? 1 : -1));
            return;
        }

        // A notch up moves the camera in.
        engine.Zoom(Math.Pow(ExplodedWheelStep, e.Delta / 120.0));
    }

    /// <summary>
    /// Ctrl + the wheel: the next layer in the stack's order (towards the front, or towards the back) is selected, its plane
    /// and badge in the accent; from none, the front one (or the back one). Each clip once, however many planes it has.
    /// </summary>
    private void CycleExplodedSelection(GpuPreviewEngine engine, int step)
    {
        engine.TryGetExplodedScene(_explodedScene);
        var scene = _explodedScene;
        TakeExplodedInfos(scene);

        // The clips' planes from the back of the stack to the front (their place in it, then the order drawn).
        var planes = new List<(float Depth, int Index, long Key, Layer View)>();
        for (var k = 0; k < scene.Count; k++)
        {
            ref readonly var plane = ref scene.Planes[k];
            var clipKey = plane.ClipKey;
            if (plane.BlurFillOnly || ExplodedViewOf(plane) is not { } view || planes.Exists(p => p.Key == clipKey))
                continue;
            planes.Add((plane.ZDepth, k, clipKey, view));
        }

        if (planes.Count == 0)
            return;
        planes.Sort(static (a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Index.CompareTo(b.Index));

        var at = planes.FindIndex(p => p.Key == engine.SelectedClipKey);
        var next = at < 0 ? (step > 0 ? planes.Count - 1 : 0) : ((at + step) % planes.Count + planes.Count) % planes.Count;
        var (_, _, key, chosen) = planes[next];
        if (!ReferenceEquals(_selectedClip, chosen))
            SelectClip(chosen);
        engine.SelectedClipKey = key;
        _viewModel.StatusText = $"Selected {chosen.Name} ({planes.Count - next} of {planes.Count} from the front): drag it, or Ctrl + the wheel for the next.";
    }

    /// <summary>The drag is over: a plane lifted through the stack moves its track (unless the drag was cancelled), and goes back to its place.</summary>
    private void EndExplodedDrag(bool cancelled)
    {
        var kind = _explodedDrag;
        if (kind == ExplodedDragKind.None)
            return;

        _explodedDrag = ExplodedDragKind.None;
        if (ExplodedLayer.IsMouseCaptured)
            ExplodedLayer.ReleaseMouseCapture();
        _gpu?.SetOrbiting(false);
        try
        {
            if (kind == ExplodedDragKind.Depth && !cancelled)
                MoveExplodedTrack();
        }
        finally
        {
            if (kind == ExplodedDragKind.Depth)
                _gpu?.LiftLayer(0, 0);
            if (kind != ExplodedDragKind.Orbit)
                _viewModel.HoldCenterTarget(false);
            (_explodedView, _explodedStep) = (null, null);
        }
    }

    /// <summary>W, A, S and D over the player while the 3D view is on: in and out, and round (true: taken; MainWindow.Keybinds.cs NavigationFrame).</summary>
    private bool ExplodedNavigates(int zoom, int pan, double seconds)
    {
        if (!_explodedUi || _gpu is not { } engine)
            return false;

        if (zoom != 0)
            engine.Zoom(Math.Pow(NavZoomRate, -zoom * seconds));
        if (pan != 0)
            engine.Orbit(pan * ExplodedNavOrbitRate * seconds, 0);
        return true;
    }

    // ----- Picking and moving -----

    /// <summary>A point of the window over the video (DIPs) as a point of the picture drawn (the worker's output pixels).</summary>
    private Vector2 ExplodedOutputPoint(Point at, int outputWidth, int outputHeight)
    {
        var (width, height) = (Math.Max(ExplodedLayer.ActualWidth, 1), Math.Max(ExplodedLayer.ActualHeight, 1));
        return new Vector2((float)(at.X * outputWidth / width), (float)(at.Y * outputHeight / height));
    }

    /// <summary>
    /// The layer under a point: the nearest plane (along the planes' normal, which is along the ray) whose box the ray meets,
    /// among those that are a clip's (not a blurred background); its view and where on the plane the point is.
    /// </summary>
    private bool PickExplodedPlane(GpuPreviewEngine engine, Point at, out ExplodedPlane picked, out Layer view, out Vector2 hit)
    {
        (picked, view, hit) = (default, null!, default);
        engine.TryGetExplodedScene(_explodedScene);
        var scene = _explodedScene;
        if (scene.Count == 0)
            return false;

        TakeExplodedInfos(scene);
        var camera = scene.Camera;
        var point = ExplodedOutputPoint(at, scene.OutputWidth, scene.OutputHeight);
        var nearest = float.MaxValue;
        var found = false;
        var selected = engine.SelectedClipKey;
        for (var k = 0; k < scene.Count; k++)
        {
            ref readonly var plane = ref scene.Planes[k];
            if (plane.BlurFillOnly)
                continue;

            // Of planes as near (no spread yet), the later in the stack: it is drawn over the earlier. The selected
            // layer wins wherever its box is under the pointer.
            var z = camera.DepthOf(plane.ZDepth);
            var distance = camera.DistanceTo(z);
            var isSelected = selected != 0 && plane.ClipKey == selected;
            if (!isSelected && distance > nearest || !camera.HitPlane(point.X, point.Y, z, out var onPlane)
                || !ExplodedCamera.InBox(onPlane, plane.X, plane.Y, plane.Width, plane.Height, plane.Rotation, plane.AnchorX, plane.AnchorY)
                || ExplodedViewOf(plane) is not { } layer)
                continue;

            (nearest, found, picked, view, hit) = (distance, true, plane, layer, onPlane);
            if (isSelected)
                return true;
        }

        return found;
    }

    /// <summary>How near (DIPs) the pointer must be to the selected plane's corner handle to take it.</summary>
    private const double ExplodedHandleReach = 9;

    /// <summary>The selected plane (and its layer and where on the plane the point is) when the point is on its corner handle.</summary>
    private bool ExplodedHandleHit(GpuPreviewEngine engine, Point at, out ExplodedPlane picked, out Layer view, out Vector2 hit)
    {
        (picked, view, hit) = (default, null!, default);
        if (!SelectedExplodedHandle(engine, out var plane, out var corner) || (corner - at).Length > ExplodedHandleReach || ExplodedViewOf(plane) is not { } layer)
            return false;

        var camera = _explodedScene.Camera;
        var point = ExplodedOutputPoint(at, _explodedScene.OutputWidth, _explodedScene.OutputHeight);
        if (!camera.HitPlane(point.X, point.Y, camera.DepthOf(plane.ZDepth), out var onPlane))
            return false;
        (picked, view, hit) = (plane, layer, onPlane);
        return true;
    }

    /// <summary>
    /// The selected plane and where its box's far corner (its bottom right, the flat player's resize handle) is in the window
    /// over the video, from the scene drawn last; false when nothing selected is drawn.
    /// </summary>
    private bool SelectedExplodedHandle(GpuPreviewEngine engine, out ExplodedPlane plane, out Point corner)
    {
        (plane, corner) = (default, default);
        var scene = _explodedScene;
        var selected = engine.SelectedClipKey;
        if (selected == 0 || scene.Count == 0)
            return false;

        for (var k = 0; k < scene.Count; k++)
        {
            ref readonly var candidate = ref scene.Planes[k];
            if (candidate.BlurFillOnly || candidate.ClipKey != selected)
                continue;

            var camera = scene.Camera;
            var far = ExplodedCamera.BoxPoint(candidate.X, candidate.Y, candidate.Width, candidate.Height, candidate.Rotation, candidate.AnchorX, candidate.AnchorY, 1, 1);
            var on = camera.Project(far.X, far.Y, camera.DepthOf(candidate.ZDepth));
            if (!float.IsFinite(on.X) || !float.IsFinite(on.Y))
                return false;
            var (toX, toY) = (ExplodedLayer.ActualWidth / Math.Max(scene.OutputWidth, 1), ExplodedLayer.ActualHeight / Math.Max(scene.OutputHeight, 1));
            (plane, corner) = (candidate, new Point(on.X * toX, on.Y * toY));
            return true;
        }

        return false;
    }

    /// <summary>
    /// The selected plane's corner handle dragged: the layer grows by the pointer's move on its plane, turned into the box's
    /// own axes (a turned layer grows along its own sides), as the flat player's handle grows it (ResizeTrackLayer).
    /// </summary>
    private void ResizeExplodedPlane(Point at)
    {
        if (_explodedView is not { } view)
            return;

        var camera = _explodedCamera;
        var point = ExplodedOutputPoint(at, camera.OutputWidth, camera.OutputHeight);
        if (!camera.HitPlane(point.X, point.Y, _explodedPlaneZ, out var onPlane))
            return;

        var moved = onPlane - _explodedHit;
        _explodedHit = onPlane;
        if (moved == Vector2.Zero || !float.IsFinite(moved.X) || !float.IsFinite(moved.Y))
            return;

        var turn = -_explodedPlane.Rotation * MathF.PI / 180;
        var (cos, sin) = (MathF.Cos(turn), MathF.Sin(turn));
        var local = new Vector2(moved.X * cos - moved.Y * sin, moved.X * sin + moved.Y * cos);
        var (frameWidth, frameHeight) = (Math.Max(_viewModel.FrameWidth, 1), Math.Max(_viewModel.FrameHeight, 1));
        _explodedStep?.Changing();
        ResizeTrackLayer(view, local.X * (double)frameWidth / camera.CanvasWidth, local.Y * (double)frameHeight / camera.CanvasHeight);
    }

    /// <summary>The clip view a plane is the picture of (from its clip's id); null for one that is no clip's (the whole picture, the captions).</summary>
    private Layer? ExplodedViewOf(in ExplodedPlane plane)
    {
        if (ExplodedInfoOf(plane) is not { ClipId.Length: > 0 } info || !Guid.TryParse(info.ClipId, out var id)
            || _viewModel.Document.FindClip(id) is not { } clip)
            return null;
        return _viewModel.ViewOf(clip);
    }

    /// <summary>The plane dragged in its plane: the layer moves by as much as the point under the pointer moved on it.</summary>
    private void MoveExplodedPlane(Point at)
    {
        if (_explodedView is not { } view)
            return;

        var camera = _explodedCamera;
        var point = ExplodedOutputPoint(at, camera.OutputWidth, camera.OutputHeight);
        if (!camera.HitPlane(point.X, point.Y, _explodedPlaneZ, out var onPlane))
            return;

        var moved = onPlane - _explodedHit;
        _explodedHit = onPlane;
        if (moved == Vector2.Zero || !float.IsFinite(moved.X) || !float.IsFinite(moved.Y))
            return;

        // Canvas pixels to the frame's (the same, but for a canvas the engine stood in for).
        var (frameWidth, frameHeight) = (Math.Max(_viewModel.FrameWidth, 1), Math.Max(_viewModel.FrameHeight, 1));
        var (toX, toY) = ((double)frameWidth / camera.CanvasWidth, (double)frameHeight / camera.CanvasHeight);

        // The frame as large as it looks there: the guides' reach is a few pixels of it on screen.
        var left = camera.Project(0, camera.CanvasHeight / 2f, _explodedPlaneZ);
        var right = camera.Project(camera.CanvasWidth, camera.CanvasHeight / 2f, _explodedPlaneZ);
        var across = float.IsFinite(left.X) && float.IsFinite(right.X) ? Vector2.Distance(left, right) * ExplodedLayer.ActualWidth / Math.Max(camera.OutputWidth, 1) : ExplodedLayer.ActualWidth;
        var frame = new Rect(0, 0, Math.Max(across, 1), Math.Max(across * frameHeight / frameWidth, 1));

        _explodedStep?.Changing();
        MoveTrackLayer(view, moved.X * toX, moved.Y * toY, frame, null);
    }

    /// <summary>
    /// The plane dragged through the stack (Shift): lifted by the pointer's move along the stack's depth as it is seen at the
    /// plane (a place per spacing; straight on, a place per <see cref="ExplodedDepthFallback"/> DIPs upward).
    /// </summary>
    private void LiftExplodedPlane(GpuPreviewEngine engine, Point at)
    {
        var camera = _explodedCamera;
        var (toX, toY) = (ExplodedLayer.ActualWidth / Math.Max(camera.OutputWidth, 1), ExplodedLayer.ActualHeight / Math.Max(camera.OutputHeight, 1));
        var near = camera.Project(_explodedHit.X, _explodedHit.Y, _explodedPlaneZ);
        var far = camera.Project(_explodedHit.X, _explodedHit.Y, _explodedPlaneZ + camera.Spread);
        var (alongX, alongY) = ((far.X - near.X) * toX, (far.Y - near.Y) * toY);
        var length = alongX * alongX + alongY * alongY;
        if (!double.IsFinite(length) || length < 8 * 8)
            (alongX, alongY, length) = (0, -ExplodedDepthFallback, ExplodedDepthFallback * ExplodedDepthFallback);

        var moved = at - _explodedPress;
        var places = (moved.X * alongX + moved.Y * alongY) / length;
        var top = Math.Max(_explodedScene.LayerCount - 1, 0);
        _explodedLift = Math.Clamp(places, -_explodedPlane.ZDepth - 0.5, top - _explodedPlane.ZDepth + 0.5);
        engine.LiftLayer(_explodedPlane.ClipKey, _explodedLift);
    }

    /// <summary>
    /// A plane let go after a lift through the stack: beyond another track's plane, its track moves past the farthest one it
    /// passed (the Layers' reorder, a track at a time), the whole move one undo step. Short of any, nothing changes.
    /// </summary>
    private void MoveExplodedTrack()
    {
        if (_explodedView is not { } view || Math.Abs(_explodedLift) < 0.5 || _viewModel.ClipOf(view) is not { } model
            || _viewModel.Document.TrackOf(model) is not { } track)
            return;

        var (from, to, forward) = (_explodedPlane.ZDepth, _explodedPlane.ZDepth + _explodedLift, _explodedLift > 0);
        if (_gpu is { } engine)
            engine.TryGetExplodedScene(_explodedScene);
        var scene = _explodedScene;
        TakeExplodedInfos(scene);

        // The farthest plane of another track it went past.
        Models.Timeline.TimelineTrackModel? target = null;
        string? targetName = null;
        var farthest = from;
        for (var k = 0; k < scene.Count; k++)
        {
            ref readonly var plane = ref scene.Planes[k];
            if (plane.ClipKey == _explodedPlane.ClipKey || plane.BlurFillOnly || ExplodedViewOf(plane) is not { } other
                || _viewModel.ClipOf(other) is not { } clip || _viewModel.Document.TrackOf(clip) is not { } otherTrack || ReferenceEquals(otherTrack, track))
                continue;

            var depth = plane.ZDepth;
            var passed = forward ? depth > from && depth < to && depth > farthest : depth < from && depth > to && depth < farthest;
            if (passed)
                (target, targetName, farthest) = (otherTrack, other.Name, depth);
        }

        if (target is null)
        {
            _viewModel.StatusText = $"{view.Name} stays where it is in the stack: drag it past another layer's plane to move its track there.";
            return;
        }

        var name = view.Name;
        var moved = _viewModel.EditTogether($"move {name} {(forward ? "forward" : "back")} in the stack", () =>
        {
            var tracks = _viewModel.Document.Tracks;
            var step = forward ? _viewModel.MoveLayerUpCommand : _viewModel.MoveLayerDownCommand;
            for (var guard = tracks.Count; guard > 0; guard--)
            {
                var (mine, theirs) = (tracks.IndexOf(track), tracks.IndexOf(target));
                if (mine < 0 || theirs < 0 || (forward ? mine > theirs : mine < theirs))
                    break;
                step.Execute(view);
                if (tracks.IndexOf(track) == mine)
                    break;
            }
        });

        // The Timeline shows the new order at once (5.0-Release, the user's directive 4: its rows are laid out now, not after
        // the queued layout): the track's row in its place, and a child moved across its parent above or below its row.
        if (moved)
        {
            ScheduleClipRedraw();
            FlushClipRedraw();
        }

        _viewModel.StatusText = moved
            ? $"{name} moved {(forward ? "in front of" : "behind")} {targetName}: its track is there in the stack now. Ctrl+Z puts it back."
            : $"{name} could not be moved in the stack.";
    }
}
