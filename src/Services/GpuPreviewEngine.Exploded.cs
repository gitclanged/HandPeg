using System.Diagnostics;
using HandPegApp.Compositor;
using HandPegApp.Models;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>Last-drawn 3D plane for picking and labels (canvas pixels, stack slot).</summary>
public struct ExplodedPlane
{
    public long ClipKey;

    /// <summary>Its index among the scene's layers (GpuScenePlan.LayerInfoAt's order).</summary>
    public int Index;

    public float X, Y, Width, Height, Rotation, AnchorX, AnchorY;

    /// <summary>Drawn stack slot (fractional while lifted, <see cref="GpuPreviewEngine.LiftLayer"/>).</summary>
    public float ZDepth;

    public int Flags;

    /// <summary>
    /// Plane pitch and yaw (degrees) about the anchor, used for picking/resizing (ExplodedCamera.HitBox); 0 for upright planes
    /// (SceneRenderer.TiltsInDepth; blur backgrounds stay upright).
    /// </summary>
    public float TiltX, TiltY;

    public readonly bool Tilted => TiltX != 0 || TiltY != 0;

    /// <summary>Blur-fill-only plane (full-frame blur under its video), picked as Background Blur.</summary>
    public readonly bool BlurFillOnly => (Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0;

    public readonly bool Adjustment => (Flags & GpuLayerDescriptor.FlagAdjustment) != 0;
}

/// <summary>
/// Last drawn 3D scene (camera and planes) via <see cref="GpuPreviewEngine.TryGetExplodedScene"/>. The UI thread reuses one instance;
/// refills are allocation-free.
/// </summary>
public sealed class ExplodedScene
{
    public ExplodedPlane[] Planes { get; } = new ExplodedPlane[CompositorProtocol.MaxLayers];
    public int Count { get; private set; }

    /// <summary>Raised with every scene drawn in 3D (0: none yet).</summary>
    public long Version { get; private set; }

    /// <summary>Source plan (<see cref="GpuScenePlan.LayerInfoAt"/> names planes) and evaluation time.</summary>
    public GpuScenePlan Plan { get; private set; } = GpuScenePlan.Empty;
    public double Seconds { get; private set; }

    public int CanvasWidth { get; private set; }
    public int CanvasHeight { get; private set; }
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    /// <summary>How many places the stack has (its plan's depth: a track a place, whatever is on now).</summary>
    public int LayerCount { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float Zoom { get; private set; }
    public float Spread { get; private set; }

    /// <summary>Camera used for rendering (same math as the worker, so picking matches the drawn planes).</summary>
    public ExplodedCamera Camera => ExplodedCamera.Create(CanvasWidth, CanvasHeight, OutputWidth, OutputHeight, Yaw, Pitch, Zoom, Spread, LayerCount);

    internal void Take(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, GpuScenePlan plan, long version)
    {
        (Plan, Seconds, Version) = (plan, header.PlayheadSeconds, version);
        (CanvasWidth, CanvasHeight, OutputWidth, OutputHeight) = (header.CanvasWidth, header.CanvasHeight, header.OutputWidth, header.OutputHeight);
        (LayerCount, Yaw, Pitch, Zoom, Spread) = (ExplodedCamera.StackDepthOf(header), header.CameraYaw, header.CameraPitch, header.CameraZoom, header.ExplodedZSpread);
        var count = Math.Min(Math.Clamp(header.ActiveLayerCount, 0, layers.Length), Planes.Length);
        for (var k = 0; k < count; k++)
        {
            ref readonly var layer = ref layers[k];
            var tilts = SceneRenderer.TiltsInDepth(layer);
            Planes[k] = new ExplodedPlane
            {
                ClipKey = layer.ClipKey, Index = k, Flags = layer.Flags, ZDepth = layer.ZDepth,
                X = layer.X, Y = layer.Y, Width = layer.Width, Height = layer.Height,
                Rotation = layer.Rotation, AnchorX = layer.AnchorX, AnchorY = layer.AnchorY,
                TiltX = tilts ? layer.TiltX : 0, TiltY = tilts ? layer.TiltY : 0,
            };
        }

        Count = count;
    }

    /// <summary>Holds nothing again (the view left): the next scene copied in is taken whatever its version.</summary>
    public void Reset() => (Version, Count, Plan) = (0, 0, GpuScenePlan.Empty);

    internal void CopyFrom(ExplodedScene other)
    {
        (Plan, Seconds, Version, Count) = (other.Plan, other.Seconds, other.Version, other.Count);
        (CanvasWidth, CanvasHeight, OutputWidth, OutputHeight) = (other.CanvasWidth, other.CanvasHeight, other.OutputWidth, other.OutputHeight);
        (LayerCount, Yaw, Pitch, Zoom, Spread) = (other.LayerCount, other.Yaw, other.Pitch, other.Zoom, other.Spread);
        Array.Copy(other.Planes, Planes, other.Count);
    }
}

// ----- 3D Exploded View camera -----
// User input (enable, orbit, distance, spacing) sets a target; the drawn camera follows on the pump thread. Enter, exit and Reset are
// fixed 240 ms CubicInOut transitions (EasingCurves.CubicInOut) toward the moving target, each step capped at one 60 Hz refresh so a
// cold first frame never jumps; transitions are interruptible from the current pose. Direct user moves (orbit, wheel, spacing) use an
// exponential chase. While moving or orbiting, the paused player redraws every refresh (CameraMoving). Flat and 3D coincide at yaw 0,
// pitch 0, zoom 1, no spread (ExplodedCamera). Each 3D scene is kept (ExplodedScene) for UI-thread picking and labels.
public sealed partial class GpuPreviewEngine
{
    /// <summary>Default pose: yaw/pitch (degrees), distance (x fitted) and spacing.</summary>
    public const double DefaultYaw = 30, DefaultPitch = 16, DefaultZoom = 1.85, DefaultSpacing = 0.16;

    /// <summary>How far apart the planes may be set, as fractions of the canvas's larger side.</summary>
    public const double MinSpacing = 0.02, MaxSpacing = 0.6;

    // Exponential chase time constant (s); 95 % converged after three.
    private const double CameraSettle = 0.085;

    /// <summary>Enter/exit/Reset transition duration (s).</summary>
    public const double CameraTransitionSeconds = 0.24;

    /// <summary>Max time advance per camera step (one 60 Hz refresh) so slow frames never cause jumps.</summary>
    public const double MaxCameraStep = 1 / 60.0;

    private readonly object _camera = new();

    // The target, set by the UI thread; the camera drawn, stepped by the pump (both under _camera).
    private bool _wantExploded;
    private double _yaw = DefaultYaw, _pitch = DefaultPitch, _zoom = DefaultZoom, _spacing = DefaultSpacing;
    private double _drawnYaw, _drawnPitch, _drawnZoom = 1, _drawnSpacing;
    private long _cameraTicks;

    // Active transition: starting pose and elapsed seconds (null when none).
    private CameraPose _transitionFrom;
    private double? _transitionClock;

    /// <summary>3D camera pose: yaw/pitch (degrees), distance (x fitted), plane spacing.</summary>
    internal readonly record struct CameraPose(double Yaw, double Pitch, double Zoom, double Spacing)
    {
        /// <summary>The flat picture: where the way in starts and the way out ends.</summary>
        public static CameraPose Flat => new(0, 0, 1, 0);

        /// <summary>This pose <paramref name="share"/> of the way to <paramref name="to"/>.</summary>
        public CameraPose Toward(CameraPose to, double share) =>
            new(Yaw + (to.Yaw - Yaw) * share, Pitch + (to.Pitch - Pitch) * share, Zoom + (to.Zoom - Zoom) * share, Spacing + (to.Spacing - Spacing) * share);

        /// <summary>Whether this pose is where <paramref name="to"/> is, near enough to land on it.</summary>
        public bool Near(CameraPose to) => Math.Abs(to.Yaw - Yaw) < 0.02 && Math.Abs(to.Pitch - Pitch) < 0.02 && Math.Abs(to.Zoom - Zoom) < 2e-4
            && Math.Abs(to.Spacing - Spacing) < 2e-5;
    }

    /// <summary>
    /// One camera step (pure; exploded-3d-transition-pacing golden) of <paramref name="elapsed"/> s clamped to <see cref="MaxCameraStep"/>.
    /// In a transition: CubicInOut from start to current target, landing exactly after <see cref="CameraTransitionSeconds"/> (clock reset to
    /// null); otherwise exponential chase, snapping when close.
    /// </summary>
    internal static CameraPose StepPose(CameraPose drawn, CameraPose target, double elapsed, CameraPose from, ref double? clock)
    {
        var step = double.IsFinite(elapsed) ? Math.Clamp(elapsed, 0, MaxCameraStep) : MaxCameraStep;
        if (clock is { } run)
        {
            run += step;
            if (run >= CameraTransitionSeconds)
            {
                clock = null;
                return target;
            }

            clock = run;
            return from.Toward(target, EasingCurves.CubicInOut(run / CameraTransitionSeconds));
        }

        var next = drawn.Toward(target, 1 - Math.Exp(-step / CameraSettle));
        return next.Near(target) ? target : next;
    }

    // The camera drawn and the target as poses (under _camera).
    private CameraPose Drawn
    {
        get => new(_drawnYaw, _drawnPitch, _drawnZoom, _drawnSpacing);
        set => (_drawnYaw, _drawnPitch, _drawnZoom, _drawnSpacing) = (value.Yaw, value.Pitch, value.Zoom, value.Spacing);
    }

    private CameraPose Target => _wantExploded ? new(_yaw, _pitch, _zoom, _spacing) : CameraPose.Flat;

    // A transition from where the camera is now (under _camera).
    private void StartTransition() => (_transitionFrom, _transitionClock) = (Drawn, 0);

    // Whether the scenes are 3D now (from the toggle on until the way out has settled): the only thing a flat scene reads.
    private volatile bool _drawingExploded;

    // Stopwatch time when scenes returned to flat; 0 after the follow-up flat redraw that lets the worker release 3D textures. Pump-owned.
    private long _flatSince;
    private volatile bool _orbiting;
    private long _selectedClipKey;
    private long _liftedKey;
    private double _liftedBy;

    // The last 3D scene drawn (written by the pump, copied out by the UI thread, under _camera).
    private readonly ExplodedScene _lastExploded = new();
    private long _explodedVersion;

    /// <summary>Whether the 3D exploded view is asked for (the toggle's state).</summary>
    public bool IsExploded
    {
        get
        {
            lock (_camera)
                return _wantExploded;
        }
    }

    /// <summary>True from enabling 3D until the exit transition has settled.</summary>
    public bool IsDrawingExploded => _drawingExploded;

    /// <summary>Enters or exits 3D view with an animated, interruptible transition.</summary>
    public void SetExploded(bool on)
    {
        lock (_camera)
        {
            if (_wantExploded == on)
                return;
            _wantExploded = on;
            if (on && !_drawingExploded)
            {
                // From flat: start at the 2D-equivalent pose and discard the previous 3D scene.
                Drawn = CameraPose.Flat;
                _cameraTicks = 0;
                _lastExploded.Reset();
                _drawingExploded = true;
            }

            // In or out, 240 ms from wherever the camera is now (the way in turned back halfway starts from there).
            StartTransition();
            if (!on)
                (_liftedKey, _liftedBy, _orbiting) = (0, 0, false);
        }

        Invalidate();
    }

    /// <summary>
    /// Target pose (stored in tab headers on deactivation): orbit, distance and spacing as last set, regardless of view state (never the flat exit pose).
    /// </summary>
    internal CameraPose TargetPose
    {
        get
        {
            lock (_camera)
                return new(_yaw, _pitch, _zoom, _spacing);
        }
    }

    /// <summary>
    /// Enters/exits 3D instantly without transition (tab deactivation/activation); the next scene uses the target pose. Uses
    /// <paramref name="pose"/> clamped to limits (default pose when null or NaN).
    /// </summary>
    internal void SetExplodedInstant(bool on, CameraPose? pose = null)
    {
        lock (_camera)
        {
            _wantExploded = on;
            _transitionClock = null;
            _cameraTicks = 0;
            (_liftedKey, _liftedBy, _orbiting) = (0, 0, false);
            if (on)
            {
                (_yaw, _pitch, _zoom, _spacing) = HeldToLimits(pose ?? new(DefaultYaw, DefaultPitch, DefaultZoom, DefaultSpacing));
                _lastExploded.Reset();
                _drawingExploded = true;
                Drawn = Target;
            }
            else
            {
                Drawn = CameraPose.Flat;
                if (_drawingExploded)
                    (_drawingExploded, _flatSince) = (false, Stopwatch.GetTimestamp());
            }
        }

        Invalidate();
    }

    /// <summary>Clamps a pose: angles short of edge-on, distance and spacing to range, NaN components to defaults.</summary>
    internal static CameraPose HeldToLimits(CameraPose pose)
    {
        static double Held(double value, double fallback, double low, double high) =>
            double.IsFinite(value) ? Math.Max(low, Math.Min(value, high)) : fallback;

        return new(Held(pose.Yaw, DefaultYaw, -ExplodedCamera.MaxTurnDegrees, ExplodedCamera.MaxTurnDegrees),
            Held(pose.Pitch, DefaultPitch, -ExplodedCamera.MaxTurnDegrees, ExplodedCamera.MaxTurnDegrees),
            Held(pose.Zoom, DefaultZoom, ExplodedCamera.MinZoom, ExplodedCamera.MaxZoom),
            Held(pose.Spacing, DefaultSpacing, MinSpacing, MaxSpacing));
    }

    /// <summary>Orbits by degrees (positive yaw right, positive pitch up), clamped short of edge-on.</summary>
    public void Orbit(double yawDegrees, double pitchDegrees)
    {
        lock (_camera)
        {
            _yaw = Math.Clamp(_yaw + yawDegrees, -ExplodedCamera.MaxTurnDegrees, ExplodedCamera.MaxTurnDegrees);
            _pitch = Math.Clamp(_pitch + pitchDegrees, -ExplodedCamera.MaxTurnDegrees, ExplodedCamera.MaxTurnDegrees);
        }

        Invalidate();
    }

    /// <summary>Moves the camera in (a factor under 1) or out (over 1).</summary>
    public void Zoom(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
            return;
        lock (_camera)
            _zoom = Math.Clamp(_zoom * factor, ExplodedCamera.MinZoom, ExplodedCamera.MaxZoom);
        Invalidate();
    }

    /// <summary>Plane spacing as a fraction of the canvas's larger side (<see cref="MinSpacing"/>..<see cref="MaxSpacing"/>).</summary>
    public double ZSpacing
    {
        get
        {
            lock (_camera)
                return _spacing;
        }
        set
        {
            if (!double.IsFinite(value))
                return;
            lock (_camera)
                _spacing = Math.Clamp(value, MinSpacing, MaxSpacing);
            Invalidate();
        }
    }

    /// <summary>Resets orbit and distance to defaults (spacing kept) with the 240 ms transition.</summary>
    public void ResetCamera()
    {
        lock (_camera)
        {
            (_yaw, _pitch, _zoom) = (DefaultYaw, DefaultPitch, DefaultZoom);
            if (_wantExploded && _drawingExploded)
                StartTransition();
        }

        Invalidate();
    }

    /// <summary>
    /// Sets orbit and distance from <paramref name="pose"/> (clamped; spacing unchanged), e.g. restoring the active tab's last 3D angle;
    /// animates over 240 ms when the view is on.
    /// </summary>
    internal void SetCamera(CameraPose pose)
    {
        lock (_camera)
        {
            var held = HeldToLimits(pose);
            (_yaw, _pitch, _zoom) = (held.Yaw, held.Pitch, held.Zoom);
            if (_wantExploded && _drawingExploded)
                StartTransition();
        }

        Invalidate();
    }

    /// <summary>True during an orbit drag; forces paused redraws every refresh even after the camera catches up.</summary>
    public void SetOrbiting(bool held)
    {
        _orbiting = held;
        Invalidate();
    }

    /// <summary>Selected clip key (<see cref="GpuHash.Of"/> its id; 0 none), outlined in 3D.</summary>
    public long SelectedClipKey
    {
        get => Interlocked.Read(ref _selectedClipKey);
        set
        {
            if (Interlocked.Exchange(ref _selectedClipKey, value) != value && _drawingExploded)
                Invalidate();
        }
    }

    /// <summary>
    /// Depth-drag preview: draws the clip's layer offset by a number of slots (positive = nearer) until the drag commits the track move;
    /// key 0 clears it.
    /// </summary>
    public void LiftLayer(long clipKey, double places)
    {
        lock (_camera)
            (_liftedKey, _liftedBy) = (clipKey, clipKey == 0 || !double.IsFinite(places) ? 0 : places);
        Invalidate();
    }

    /// <summary>
    /// Copies the last 3D scene into <paramref name="into"/> if newer (UI thread, for picking and labels); false otherwise.
    /// </summary>
    public bool TryGetExplodedScene(ExplodedScene into)
    {
        lock (_camera)
        {
            if (_lastExploded.Version == 0 || _lastExploded.Version == into.Version)
                return false;
            into.CopyFrom(_lastExploded);
            return true;
        }
    }

    partial void CameraMoving(ref bool moving)
    {
        if (!_drawingExploded)
        {
            // 0.5 s after leaving 3D, redraw flat so the worker releases 3D plane textures (hundreds of MB) even while paused.
            if (_flatSince != 0 && Stopwatch.GetElapsedTime(_flatSince).TotalSeconds > SceneRenderer.ExplodedTexturesPatience + 0.5)
            {
                _flatSince = 0;
                Invalidate();
            }

            return;
        }

        var (width, height) = _viewport.PixelSize;
        lock (_camera)
        {
            if (width < 8 || height < 8)
            {
                // No surface (hidden player): snap the camera to its target (including the flat pose).
                (Drawn, _transitionClock) = (Target, null);
                if (!_wantExploded)
                    (_drawingExploded, _flatSince) = (false, Stopwatch.GetTimestamp());
                return;
            }

            moving = _orbiting || !Settled();
        }

        // Hold background work while the camera moves so worker frames take priority.
        if (moving)
            WorkScheduler.HoldBackground(CameraHold);
    }

    /// <summary>How long background work is held after each frame of a moving camera.</summary>
    private static readonly TimeSpan CameraHold = TimeSpan.FromMilliseconds(350);

    partial void WriteCamera(ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers)
    {
        // The flat picture: nothing to do (a scene is cleared to ViewMode 0 before it is written).
        if (!_drawingExploded)
            return;

        lock (_camera)
        {
            StepCamera();
            if (!_wantExploded && Settled())
            {
                // Back where the flat picture is: this scene and those after it are flat.
                (_drawingExploded, _flatSince) = (false, Stopwatch.GetTimestamp());
                header.ViewMode = 0;
                return;
            }

            var plan = Volatile.Read(ref _plan);
            WriteStack(ref header, layers, Drawn, plan);
            _lastExploded.Take(header, layers, plan, ++_explodedVersion);
        }
    }

    /// <summary>
    /// Writes a 3D scene of <paramref name="plan"/> at fixed <paramref name="pose"/> without stepping or keeping it (tab still capture,
    /// GpuPreviewEngine.CaptureThumbnail; pump thread).
    /// </summary>
    private void WriteCameraAt(ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers, CameraPose pose, GpuScenePlan plan)
    {
        lock (_camera)
            WriteStack(ref header, layers, pose, plan);
    }

    /// <summary>Writes the 3D camera and per-layer slot, outline and lift into a scene (under _camera).</summary>
    private void WriteStack(ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers, CameraPose pose, GpuScenePlan plan)
    {
        header.ViewMode = 1;
        header.Flags |= FrameSceneHeader.FlagDepthRails;
        header.CameraYaw = (float)pose.Yaw;
        header.CameraPitch = (float)pose.Pitch;
        header.CameraZoom = (float)pose.Zoom;
        header.ExplodedZSpread = (float)(pose.Spacing * Math.Max(Math.Max(header.CanvasWidth, header.CanvasHeight), 1));

        // Each layer at its track's slot in the full plan depth (stable when layers come and go). Selected clip outlined; other clips on
        // its track outlined faintly so the plane never blinks at cuts; depth-dragged layer drawn at its drag offset.
        var count = Math.Clamp(header.ActiveLayerCount, 0, layers.Length);
        header.ExplodedDepth = plan.PlaceLayers(layers, count);
        var selected = Interlocked.Read(ref _selectedClipKey);
        var selectedPlace = selected != 0 ? plan.PlaceOf(selected) : -1;
        for (var k = 0; k < count; k++)
        {
            ref var layer = ref layers[k];
            if ((layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0)
            {
                // Blur-fill plane: outlined while Background Blur is selected.
                if (selected == GpuScenePlan.BlurFillKey)
                    layer.Flags |= GpuLayerDescriptor.FlagSelected;
            }
            else if (selected != 0)
            {
                if (layer.ClipKey == selected)
                    layer.Flags |= GpuLayerDescriptor.FlagSelected;
                else if (selectedPlace >= 0 && layer.ZDepth == selectedPlace)
                    layer.Flags |= GpuLayerDescriptor.FlagSelectedTrack;
            }

            if (_liftedKey != 0 && layer.ClipKey == _liftedKey && (layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) == 0)
                layer.ZDepth += (float)_liftedBy;
        }
    }

    /// <summary>
    /// Camera for an as-displayed still: the target pose while 3D is requested (transitions rendered at their end); null when flat.
    /// </summary>
    private CameraPose? ShownPose()
    {
        lock (_camera)
            return _wantExploded ? new CameraPose(_yaw, _pitch, _zoom, _spacing) : null;
    }

    /// <summary>
    /// Advances the drawn camera one frame (under _camera) by elapsed time capped at one 60 Hz refresh (first step after rest counts as one
    /// refresh, so moves never jump). Lands exactly on target so exit ends on the flat pose.
    /// </summary>
    private void StepCamera()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _cameraTicks == 0 ? MaxCameraStep : (now - _cameraTicks) / (double)Stopwatch.Frequency;
        _cameraTicks = now;
        Drawn = StepPose(Drawn, Target, elapsed, _transitionFrom, ref _transitionClock);
    }

    /// <summary>Whether the drawn camera has reached its target, no transition under way (under _camera).</summary>
    private bool Settled() => _transitionClock is null && Drawn.Near(Target);
}
