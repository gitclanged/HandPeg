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
//   the layer selected wins wherever its box is under the pointer, even behind another (so the layer Alt + the wheel
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
//   (GpuScenePlan.PlaceLayers), so the planes keep theirs while layers come and go. The captions' plane (5.1-Polish, the
//   user's Q2) is picked, moved and resized as their box is, and lifted past a track's plane they go over it or under it
//   (MoveExplodedCaptions: the track's CaptionsUnder), drawn there by the preview and by every export.
// - The right button (or the middle; Alt + middle from the flat player) orbits, from anywhere; a mouse's wheel moves the
//   camera in and out; Alt + the wheel, or [ and ], selects the layer in front of or behind the selected one, in the stack's
//   order (its plane and badge in the accent); Shift + the wheel sets the spacing; a double-click on the empty space puts the
//   camera back; Esc, the panel's close button or the toggle leaves.
// - Touchpads and touch screens (5.3-Stage3, the user's ask): a pinch (a precision touchpad's is Ctrl + the wheel, which is
//   kept for it: the layers moved to Alt) moves the camera in and out; a touchpad's two-finger scroll orbits (its wheel told
//   from a mouse's by GetCurrentInputMessageSource); on a touch screen one finger on the empty space orbits, two pinch, and two
//   dragged up or down set the spacing (ExplodedLayer's manipulation), while a finger on a plane, a handle or the panel is
//   the mouse's (its touch promoted to the mouse: select, move, resize, the slider).
public partial class MainWindow
{
    private enum ExplodedDragKind { None, Orbit, Move, Depth, Resize, AlongX, AlongY }

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

    // The label badges (made as needed, kept; each placed by its own translation, 5.3-Stage3), the flat picture's overlays hidden
    // under the 3D view (with their opacity), and the player's own zoom matrix the 3D layer last cancelled.
    private readonly List<(Border Badge, TextBlock Text, TranslateTransform Place)> _explodedBadges = [];
    private readonly List<Rect> _explodedBadgeBoxes = [];
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

    // When the 3D view was left, and how long its layer may wait for a flat picture (the way back is 240 ms of camera, each step at
    // most a 60 Hz refresh: 15 frames, slow ones included).
    private long _explodedLeftAt;

    // The front tab's last 3D camera (5.4.1-M1, the user's answer Q2 b of 5.4D): taken as the view is left, given back as it is turned on
    // again by hand in the same tab; kept in the tab's header when it is put behind and set from it as a tab comes to the front (null:
    // never in 3D here, the camera's start). The Reset button sets the start again.
    private GpuPreviewEngine.CameraPose? _explodedLastPose;
    private const long ExplodedWayBackMs = 1500;

    /// <summary>
    /// Into the 3D view (the camera swings out of the flat picture) or out of it (back into the flat picture, the overlays of
    /// the flat player coming back once it is there). Only on the GPU engine, with a video open.
    /// <para>5.4D, a project tab put behind or brought back (MainWindow.ProjectTabs.cs): <paramref name="instant"/> leaves or enters
    /// it at once, with no way out or in; into it at <paramref name="pose"/> (the tab's camera, its spacing the panel's again) and with
    /// no status line. Turned on by hand the camera still starts where it always does.</para>
    /// </summary>
    private void SetExplodedView(bool on, bool instant = false, GpuPreviewEngine.CameraPose? pose = null)
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

            _explodedUi = true;
            NoteStillStale();
            engine.SelectedClipKey = SelectedExplodedKey();
            if (instant)
            {
                // A project tab come back to the front (5.4D): its camera where it was left, at once (the panel's spacing with it).
                var at = GpuPreviewEngine.HeldToLimits(pose ?? new(GpuPreviewEngine.DefaultYaw, GpuPreviewEngine.DefaultPitch, GpuPreviewEngine.DefaultZoom, ExplodedSpacing.Value));
                ExplodedSpacing.Value = Math.Max(ExplodedSpacing.Minimum, Math.Min(at.Spacing, ExplodedSpacing.Maximum));
                engine.SetExplodedInstant(true, at with { Spacing = ExplodedSpacing.Value });
                ShowExplodedLayer();
                SyncExplodedToggle();
                return;
            }

            // The camera where this tab left it (5.4.1-M1), else where it always starts; the spacing is the panel's, kept for the session.
            if (_explodedLastPose is { } last)
                engine.SetCamera(last);
            else
                engine.ResetCamera();
            engine.ZSpacing = ExplodedSpacing.Value;
            engine.SetExploded(true);
            ShowExplodedLayer();
            _viewModel.StatusText = "3D Exploded View: right-drag or two-finger scroll to orbit, the wheel or a pinch to zoom, Alt + the wheel or [ ] to pick the layer in front or behind; drag a layer to move it in its plane (its corner handle resizes it), Shift + drag to move it through the stack. Esc goes back.";
        }
        else
        {
            EndExplodedDrag(cancelled: true);
            _explodedUi = false;
            if (_gpu is { } left)
                _explodedLastPose = left.TargetPose;
            _explodedLeftAt = Environment.TickCount64;
            NoteStillStale();
            if (instant)
            {
                // A project tab put behind or closed (5.4D): the flat picture at once, and the layer, panel and badges gone with it.
                _gpu?.SetExplodedInstant(false);
                HideExplodedLayer();
                SyncExplodedToggle();
                return;
            }

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

    /// <summary>
    /// The flat picture at once (5.4D: a project tab put behind or closed): the 3D view left with no way out, and one already on its way
    /// out ended where it is, so the next tab's first scenes are never drawn in 3D and its overlays never wait hidden for the way back.
    /// </summary>
    private void LeaveExplodedAtOnce()
    {
        if (_explodedUi)
        {
            SetExplodedView(false, instant: true);
            return;
        }

        if (_gpu is { IsDrawingExploded: true } engine)
            engine.SetExplodedInstant(false);
        if (_explodedTicking)
            HideExplodedLayer();
    }

    private void ShowExplodedLayer()
    {
        // The flat player's boxes, crop and margins are drawn where the flat picture has things: out of sight in 3D.
        if (_explodedHidden.Count == 0)
        {
            foreach (UIElement child in PlayerOverlay.Children)
            {
                // A project tab's still (5.4D) is the picture of the view, flat or 3D, until the engine has drawn the tab: never hidden.
                if (ReferenceEquals(child, ExplodedLayer) || ReferenceEquals(child, _tabStill))
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

        // Nothing of this view's last moment is left to show when it comes back: the badges and the marks wait for its first scene.
        foreach (var (badge, _, _) in _explodedBadges)
            badge.Visibility = Visibility.Collapsed;
        if (_explodedMarks is { } marks)
            marks.Open().Close();
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
        NoteStillStale();
    }

    private void ExplodedReset_Click(object sender, RoutedEventArgs e)
    {
        _gpu?.ResetCamera();
        _explodedLastPose = null;
        NoteStillStale();
    }

    private void ExplodedClose_Click(object sender, RoutedEventArgs e) => SetExplodedView(false);

    /// <summary>The clip selected (the timeline's selection) as the engine's key (the captions' own, selected); 0 none.</summary>
    private long SelectedExplodedKey() => _selectedClip is Layer { IsCaptions: true } ? GpuScenePlan.CaptionClipKey
        : _selectedClip is Layer { IsBackground: true } ? GpuScenePlan.BlurFillKey
        : _selectedClip is Layer { ClipId: var id } && id != Guid.Empty ? GpuHash.Of(id) : 0;

    /// <summary>The name a Background Blur plane goes by in the 3D view (its badge, the status line).</summary>
    private const string ExplodedBlurName = "Background Blur";

    // ----- Every frame while the 3D view is up -----

    private void OnExplodedFrame(object? sender, EventArgs e)
    {
        var engine = _gpu;

        // The engine gone (stopped, or faulted and not yet back) or the video closed: the view is over.
        if (_explodedUi && (engine is null || !_viewModel.HasSource))
        {
            SetExplodedView(false);
            engine = _gpu;
        }

        // Paused, the engine may have no frame to tell by: the way back ends all the same, or the flat player's boxes under this
        // layer take no press (5.1-TrackEqualityAndVram, the user's directive).
        if (engine is null || !_explodedUi && (!engine.IsDrawingExploded || Environment.TickCount64 - _explodedLeftAt > ExplodedWayBackMs))
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

    /// <summary>
    /// A badge over each named plane, at its box's top left corner as drawn; masked layers in the outline's cyan, the selected in amber.
    /// Every frame of a camera move (5.3-Stage3): a badge is moved by its translation and faded by its opacity, which WPF only draws
    /// (its Measure and Arrange run when its name or weight changes, or it comes or goes), and the handle and the gizmo are drawn
    /// anew into one drawing (ExplodedMarks), never laid out.
    /// </summary>
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

        // Front to back, so a plane's badge that would lie over one nearer the camera yields (5.2.0's re-cut: the Background Blur's, whose
        // corner is the frame's, met a top Cutout's), moved up past it, or down where there is no room above.
        _explodedBadgeBoxes.Clear();
        for (var k = scene.Count - 1; k >= 0; k--)
        {
            ref readonly var plane = ref scene.Planes[k];
            if (ExplodedInfoOf(plane) is not { Name.Length: > 0 } info)
                continue;

            var corner = ExplodedCamera.BoxPoint(plane.X, plane.Y, plane.Width, plane.Height, plane.Rotation, plane.AnchorX, plane.AnchorY, 0, 0);
            var at = camera.Project(corner.X, corner.Y, camera.DepthOf(plane.ZDepth));
            if (!float.IsFinite(at.X) || !float.IsFinite(at.Y))
                continue;

            var (badge, text, place) = ExplodedBadge(shown++);
            var selected = (plane.Flags & GpuLayerDescriptor.FlagSelected) != 0;
            var name = plane.BlurFillOnly ? ExplodedBlurName : info.Name;
            text.Text = selected ? "▲ " + name : name;

            // The selected layer's badge is filled with the accent (its plane's box is too), so a drag's layer is never in doubt;
            // another clip of its track, playing while it is not, has the accent's edge (its plane's fainter outline).
            var selectedTrack = (plane.Flags & GpuLayerDescriptor.FlagSelectedTrack) != 0;
            badge.Background = selected ? ExplodedSelectedEdge : ExplodedBadgeFill;
            badge.BorderBrush = selected || selectedTrack ? ExplodedSelectedEdge : info.HasMask && !plane.BlurFillOnly ? ExplodedMaskEdge : ExplodedBadgeEdge;
            text.Foreground = selected ? ExplodedSelectedText : plane.BlurFillOnly ? ExplodedMutedText : ExplodedBadgeText;
            text.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            Panel.SetZIndex(badge, selected ? 1 : 0);
            badge.Opacity = camera.Amount;
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = badge.DesiredSize;
            var box = new Rect(Math.Clamp(at.X * toX, 2, Math.Max(width - 60, 2)), Math.Clamp(at.Y * toY - 21, 2, Math.Max(height - 20, 2)), size.Width, size.Height);
            for (var tries = 0; tries < 8 && _explodedBadgeBoxes.FindIndex(placed => placed.IntersectsWith(box)) is >= 0 and var hit; tries++)
            {
                var over = _explodedBadgeBoxes[hit];
                box.Y = over.Top - box.Height - 2 >= 2 ? over.Top - box.Height - 2 : over.Bottom + 2;
            }

            _explodedBadgeBoxes.Add(box);
            (place.X, place.Y) = (box.X, box.Y);
            badge.Visibility = Visibility.Visible;
        }

        for (var k = shown; k < _explodedBadges.Count; k++)
            _explodedBadges[k].Badge.Visibility = Visibility.Collapsed;

        // The selected plane's corner handle (the resize grip), where its box's far corner is drawn, and the gizmo's arrows.
        var marks = _explodedMarks ??= MakeExplodedMarks();
        using var drawing = marks.Open();
        drawing.PushOpacity(camera.Amount);
        if (_gpu is { } engine && SelectedExplodedHandle(engine, out _, out var grip) && grip.X >= 0 && grip.Y >= 0 && grip.X <= width && grip.Y <= height)
        {
            // 10 DIPs across with its 1 DIP edge inside, as the Border it was.
            const double inner = ExplodedHandleSide - 1;
            drawing.DrawRoundedRectangle(ExplodedSelectedEdge, ExplodedHandlePen, new Rect(grip.X - inner / 2, grip.Y - inner / 2, inner, inner), 2, 2);
        }

        PlaceExplodedGizmo(camera, toX, toY, drawing);
        drawing.Pop();
    }

    // ----- The 3D view's 3-axis gizmo (5.1-GpuFx; a tool, so shown whatever Extra Animations says since 5.2) -----

    // Across in red, down the picture in green, through the stack in blue. Drawn in vector (Section 1), on the selected plane; a press on an arrow moves along it alone.
    private readonly Point[] _gizmoTips = new Point[3];
    private Point _gizmoMiddle;
    private bool _gizmoShown;

    /// <summary>The arrows' length, as a share of the canvas's shorter side; how near (DIPs) a press must be to take one.</summary>
    private const float GizmoLength = 0.14f;
    private const double GizmoReach = 8;

    private static readonly SolidColorBrush[] GizmoColors = [FrozenBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)), FrozenBrush(Color.FromRgb(0x45, 0xE0, 0x7A)), FrozenBrush(Color.FromRgb(0x4D, 0x9D, 0xFF))];
    private static readonly Pen[] GizmoPens = [.. GizmoColors.Select(color => FrozenPen(color, 2))];

    /// <summary>The arrows at the selected plane's middle, from the scene drawn last, into the marks' drawing.</summary>
    private void PlaceExplodedGizmo(in ExplodedCamera camera, double toX, double toY, DrawingContext drawing)
    {
        _gizmoShown = false;
        if (_gpu is not { } engine || !SelectedExplodedHandle(engine, out var plane, out _))
            return;

        var z = camera.DepthOf(plane.ZDepth);
        var middle = ExplodedCamera.BoxPoint(plane.X, plane.Y, plane.Width, plane.Height, plane.Rotation, plane.AnchorX, plane.AnchorY, 0.5f, 0.5f);
        var length = Math.Min(camera.CanvasWidth, camera.CanvasHeight) * GizmoLength;
        var from = camera.Project(middle.X, middle.Y, z);
        Span<Vector2> tips = [camera.Project(middle.X + length, middle.Y, z), camera.Project(middle.X, middle.Y - length, z), camera.Project(middle.X, middle.Y, z + length)];
        if (!float.IsFinite(from.X) || !float.IsFinite(from.Y))
            return;

        _gizmoMiddle = new Point(from.X * toX, from.Y * toY);
        _gizmoShown = true;
        for (var axis = 0; axis < 3; axis++)
        {
            var tip = float.IsFinite(tips[axis].X) && float.IsFinite(tips[axis].Y) ? new Point(tips[axis].X * toX, tips[axis].Y * toY) : _gizmoMiddle;
            _gizmoTips[axis] = tip;
            var way = tip - _gizmoMiddle;
            if (way.Length < 6)
                continue;

            way.Normalize();
            var side = new Vector(-way.Y, way.X) * 4.5;
            var back = tip - way * 11;
            drawing.DrawLine(GizmoPens[axis], _gizmoMiddle, back);
            var head = new StreamGeometry();
            using (var path = head.Open())
            {
                path.BeginFigure(tip, isFilled: true, isClosed: true);
                path.LineTo(back + side, isStroked: false, isSmoothJoin: false);
                path.LineTo(back - side, isStroked: false, isSmoothJoin: false);
            }

            head.Freeze();
            drawing.DrawGeometry(GizmoColors[axis], null, head);
        }
    }

    /// <summary>Which arrow a point is on (0 across, 1 down, 2 through the stack), the nearest within reach; -1 for none.</summary>
    private int ExplodedAxisAt(Point at)
    {
        var (best, nearest) = (-1, GizmoReach);
        for (var axis = 0; _gizmoShown && axis < 3; axis++)
        {
            var way = _gizmoTips[axis] - _gizmoMiddle;
            var length = way.LengthSquared;
            if (length < 36)
                continue;

            var along = Math.Clamp(((at - _gizmoMiddle) * way) / length, 0, 1);
            var distance = (at - (_gizmoMiddle + way * along)).Length;
            if (distance < nearest)
                (best, nearest) = (axis, distance);
        }

        return best;
    }

    // The selected plane's corner handle: its size, and its edge.
    private const double ExplodedHandleSide = 10;
    private static readonly Pen ExplodedHandlePen = FrozenPen(ExplodedSelectedText, 1);

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// The handle and the gizmo of the selected plane (5.3-Stage3): one element over the badges whose picture is a drawing filled
    /// again each frame (<see cref="Open"/>), which WPF draws without laying anything out. Its lines and shapes were elements
    /// placed by Canvas.Left, Top and their points until then, every one of them measured and arranged again each frame of a move.
    /// </summary>
    private sealed class ExplodedMarks : FrameworkElement
    {
        private readonly DrawingGroup _drawing = new();

        public ExplodedMarks() => IsHitTestVisible = false;

        /// <summary>The drawing anew: what is drawn into the context until it is closed replaces what was there.</summary>
        public DrawingContext Open() => _drawing.Open();

        protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawDrawing(_drawing);
    }

    private ExplodedMarks? _explodedMarks;

    private ExplodedMarks MakeExplodedMarks()
    {
        var marks = new ExplodedMarks();
        Panel.SetZIndex(marks, 3);
        ExplodedBadges.Children.Add(marks);
        return marks;
    }

    private (Border Badge, TextBlock Text, TranslateTransform Place) ExplodedBadge(int index)
    {
        while (_explodedBadges.Count <= index)
        {
            var text = new TextBlock { FontSize = 11, Foreground = ExplodedBadgeText, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
            var place = new TranslateTransform();
            var badge = new Border
            {
                Child = text, Background = ExplodedBadgeFill, BorderBrush = ExplodedBadgeEdge, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 2), IsHitTestVisible = false, RenderTransform = place,
            };
            ExplodedBadges.Children.Add(badge);
            _explodedBadges.Add((badge, text, place));
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
        VideoView.OverlayMessageHook += ExplodedOverlayMessage;
        ExplodedLayer.IsManipulationEnabled = true;
        ExplodedLayer.ManipulationStarting += ExplodedLayer_ManipulationStarting;
        ExplodedLayer.ManipulationDelta += ExplodedLayer_ManipulationDelta;
        ExplodedLayer.ManipulationCompleted += ExplodedLayer_ManipulationCompleted;

        // After the window's own keys (an open dial or the tour take Esc first).
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(ExplodedWindow_PreviewKeyDown));
    }

    private const int WmMiddleButtonDown = 0x0207;

    // The video's native window: a press there is no WPF input, so Alt + middle is caught from its messages; and in 3D, a wheel
    // that lands on it (not on the window over it) is the 3D view's as well.
    private IntPtr ExplodedSurfaceMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmMouseWheel or WmMouseHWheel)
            return ExplodedOverlayMessage(hwnd, msg, wParam, lParam, ref handled);
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
        if (e.Key is not (Key.Escape or Key.OemOpenBrackets or Key.OemCloseBrackets) || !_explodedUi
            || Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem)
            return;

        // [ and ] with no modifier (5.3-Stage3): the layer behind the selected one, or in front of it, as Alt + the wheel picks them.
        // The view's own keys while it is up, as Esc is (a key map that binds them to something else keeps them).
        if (e.Key != Key.Escape)
        {
            if (Keyboard.Modifiers != ModifierKeys.None || _gpu is not { } engine)
                return;
            if (_explodedDrag == ExplodedDragKind.None)
                RunGuarded("3D view", () => CycleExplodedSelection(engine, e.Key == Key.OemCloseBrackets ? 1 : -1));
            e.Handled = true;
            return;
        }

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

            // An arrow of the gizmo: along it alone.
            if (!TimelineDragHeld && e.ClickCount == 1 && ExplodedAxisAt(at) is >= 0 and var axis
                && SelectedExplodedHandle(engine, out var held, out _) && ExplodedViewOf(held) is { } heldView)
            {
                var pressed = ExplodedOutputPoint(at, _explodedScene.OutputWidth, _explodedScene.OutputHeight);
                if (_explodedScene.Camera.HitPlane(pressed.X, pressed.Y, _explodedScene.Camera.DepthOf(held.ZDepth), out var onHeld))
                {
                    BeginExplodedPlaneDrag(held, heldView, onHeld, axis == 2 ? ExplodedDragKind.Depth : axis == 0 ? ExplodedDragKind.AlongX : ExplodedDragKind.AlongY, $"move {heldView.Name}", at);
                    return;
                }
            }

            if (!PickExplodedPlane(engine, at, out var plane, out var view, out var hit, out var hitDepth))
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

            // The Background Blur: selected (its row, and its look in the inspector), never dragged: it covers the frame by itself.
            if (plane.BlurFillOnly)
            {
                engine.SelectedClipKey = GpuScenePlan.BlurFillKey;
                _viewModel.StatusText = $"Selected the {ExplodedBlurName}: under its clip and every Cutout of it. Its blur, zoom and dimming are in the inspector; each clip's menu turns its Blur Fill on or off.";
                return;
            }

            engine.SelectedClipKey = plane.ClipKey;
            if (TimelineDragHeld || e.ClickCount > 1)
                return;

            var depth = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            BeginExplodedPlaneDrag(plane, view, hit, depth ? ExplodedDragKind.Depth : ExplodedDragKind.Move, $"move {view.Name}", at, hitDepth);
        });
    }

    /// <summary>
    /// A plane's drag begins: its layer, the camera it was pressed under, where on the plane the pointer is (at the depth it is at:
    /// <paramref name="depth"/>, a tilted plane's point, 5.2C; NaN its plane's), one undo step.
    /// </summary>
    private void BeginExplodedPlaneDrag(in ExplodedPlane plane, Layer view, Vector2 hit, ExplodedDragKind kind, string step, Point at, float depth = float.NaN)
    {
        (_explodedPlane, _explodedView, _explodedCamera, _explodedHit) = (plane, view, _explodedScene.Camera, hit);
        _explodedPlaneZ = float.IsFinite(depth) ? depth : _explodedCamera.DepthOf(plane.ZDepth);
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

        // An orbit is drawn every refresh while it is held; a plane's drag holds the clip the Crop boxes stand for (as a box's
        // drag on the flat player does), so the plane keeps moving the clip it was pressed on.
        if (kind == ExplodedDragKind.Orbit)
            _gpu?.SetOrbiting(true);
        else
            _viewModel.HoldCropTarget(true);
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
                case ExplodedDragKind.Move or ExplodedDragKind.AlongX or ExplodedDragKind.AlongY:
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

    // A wheel that reaches WPF over the 3D layer (the window over the video answers it first: ExplodedOverlayMessage).
    private void ExplodedLayer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ExplodedWheel(e.Delta, horizontal: false, Keyboard.Modifiers, FromTouchpad()))
            e.Handled = true;
    }

    private const int WmMouseWheel = 0x020A, WmMouseHWheel = 0x020E, MkShift = 0x0004, MkControl = 0x0008;

    /// <summary>
    /// The wheel over the 3D view, from its own messages (the window over the video, and the video's native window): their modifier keys
    /// as the message carries them (a precision touchpad's pinch is Ctrl + the wheel), the horizontal wheel too (a touchpad's sideways
    /// scroll, a tilted wheel), which WPF raises nothing for.
    /// </summary>
    private IntPtr ExplodedOverlayMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (WmMouseWheel or WmMouseHWheel) || !_explodedUi)
            return IntPtr.Zero;

        var word = wParam.ToInt64();
        var keys = Keyboard.Modifiers & ModifierKeys.Alt;
        if ((word & MkShift) != 0)
            keys |= ModifierKeys.Shift;
        if ((word & MkControl) != 0)
            keys |= ModifierKeys.Control;
        var delta = (short)((word >> 16) & 0xFFFF);
        var horizontal = msg == WmMouseHWheel;
        var touchpad = FromTouchpad();
        if (ExplodedWheel(delta, horizontal, keys, touchpad))
        {
            handled = true;

            // A horizontal wheel's message is answered TRUE, or Windows sends it on as clicks of an old tilt wheel.
            return horizontal ? new IntPtr(1) : IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // Degrees a touchpad's scroll (or a tilted wheel) turns the camera per notch's worth (120); how far a notch of Shift + the
    // wheel moves the spacing; what Alt + the wheel has turned towards the next layer and not yet taken.
    private const double ExplodedScrollOrbit = 6, ExplodedSpacingPerNotch = 0.02;
    private double _explodedCycleRest;

    /// <summary>
    /// The wheel over the 3D view (5.3-Stage3), <paramref name="delta"/> in Windows' 120 a notch: Ctrl (a mouse's, or a touchpad's
    /// pinch) moves the camera in and out; Alt selects the layer in front or behind, a notch's worth at a time; Shift sets the
    /// spacing; a touchpad's two-finger scroll (or a horizontal wheel) orbits; a mouse's wheel moves the camera in and out, as it
    /// always has. False when the 3D view is not up.
    /// </summary>
    private bool ExplodedWheel(int delta, bool horizontal, ModifierKeys keys, bool fromTouchpad)
    {
        if (!_explodedUi || _gpu is not { } engine)
            return false;

        var notches = delta / 120.0;
        switch (ExplodedWheelDoes(horizontal, keys, fromTouchpad))
        {
            case ExplodedWheelAction.Zoom:
                // A notch up (a pinch spread apart) moves the camera in.
                engine.Zoom(Math.Pow(ExplodedWheelStep, notches));
                break;
            case ExplodedWheelAction.Layers when _explodedDrag == ExplodedDragKind.None:
                // A notch up the layer in front, down the one behind; a touchpad's little steps add up to a notch first.
                _explodedCycleRest += delta;
                while (Math.Abs(_explodedCycleRest) >= 120)
                {
                    var step = Math.Sign(_explodedCycleRest);
                    _explodedCycleRest -= step * 120;
                    RunGuarded("3D view", () => CycleExplodedSelection(engine, step));
                }

                break;
            case ExplodedWheelAction.Spacing:
                ExplodedSpacing.Value = Math.Clamp(ExplodedSpacing.Value + notches * ExplodedSpacingPerNotch, ExplodedSpacing.Minimum, ExplodedSpacing.Maximum);
                break;
            case ExplodedWheelAction.Turn:
                // Fingers to the left scroll right: the stack turns as a drag to the left turns it.
                engine.Orbit(notches * ExplodedScrollOrbit, 0);
                break;
            case ExplodedWheelAction.Tip:
                // Two fingers down (Windows' natural direction: the wheel turned up) bring the stack's top forward, as a drag down does.
                engine.Orbit(0, notches * ExplodedScrollOrbit);
                break;
        }

        NoteStillStale();
        return true;
    }

    /// <summary>What a turn of the wheel does in the 3D view.</summary>
    internal enum ExplodedWheelAction { None, Zoom, Layers, Spacing, Turn, Tip }

    /// <summary>
    /// What the wheel does in the 3D view (5.3-Stage3; pure, for exploded-3d-gestures): Ctrl (a mouse's, or a precision touchpad's
    /// pinch) zooms; Alt picks the layer in front or behind; Shift sets the spacing; sideways (a touchpad, a tilted wheel) turns the
    /// stack; a touchpad's two-finger scroll tips it; a mouse's own wheel zooms, as it always has. A sideways wheel with a key does nothing.
    /// </summary>
    internal static ExplodedWheelAction ExplodedWheelDoes(bool horizontal, ModifierKeys keys, bool fromTouchpad) =>
        keys.HasFlag(ModifierKeys.Control) ? (horizontal ? ExplodedWheelAction.None : ExplodedWheelAction.Zoom)
        : keys.HasFlag(ModifierKeys.Alt) ? (horizontal ? ExplodedWheelAction.None : ExplodedWheelAction.Layers)
        : keys.HasFlag(ModifierKeys.Shift) ? (horizontal ? ExplodedWheelAction.None : ExplodedWheelAction.Spacing)
        : horizontal ? ExplodedWheelAction.Turn
        : fromTouchpad ? ExplodedWheelAction.Tip
        : ExplodedWheelAction.Zoom;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct InputMessageSource
    {
        public int DeviceType, OriginId;
    }

    private const int ImdtTouchpad = 0x10;

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetCurrentInputMessageSource(out InputMessageSource source);

    /// <summary>Whether the input message being handled now came from a touchpad (Windows 8.1 and later say so; a mouse's never does).</summary>
    private static bool FromTouchpad() => GetCurrentInputMessageSource(out var source) && (source.DeviceType & ImdtTouchpad) != 0;

    // ----- Touch (5.3-Stage3) -----

    // DIPs two fingers move up for the spacing to grow by one (its whole range is under 0.6); the least pinch counted as one.
    private const double ExplodedTouchSpacingPerDip = 0.0015, ExplodedPinchFloor = 0.01;

    /// <summary>
    /// A touch on the 3D layer: on a plane, the selected plane's handle or an arrow of the gizmo, or the panel, it is the mouse's
    /// (cancelled here, WPF promotes it to the mouse: select and move, resize, the slider and buttons); on the empty space, the
    /// camera's (one finger orbits, two pinch and set the spacing).
    /// </summary>
    private void ExplodedLayer_ManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        if (!_explodedUi || _gpu is not { } engine || e.Manipulators.FirstOrDefault() is not { } finger)
        {
            e.Cancel();
            return;
        }

        var at = finger.GetPosition(ExplodedLayer);
        var onPanel = ExplodedPanel.IsVisible && new Rect(ExplodedPanel.TranslatePoint(default, ExplodedLayer), ExplodedPanel.RenderSize).Contains(at);
        if (onPanel || ExplodedAxisAt(at) >= 0 || ExplodedHandleHit(engine, at, out _, out _, out _) || PickExplodedPlane(engine, at, out _, out _, out _, out _))
        {
            e.Cancel();
            return;
        }

        e.ManipulationContainer = ExplodedLayer;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        e.Handled = true;
    }

    private void ExplodedLayer_ManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        if (!_explodedUi || _gpu is not { } engine || e.IsInertial)
        {
            e.Complete();
            return;
        }

        e.Handled = true;
        engine.SetOrbiting(true);
        var change = e.DeltaManipulation;
        var (yaw, pitch, zoom, spacing) = ExplodedTouchDoes(e.Manipulators.Count(), change.Translation, change.Scale);
        if (yaw != 0 || pitch != 0)
            engine.Orbit(yaw, pitch);
        if (zoom != 1)
            engine.Zoom(zoom);
        if (spacing != 0)
            ExplodedSpacing.Value = Math.Clamp(ExplodedSpacing.Value + spacing, ExplodedSpacing.Minimum, ExplodedSpacing.Maximum);
    }

    /// <summary>
    /// What a step of a touch on the 3D view's empty space does (pure, for exploded-3d-gestures): one finger orbits as the right button's
    /// drag does; two spread apart bring the camera in (the factor <see cref="GpuPreviewEngine.Zoom"/> takes, under 1), and dragged up
    /// or down with no more than a hair of a pinch part or close the planes. Degrees of turn and tip, the zoom factor, the spacing's change.
    /// </summary>
    internal static (double Yaw, double Pitch, double Zoom, double Spacing) ExplodedTouchDoes(int fingers, Vector moved, Vector scale)
    {
        if (fingers < 2)
            return (-moved.X * ExplodedOrbitPerDip, moved.Y * ExplodedOrbitPerDip, 1, 0);

        var pinch = (scale.X + scale.Y) / 2;
        if (!double.IsFinite(pinch) || pinch <= 0)
            pinch = 1;
        var zoom = Math.Abs(pinch - 1) > 1e-4 ? 1 / pinch : 1;
        var spacing = Math.Abs(pinch - 1) < ExplodedPinchFloor ? -moved.Y * ExplodedTouchSpacingPerDip : 0;
        return (0, 0, zoom, spacing);
    }

    private void ExplodedLayer_ManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
    {
        _gpu?.SetOrbiting(false);
        NoteStillStale();
        e.Handled = true;
    }

    /// <summary>
    /// Alt + the wheel, [ and ]: the next layer in the stack's order (towards the front, or towards the back) is selected, its plane
    /// and badge in the accent; from none, the front one (or the back one). Each clip once, however many planes it has.
    /// </summary>
    private void CycleExplodedSelection(GpuPreviewEngine engine, int step)
    {
        engine.TryGetExplodedScene(_explodedScene);
        var scene = _explodedScene;
        TakeExplodedInfos(scene);

        // The clips' planes from the back of the stack to the front (their place in it, then the order drawn); the Background Blur
        // once, at its first plane's place.
        var planes = new List<(float Depth, int Index, long Key, Layer View)>();
        for (var k = 0; k < scene.Count; k++)
        {
            ref readonly var plane = ref scene.Planes[k];
            var clipKey = plane.BlurFillOnly ? GpuScenePlan.BlurFillKey : plane.ClipKey;
            if (ExplodedViewOf(plane) is not { } view || planes.Exists(p => p.Key == clipKey))
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
        _viewModel.StatusText = $"Selected {chosen.Name} ({planes.Count - next} of {planes.Count} from the front): drag it, or Alt + the wheel ([ or ]) for the next.";
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
        NoteStillStale();
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
                _viewModel.HoldCropTarget(false);
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
        if (zoom != 0 || pan != 0)
            NoteStillStale();
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
    /// The layer under a point: the nearest plane (along the planes' normal, which is along the ray: the order they are drawn in) whose
    /// box the ray meets, a tilted one's box on its tilted plane (5.2C, the user's Q4); a blur fill's plane (behind its video's family,
    /// so only where nothing in front of it is pressed) is the Background Blur (5.2.0's re-cut). Its view and where the point is (canvas
    /// pixels at the depth <paramref name="depth"/>, the plane's own for a flat one).
    /// </summary>
    private bool PickExplodedPlane(GpuPreviewEngine engine, Point at, out ExplodedPlane picked, out Layer view, out Vector2 hit, out float depth)
    {
        (picked, view, hit, depth) = (default, null!, default, float.NaN);
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

            // Of planes as near (no spread yet), the later in the stack: it is drawn over the earlier. The selected
            // layer wins wherever its box is under the pointer (the Background Blur, the whole frame, only where nothing is in front).
            var z = camera.DepthOf(plane.ZDepth);
            var distance = camera.DistanceTo(z);
            var isSelected = selected != 0 && plane.ClipKey == selected && !plane.BlurFillOnly;
            if (!isSelected && distance > nearest || !OnExplodedBox(camera, point, plane, z, out var onPlane, out var onDepth)
                || ExplodedViewOf(plane) is not { } layer)
                continue;

            (nearest, found, picked, view, hit, depth) = (distance, true, plane, layer, onPlane, onDepth);
            if (isSelected)
                return true;
        }

        return found;
    }

    /// <summary>
    /// Whether the ray through an output point meets a plane's box: a flat plane's on its depth, a tilted one's on its tilted plane (5.2C);
    /// where (canvas pixels) and at what depth.
    /// </summary>
    internal static bool OnExplodedBox(in ExplodedCamera camera, Vector2 point, in ExplodedPlane plane, float z, out Vector2 hit, out float depth)
    {
        (hit, depth) = (default, z);
        if (!plane.Tilted)
        {
            return camera.HitPlane(point.X, point.Y, z, out hit)
                && ExplodedCamera.InBox(hit, plane.X, plane.Y, plane.Width, plane.Height, plane.Rotation, plane.AnchorX, plane.AnchorY);
        }

        if (!camera.HitBox(point.X, point.Y, plane.X, plane.Y, plane.Width, plane.Height, plane.Rotation, plane.AnchorX, plane.AnchorY, z,
                plane.TiltX, plane.TiltY, out var uv, out var world)
            || uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1)
            return false;
        (hit, depth) = (new Vector2(world.X, world.Y), world.Z);
        return true;
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
        var z = camera.DepthOf(plane.ZDepth);
        if (plane.Tilted)
        {
            // On its tilted plane (5.2C): the resize reads the pointer's place on the box itself (ResizeExplodedPlane).
            if (!camera.HitBox(point.X, point.Y, plane.X, plane.Y, plane.Width, plane.Height, plane.Rotation, plane.AnchorX, plane.AnchorY, z,
                    plane.TiltX, plane.TiltY, out var uv, out _))
                return false;
            (picked, view, hit) = (plane, layer, uv);
            return true;
        }

        if (!camera.HitPlane(point.X, point.Y, z, out var onPlane))
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

            // Its box's far corner where it is drawn: on its tilted plane for a tilted one (5.2C).
            var camera = scene.Camera;
            var far = ExplodedCamera.OnBox(candidate.X, candidate.Y, candidate.Width, candidate.Height, candidate.Rotation, candidate.AnchorX, candidate.AnchorY,
                camera.DepthOf(candidate.ZDepth), candidate.TiltX, candidate.TiltY, 1, 1);
            if (!camera.TryProject(far, out var on))
                return false;
            var (toX, toY) = (ExplodedLayer.ActualWidth / Math.Max(scene.OutputWidth, 1), ExplodedLayer.ActualHeight / Math.Max(scene.OutputHeight, 1));
            (plane, corner) = (candidate, new Point(on.X * toX, on.Y * toY));
            return true;
        }

        return false;
    }

    /// <summary>
    /// The selected plane's corner handle dragged: the layer grows by the pointer's move on its plane, turned into the box's
    /// own axes (a turned layer grows along its own sides), as the flat player's handle grows it (ResizeTrackLayer). A tilted
    /// plane's (5.2C) is read on its tilted plane as it was pressed: the move across and down the box itself.
    /// </summary>
    private void ResizeExplodedPlane(Point at)
    {
        if (_explodedView is not { } view)
            return;

        var camera = _explodedCamera;
        var point = ExplodedOutputPoint(at, camera.OutputWidth, camera.OutputHeight);
        var (frameWidth, frameHeight) = (Math.Max(_viewModel.FrameWidth, 1), Math.Max(_viewModel.FrameHeight, 1));
        if (_explodedPlane.Tilted)
        {
            var plane = _explodedPlane;
            if (!camera.HitBox(point.X, point.Y, plane.X, plane.Y, plane.Width, plane.Height, plane.Rotation, plane.AnchorX, plane.AnchorY, _explodedPlaneZ,
                    plane.TiltX, plane.TiltY, out var uv, out _))
                return;
            var across = (uv - _explodedHit) * new Vector2(plane.Width, plane.Height);
            _explodedHit = uv;
            if (across == Vector2.Zero || !float.IsFinite(across.X) || !float.IsFinite(across.Y))
                return;
            _explodedStep?.Changing();
            ResizeTrackLayer(view, across.X * (double)frameWidth / camera.CanvasWidth, across.Y * (double)frameHeight / camera.CanvasHeight);
            return;
        }

        if (!camera.HitPlane(point.X, point.Y, _explodedPlaneZ, out var onPlane))
            return;

        var moved = onPlane - _explodedHit;
        _explodedHit = onPlane;
        if (moved == Vector2.Zero || !float.IsFinite(moved.X) || !float.IsFinite(moved.Y))
            return;

        var turn = -_explodedPlane.Rotation * MathF.PI / 180;
        var (cos, sin) = (MathF.Cos(turn), MathF.Sin(turn));
        var local = new Vector2(moved.X * cos - moved.Y * sin, moved.X * sin + moved.Y * cos);
        _explodedStep?.Changing();
        ResizeTrackLayer(view, local.X * (double)frameWidth / camera.CanvasWidth, local.Y * (double)frameHeight / camera.CanvasHeight);
    }

    /// <summary>
    /// The clip view a plane is the picture of (from its clip's id), or the caption box for the captions' plane (5.1-Polish);
    /// null for one that is neither (the whole picture's look, the fades, the subtitle pictures).
    /// </summary>
    private Layer? ExplodedViewOf(in ExplodedPlane plane)
    {
        if (plane.ClipKey == GpuScenePlan.CaptionClipKey)
            return _viewModel.CaptionLayer;
        if (plane.BlurFillOnly)
            return _viewModel.BackgroundLayer;
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
        if (_explodedDrag == ExplodedDragKind.AlongX)
            moved.Y = 0;
        else if (_explodedDrag == ExplodedDragKind.AlongY)
            moved.X = 0;
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
        if (_explodedView is { IsCaptions: true } && Math.Abs(_explodedLift) >= 0.5)
        {
            MoveExplodedCaptions();
            return;
        }

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
    /// <summary>
    /// The captions' plane let go after a lift through the stack (5.1-Polish, the user's Q2): past another track's plane,
    /// the captions go over the farthest one passed (lifted forward) or under it (back), one undo step; short of any, nothing
    /// changes. They are drawn there by the preview and by every export.
    /// </summary>
    private void MoveExplodedCaptions()
    {
        var (from, to, forward) = (_explodedPlane.ZDepth, _explodedPlane.ZDepth + _explodedLift, _explodedLift > 0);
        if (_gpu is { } engine)
            engine.TryGetExplodedScene(_explodedScene);
        var scene = _explodedScene;
        TakeExplodedInfos(scene);

        Models.Timeline.TimelineTrackModel? target = null;
        string? targetName = null;
        var farthest = from;
        for (var k = 0; k < scene.Count; k++)
        {
            ref readonly var plane = ref scene.Planes[k];
            if (plane.ClipKey == GpuScenePlan.CaptionClipKey || plane.BlurFillOnly || ExplodedViewOf(plane) is not { IsCaptions: false } other
                || _viewModel.ClipOf(other) is not { } clip || _viewModel.Document.TrackOf(clip) is not { } track || _viewModel.CaptionPlaceOf(track) < 0)
                continue;

            var depth = plane.ZDepth;
            var passed = forward ? depth > from && depth < to && depth > farthest : depth < from && depth > to && depth < farthest;
            if (passed)
                (target, targetName, farthest) = (track, other.Name, depth);
        }

        if (target is null)
        {
            _viewModel.StatusText = "The captions stay where they are in the stack: drag them past another layer's plane to put them over or under it.";
            return;
        }

        var moved = forward
            ? _viewModel.PutCaptionsAt(_viewModel.CaptionPlaceOf(target) + 1, $"bring the captions in front of {targetName}")
            : _viewModel.PutCaptionsUnder(target, $"send the captions behind {targetName}");
        _viewModel.StatusText = moved
            ? $"The captions are {(forward ? "in front of" : "behind")} {targetName} now: the preview and every export draw them there. Ctrl+Z puts them back."
            : "The captions could not be moved in the stack.";
    }
}
