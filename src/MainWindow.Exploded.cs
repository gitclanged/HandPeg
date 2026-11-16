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

// ----- 3D Exploded View -----
// The player as the GPU engine's 3D layer stack (GpuPreviewEngine.Exploded.cs, rendered by SceneRenderer.Exploded.cs): one plane per
// layer in compositing order, camera orbiting the canvas. This partial owns the toggle (playback bar, "view.exploded3d", Alt+middle-drag),
// input, Z-Spacing panel and label badges, all hosted in ExplodedLayer (last child of PlayerOverlay; transparent and hit-test-free while flat).
// - Picking: ray vs planes from the last drawn scene (ExplodedCamera); the selected layer wins anywhere inside its box (even occluded),
//   else the nearest hit. A press selects, like the flat player.
// - Left button only selects/moves: plane drag = MoveTrackLayer (one Undo step, DragStep); corner handle = ResizeTrackLayer in box axes.
// - Shift+drag lifts a plane through the stack (GpuPreviewEngine.LiftLayer preview); releasing past another track reorders tracks
//   (MoveLayerUp/Down, one EditTogether step) and relays the Timeline immediately. Tracks keep stable stack slots
//   (GpuScenePlan.PlaceLayers). The captions plane moves/resizes like its box and lifts over/under tracks (MoveExplodedCaptions:
//   CaptionsUnder), honoured by preview and export.
// - Right/middle drag orbits; wheel dollies; Alt+wheel or [ ] steps selection front/back; Shift+wheel sets spacing; double-click on
//   empty space resets the camera; Esc, panel close or the toggle exits.
// - Touchpad: pinch (Ctrl+wheel) dollies, two-finger scroll orbits (detected via GetCurrentInputMessageSource). Touch screen: one
//   finger orbits, two pinch or drag vertically for spacing (ExplodedLayer manipulation); touches on planes, handles or the panel are
//   promoted to mouse input.
public partial class MainWindow
{
    private enum ExplodedDragKind { None, Orbit, Move, Depth, Resize, AlongX, AlongY }

    // Orbit degrees per DIP; per-notch dolly factor; DIPs per stack slot when depth is not visible (head-on view); WASD orbit deg/s.
    private const double ExplodedOrbitPerDip = 0.3, ExplodedWheelStep = 0.88, ExplodedDepthFallback = 36, ExplodedNavOrbitRate = 70;

    // Requested 3D state; whether the layer and per-frame tick are active (until the exit transition settles); input hooked once.
    private bool _explodedUi, _explodedTicking, _explodedHooked, _syncingExplodedToggle;

    // Last drawn 3D scene (for picking and badges), its layer info, and the plan/time/keys that info was built for.
    private readonly ExplodedScene _explodedScene = new();
    private readonly GpuLayerInfo[] _explodedInfos = new GpuLayerInfo[CompositorProtocol.MaxLayers];
    private readonly long[] _explodedInfoKeys = new long[CompositorProtocol.MaxLayers];
    private int _explodedInfoCount;
    private GpuScenePlan? _explodedInfoPlan;

    // Pooled label badges (positioned by RenderTransform), flat overlays hidden during 3D (with saved opacity), and the player zoom
    // matrix the 3D layer last counter-scaled.
    private readonly List<(Border Badge, TextBlock Text, TranslateTransform Place)> _explodedBadges = [];
    private readonly List<Rect> _explodedBadgeBoxes = [];
    private readonly List<(UIElement Element, double Opacity)> _explodedHidden = [];
    private Matrix _explodedUnder = Matrix.Identity;

    // Active drag: kind, button, press and last points; for a plane also its layer, press-time camera, plane-local point, depth, lift.
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

    /// <summary>Keybind CanExecute: entering needs the GPU engine and open media; exiting is always allowed.</summary>
    private bool CanToggleExplodedView => _explodedUi || _gpu is not null && _viewModel.HasSource;

    /// <summary>"view.exploded3d" keybind and toggle: switches between 3D and flat.</summary>
    private void ToggleExplodedView() => SetExplodedView(!_explodedUi);

    // ----- Enter and exit -----

    // Exit timestamp and the max wait for a flat frame (240 ms camera transition at <= 60 Hz steps: 15 frames, slow ones included).
    private long _explodedLeftAt;

    // Active tab's last 3D camera: saved on exit, restored on manual re-entry in the same tab, persisted in the tab header across
    // switches (null = never in 3D: default start pose). Reset restores the default.
    private GpuPreviewEngine.CameraPose? _explodedLastPose;
    private const long ExplodedWayBackMs = 1500;

    /// <summary>
    /// Enters 3D (camera animates out of the flat view) or exits (animates back; flat overlays return once flat). GPU engine with media only.
    /// <para>Tab switches (MainWindow.ProjectTabs.cs) pass <paramref name="instant"/>: no transition or status line, entering at
    /// <paramref name="pose"/> (the tab's camera; spacing from the panel). Manual entry uses the remembered or default pose.</para>
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

            // Player zoom does not apply in 3D: start from the fitted picture, like the camera.
            (_playerZoom, _playerPanX, _playerPanY) = (0, 0, 0);
            if (_playerView is { } view)
                view.Matrix = Matrix.Identity;
            engine.SetView(0, 0, 0);

            _explodedUi = true;
            NoteStillStale();
            engine.SelectedClipKey = SelectedExplodedKey();
            if (instant)
            {
                // Tab activation: snap to its saved camera (spacing from the panel).
                var at = GpuPreviewEngine.HeldToLimits(pose ?? new(GpuPreviewEngine.DefaultYaw, GpuPreviewEngine.DefaultPitch, GpuPreviewEngine.DefaultZoom, ExplodedSpacing.Value));
                ExplodedSpacing.Value = Math.Max(ExplodedSpacing.Minimum, Math.Min(at.Spacing, ExplodedSpacing.Maximum));
                engine.SetExplodedInstant(true, at with { Spacing = ExplodedSpacing.Value });
                ShowExplodedLayer();
                SyncExplodedToggle();
                return;
            }

            // Remembered camera for this tab, else the default; spacing comes from the panel (session-wide).
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
                // Tab deactivated or closed: go flat immediately and remove layer, panel and badges.
                _gpu?.SetExplodedInstant(false);
                HideExplodedLayer();
                SyncExplodedToggle();
                return;
            }

            _gpu?.SetExploded(false);

            // Hide panel and badges now; the layer keeps absorbing presses until flat (OnExplodedFrame) so nothing beneath is
            // clicked during the transition.
            ExplodedPanel.Visibility = Visibility.Collapsed;
            ExplodedBadges.Visibility = Visibility.Collapsed;
            if (_gpu is null)
                HideExplodedLayer();
        }

        SyncExplodedToggle();
    }

    /// <summary>
    /// Immediate flat exit for tab deactivation/close (also cuts short an exit transition), so the next tab never renders in 3D and its
    /// overlays never wait hidden.
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
        // Flat overlays (boxes, crop, margins) are positioned for the flat picture: hide them in 3D.
        if (_explodedHidden.Count == 0)
        {
            foreach (UIElement child in PlayerOverlay.Children)
            {
                // Never hide a tab still: it stands in for the view (flat or 3D) until the engine draws the tab.
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

        // Clear stale scene data: badges and marks wait for the next 3D scene.
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

    /// <summary>Engine key of the selected clip (or the captions key when captions are selected); 0 for none.</summary>
    private long SelectedExplodedKey() => _selectedClip is Layer { IsCaptions: true } ? GpuScenePlan.CaptionClipKey
        : _selectedClip is Layer { IsBackground: true } ? GpuScenePlan.BlurFillKey
        : _selectedClip is Layer { ClipId: var id } && id != Guid.Empty ? GpuHash.Of(id) : 0;

    /// <summary>Display name of a Background Blur plane (badge and status line).</summary>
    private const string ExplodedBlurName = "Background Blur";

    // ----- Per-frame update while 3D is active -----

    private void OnExplodedFrame(object? sender, EventArgs e)
    {
        var engine = _gpu;

        // Engine stopped/faulted or media closed: end the view.
        if (_explodedUi && (engine is null || !_viewModel.HasSource))
        {
            SetExplodedView(false);
            engine = _gpu;
        }

        // Paused, the engine may present no frame: end the exit transition on timeout anyway, or the flat boxes beneath stay unclickable.
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
    /// Counter-scales the 3D layer against the player zoom (W/S on the flat player) so its coordinates stay in window space (pointer, badges).
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

    // ----- Layer names and badges -----

    /// <summary>Per-layer info for the scene (GpuScenePlan.LayerInfoAt), recomputed only when the plan or its layers change.</summary>
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
            // Non-fatal: planes stay unnamed (and unpickable) until the next scene.
            AppLog.Write("3D Exploded View: the layers could not be described", ex);
            _explodedInfoCount = 0;
        }

        _explodedInfoPlan = scene.Plan;
        for (var k = 0; k < scene.Count; k++)
            _explodedInfoKeys[k] = scene.Planes[k].ClipKey;
        _explodedInfoCount = Math.Min(_explodedInfoCount, scene.Count);
    }

    /// <summary>Info for a plane: its own entry (scene order), else its clip's first entry; null if none.</summary>
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
    /// Places a badge at each named plane's projected top-left (masked = cyan, selected = amber). Per frame only RenderTransform and
    /// Opacity change (render-only; Measure/Arrange only on name/weight/visibility change); handle and gizmo redraw into ExplodedMarks.
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

        // Place nearest-first so a farther plane's overlapping badge yields (e.g. Background Blur sharing a frame corner with a top
        // Cutout): shifted above, or below when there is no room above.
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

            // Selected badge is accent-filled (as is its plane box); other clips of its track playing at this time get an accent edge.
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

        // Resize grip at the selected plane's far corner, plus gizmo arrows.
        var marks = _explodedMarks ??= MakeExplodedMarks();
        using var drawing = marks.Open();
        drawing.PushOpacity(camera.Amount);
        if (_gpu is { } engine && SelectedExplodedHandle(engine, out _, out var grip) && grip.X >= 0 && grip.Y >= 0 && grip.X <= width && grip.Y <= height)
        {
            // 10 DIP square with a 1 DIP inner stroke.
            const double inner = ExplodedHandleSide - 1;
            drawing.DrawRoundedRectangle(ExplodedSelectedEdge, ExplodedHandlePen, new Rect(grip.X - inner / 2, grip.Y - inner / 2, inner, inner), 2, 2);
        }

        PlaceExplodedGizmo(camera, toX, toY, drawing);
        drawing.Pop();
    }

    // ----- 3-axis gizmo (a tool: shown regardless of Extra Animations) -----

    // X red, Y (down the picture) green, Z (through the stack) blue; vector-drawn on the selected plane. Dragging an arrow constrains to its axis.
    private readonly Point[] _gizmoTips = new Point[3];
    private Point _gizmoMiddle;
    private bool _gizmoShown;

    /// <summary>Arrow length as a fraction of the canvas short side; press hit radius in DIPs.</summary>
    private const float GizmoLength = 0.14f;
    private const double GizmoReach = 8;

    private static readonly SolidColorBrush[] GizmoColors = [FrozenBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)), FrozenBrush(Color.FromRgb(0x45, 0xE0, 0x7A)), FrozenBrush(Color.FromRgb(0x4D, 0x9D, 0xFF))];
    private static readonly Pen[] GizmoPens = [.. GizmoColors.Select(color => FrozenPen(color, 2))];

    /// <summary>Draws the arrows at the selected plane's centre (from the last scene) into the marks drawing.</summary>
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

    /// <summary>Nearest arrow within reach of a point (0 = X, 1 = Y, 2 = Z); -1 for none.</summary>
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

    // Corner handle size and stroke.
    private const double ExplodedHandleSide = 10;
    private static readonly Pen ExplodedHandlePen = FrozenPen(ExplodedSelectedText, 1);

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Single element above the badges holding handle and gizmo as one drawing refilled each frame (<see cref="Open"/>): render-only, no
    /// layout pass (replacing per-shape Canvas children that were measured and arranged every frame).
    /// </summary>
    private sealed class ExplodedMarks : FrameworkElement
    {
        private readonly DrawingGroup _drawing = new();

        public ExplodedMarks() => IsHitTestVisible = false;

        /// <summary>Opens a new drawing context; its content replaces the previous drawing when closed.</summary>
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

    /// <summary>Hooks 3D input once (when the overlay window first shows or 3D first activates).</summary>
    private void HookExplodedInput()
    {
        if (_explodedHooked)
            return;
        _explodedHooked = true;

        // Alt+middle on the flat player: caught on the native video HWND where uncovered, else on the overlay window.
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

        // Runs after the window's own key handling (an open dial or tour consumes Esc first).
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(ExplodedWindow_PreviewKeyDown));
    }

    private const int WmMiddleButtonDown = 0x0207;

    // Native video HWND hook: its input bypasses WPF, so Alt+middle and (in 3D) wheel messages are read from its message stream.
    private IntPtr ExplodedSurfaceMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmMouseWheel or WmMouseHWheel)
            return ExplodedOverlayMessage(hwnd, msg, wParam, lParam, ref handled);
        if (msg != WmMiddleButtonDown || _explodedUi || _gpu is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return IntPtr.Zero;

        // Convert HWND pixels to overlay DIPs (the overlay exactly covers the HWND).
        var dpi = VisualTreeHelper.GetDpi(VideoView);
        var (x, y) = ((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
        handled = true;
        RunGuarded("3D view", () => BeginExplodedFromPress(new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY), MouseButton.Middle));
        return IntPtr.Zero;
    }

    // Alt+middle on a WPF overlay element over the flat video (layer box, crop).
    private void ExplodedOverlay_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_explodedUi || e.ChangedButton != MouseButton.Middle || _gpu is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return;

        e.Handled = true;
        var at = e.GetPosition(ExplodedLayer);
        RunGuarded("3D view", () => BeginExplodedFromPress(at, MouseButton.Middle));
    }

    /// <summary>Alt+middle on the flat player: enters 3D and continues the press as an orbit drag.</summary>
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

        // Unmodified [ / ]: step selection back/forward in the stack (as Alt+wheel). 3D-local keys like Esc, unless the key map
        // binds them elsewhere.
        if (e.Key != Key.Escape)
        {
            if (Keyboard.Modifiers != ModifierKeys.None || _gpu is not { } engine)
                return;
            if (_explodedDrag == ExplodedDragKind.None)
                RunGuarded("3D view", () => CycleExplodedSelection(engine, e.Key == Key.OemCloseBrackets ? 1 : -1));
            e.Handled = true;
            return;
        }

        // Cancel an active drag first (a lifted plane reverts), then exit the view.
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

        // During the exit transition the layer still absorbs presses; only Alt+middle acts (re-enter 3D).
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
            // Right or middle button orbits from anywhere.
            if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
            {
                BeginExplodedDrag(ExplodedDragKind.Orbit, at, e.ChangedButton);
                return;
            }

            // Corner handle: resize (not while a timeline drag is active).
            if (!TimelineDragHeld && e.ClickCount == 1 && ExplodedHandleHit(engine, at, out var handled, out var handledView, out var handledHit))
            {
                BeginExplodedPlaneDrag(handled, handledView, handledHit, ExplodedDragKind.Resize, $"resize {handledView.Name}", at);
                return;
            }

            // Gizmo arrow: axis-constrained move.
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
                // Empty space: double-click resets the camera; otherwise nothing (left button never orbits).
                if (e.ClickCount == 2)
                    engine.ResetCamera();
                return;
            }

            // Press selects the layer (as on the flat player) and starts its drag, unless a timeline drag is active.
            if (!ReferenceEquals(_selectedClip, view))
                SelectClip(view);

            // Background Blur: selectable (row and inspector) but never dragged; it always covers the frame.
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
    /// Starts a plane drag: records layer, press-time camera and the plane-local hit point at <paramref name="depth"/> (tilted planes;
    /// NaN = the plane's own depth), and opens one Undo step.
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
            // No overlay window to capture the drag: treat as a plain press.
            _explodedDrag = ExplodedDragKind.None;
            return;
        }

        // Orbits render every refresh while held; a plane drag pins the Crop boxes' clip (as flat box drags do) so it keeps moving
        // the pressed clip.
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
                    // Object-relative orbit: drag right turns the stack's front right (camera left); drag down tips its top
                    // forward (camera up).
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

    // WPF wheel over the 3D layer (normally ExplodedOverlayMessage handles it first at the message level).
    private void ExplodedLayer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ExplodedWheel(e.Delta, horizontal: false, Keyboard.Modifiers, FromTouchpad()))
            e.Handled = true;
    }

    private const int WmMouseWheel = 0x020A, WmMouseHWheel = 0x020E, MkShift = 0x0004, MkControl = 0x0008;

    /// <summary>
    /// Message-level wheel handler for the overlay and native video windows: reads modifiers from the message (touchpad pinch arrives as
    /// Ctrl+wheel) and handles WM_MOUSEHWHEEL (touchpad sideways scroll, tilt wheel), which WPF does not surface.
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

            // Return TRUE for WM_MOUSEHWHEEL, or Windows re-sends it as legacy tilt-wheel clicks.
            return horizontal ? new IntPtr(1) : IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // Orbit degrees per 120-unit touchpad/tilt delta; spacing change per Shift+wheel notch; Alt+wheel accumulator toward the next layer step.
    private const double ExplodedScrollOrbit = 6, ExplodedSpacingPerNotch = 0.02;
    private double _explodedCycleRest;

    /// <summary>
    /// Applies a 3D wheel event (<paramref name="delta"/> in 120 units per notch) per <see cref="ExplodedWheelDoes"/>: Ctrl/pinch dolly, Alt
    /// steps selection one notch at a time, Shift spacing, touchpad scroll or horizontal wheel orbits, plain mouse wheel dollies.
    /// Returns false when 3D is not active.
    /// </summary>
    private bool ExplodedWheel(int delta, bool horizontal, ModifierKeys keys, bool fromTouchpad)
    {
        if (!_explodedUi || _gpu is not { } engine)
            return false;

        var notches = delta / 120.0;
        switch (ExplodedWheelDoes(horizontal, keys, fromTouchpad))
        {
            case ExplodedWheelAction.Zoom:
                // Positive delta (pinch out) dollies in.
                engine.Zoom(Math.Pow(ExplodedWheelStep, notches));
                break;
            case ExplodedWheelAction.Layers when _explodedDrag == ExplodedDragKind.None:
                // Up = next layer toward the front, down = toward the back; small touchpad deltas accumulate to a full notch.
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
                // Fingers left = scroll right: turns the stack like a leftward drag.
                engine.Orbit(notches * ExplodedScrollOrbit, 0);
                break;
            case ExplodedWheelAction.Tip:
                // Two fingers down (natural scrolling: positive wheel) tip the top forward, like a downward drag.
                engine.Orbit(0, notches * ExplodedScrollOrbit);
                break;
        }

        NoteStillStale();
        return true;
    }

    /// <summary>3D wheel action kinds.</summary>
    internal enum ExplodedWheelAction { None, Zoom, Layers, Spacing, Turn, Tip }

    /// <summary>
    /// Pure (golden exploded-3d-gestures): Ctrl (mouse or touchpad pinch) zooms; Alt steps selection; Shift sets spacing; horizontal
    /// (touchpad, tilt) turns; touchpad vertical scroll tips; plain mouse wheel zooms. Horizontal with any modifier does nothing.
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

    /// <summary>True when the current input message came from a touchpad (GetCurrentInputMessageSource, Windows 8.1+).</summary>
    internal static bool FromTouchpad() => GetCurrentInputMessageSource(out var source) && (source.DeviceType & ImdtTouchpad) != 0;

    // ----- Touch -----

    // DIPs of two-finger vertical travel per +1 spacing (full range < 0.6); minimum scale change counted as a pinch.
    private const double ExplodedTouchSpacingPerDip = 0.0015, ExplodedPinchFloor = 0.01;

    /// <summary>
    /// Touch on the 3D layer: on a plane, handle, gizmo arrow or the panel the manipulation is cancelled so WPF promotes it to mouse input;
    /// on empty space it drives the camera (one finger orbits; two pinch and set spacing).
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
    /// Pure (golden exploded-3d-gestures): maps a touch manipulation delta to (turn deg, tip deg, zoom factor for
    /// <see cref="GpuPreviewEngine.Zoom"/> (&lt; 1 = in), spacing delta). One finger orbits like a right-drag; two-finger spread zooms in;
    /// two-finger vertical drag with negligible pinch changes spacing.
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
    /// Alt+wheel, [ / ]: selects the next clip in stack order toward the front or back (from none: the frontmost or backmost), highlighting
    /// its plane and badge. Each clip counts once regardless of plane count.
    /// </summary>
    private void CycleExplodedSelection(GpuPreviewEngine engine, int step)
    {
        engine.TryGetExplodedScene(_explodedScene);
        var scene = _explodedScene;
        TakeExplodedInfos(scene);

        // Clips ordered back to front (stack slot, then draw order); Background Blur once, at its first plane's slot.
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

    /// <summary>Ends a drag: a lifted plane commits its track reorder (unless cancelled) and the lift preview resets.</summary>
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

    /// <summary>WASD in 3D: W/S dolly, A/D orbit; returns true when handled (MainWindow.Keybinds.cs NavigationFrame).</summary>
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

    /// <summary>Converts an overlay point (DIPs) to worker output pixels.</summary>
    private Vector2 ExplodedOutputPoint(Point at, int outputWidth, int outputHeight)
    {
        var (width, height) = (Math.Max(ExplodedLayer.ActualWidth, 1), Math.Max(ExplodedLayer.ActualHeight, 1));
        return new Vector2((float)(at.X * outputWidth / width), (float)(at.Y * outputHeight / height));
    }

    /// <summary>
    /// Pick: the nearest plane (draw order along the ray) whose box the ray hits, tilted planes tested on their tilted plane. A blur fill
    /// plane (behind its video family, so only hit where nothing nearer is) resolves to Background Blur. Returns the view and the hit in
    /// canvas pixels at <paramref name="depth"/> (the plane's own for flat planes).
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

            // Tie at equal depth (zero spacing): the later plane wins (drawn on top). The selected layer wins anywhere in its box
            // (except Background Blur, which spans the frame and wins only where nothing nearer is hit).
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
    /// Ray-box test for an output point against a plane (flat at its depth, tilted on its tilted plane); returns the hit in canvas pixels
    /// and its depth.
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

    /// <summary>Corner handle hit radius in DIPs.</summary>
    private const double ExplodedHandleReach = 9;

    /// <summary>Corner-handle hit test: returns the selected plane, its layer and the plane-local point.</summary>
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
            // Tilted plane: the resize reads the pointer in the box's own plane (ResizeExplodedPlane).
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
    /// Projects the selected plane's far (bottom-right) corner into overlay coordinates from the last scene; false if nothing selected is drawn.
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

            // Far corner as drawn (on the tilted plane when tilted).
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
    /// Corner-handle drag: resizes by the pointer's plane-space delta rotated into box axes (rotated layers grow along their own sides),
    /// via ResizeTrackLayer as on the flat player. Tilted planes measure the delta on the press-time tilted plane.
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
    /// Clip view for a plane (by clip id), or the caption box for the captions plane; null for other planes (global look, fades,
    /// subtitle images).
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

    /// <summary>In-plane drag: moves the layer by the hit point's plane-space delta.</summary>
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

        // Canvas to frame pixels (identity unless the engine substituted a canvas).
        var (frameWidth, frameHeight) = (Math.Max(_viewModel.FrameWidth, 1), Math.Max(_viewModel.FrameHeight, 1));
        var (toX, toY) = ((double)frameWidth / camera.CanvasWidth, (double)frameHeight / camera.CanvasHeight);

        // On-screen scale of the frame at that depth: snap guide reach is a few screen pixels.
        var left = camera.Project(0, camera.CanvasHeight / 2f, _explodedPlaneZ);
        var right = camera.Project(camera.CanvasWidth, camera.CanvasHeight / 2f, _explodedPlaneZ);
        var across = float.IsFinite(left.X) && float.IsFinite(right.X) ? Vector2.Distance(left, right) * ExplodedLayer.ActualWidth / Math.Max(camera.OutputWidth, 1) : ExplodedLayer.ActualWidth;
        var frame = new Rect(0, 0, Math.Max(across, 1), Math.Max(across * frameHeight / frameWidth, 1));

        _explodedStep?.Changing();
        MoveTrackLayer(view, moved.X * toX, moved.Y * toY, frame, null);
    }

    /// <summary>
    /// Shift-drag lift: projects pointer movement onto the stack's depth axis at the plane (one slot per spacing; head-on, one slot per
    /// <see cref="ExplodedDepthFallback"/> DIPs upward).
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
    /// Commits a lift: if past another track's plane, moves the track past the farthest one passed (stepwise Layers reorder) as one Undo
    /// step; otherwise no change.
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

        // Farthest other-track plane passed.
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

        // Rebuild Timeline rows synchronously (not the queued layout) so the new order shows at once, including a child crossing its parent.
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
    /// Commits a captions-plane lift: past another track's plane, captions go over (lifted forward) or under (back) the farthest one
    /// passed, as one Undo step; otherwise no change. Preview and export honour the placement.
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
