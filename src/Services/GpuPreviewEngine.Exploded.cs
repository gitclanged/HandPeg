using System.Diagnostics;
using HandPegApp.Compositor;
using HandPegApp.Models;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>A layer as the 3D exploded view last drew it: what picking and the label badges read (canvas pixels, its place in the stack).</summary>
public struct ExplodedPlane
{
    public long ClipKey;

    /// <summary>Its index among the scene's layers (GpuScenePlan.LayerInfoAt's order).</summary>
    public int Index;

    public float X, Y, Width, Height, Rotation, AnchorX, AnchorY;

    /// <summary>Its place in the stack as drawn (a fraction while it is lifted along the stack, <see cref="GpuPreviewEngine.LiftLayer"/>).</summary>
    public float ZDepth;

    public int Flags;

    /// <summary>
    /// Its Pitch and Yaw (degrees) as the 3D view turns its plane out of the stack about its anchor (5.2C, the user's Q4: picked and resized
    /// on that plane, ExplodedCamera.HitBox); 0 for a plane drawn upright (SceneRenderer.TiltsInDepth: a blurred background keeps it so).
    /// </summary>
    public float TiltX, TiltY;

    public readonly bool Tilted => TiltX != 0 || TiltY != 0;

    /// <summary>Only a blurred background (a whole-frame blur under its video's family): a plane of its own, picked as the Background Blur.</summary>
    public readonly bool BlurFillOnly => (Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0;

    public readonly bool Adjustment => (Flags & GpuLayerDescriptor.FlagAdjustment) != 0;
}

/// <summary>
/// The 3D exploded scene the player last drew: its camera and its planes (<see cref="GpuPreviewEngine.TryGetExplodedScene"/>).
/// The UI thread keeps one and has it filled again when a newer one was drawn; nothing is allocated after it is made.
/// </summary>
public sealed class ExplodedScene
{
    public ExplodedPlane[] Planes { get; } = new ExplodedPlane[CompositorProtocol.MaxLayers];
    public int Count { get; private set; }

    /// <summary>Raised with every scene drawn in 3D (0: none yet).</summary>
    public long Version { get; private set; }

    /// <summary>The plan the scene was evaluated from (its <see cref="GpuScenePlan.LayerInfoAt"/> names the planes), and the moment.</summary>
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

    /// <summary>The camera it was drawn with: the worker's own maths, so a point picked here is the plane drawn there.</summary>
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

// ----- The 3D exploded layer stack's camera (Pass 5.0-AudioAnd3D, 5.0D) -----
// What the user asks for (on, the turn, the distance, the spacing) is the camera's target; the camera drawn follows it on
// the pump's thread, frame by frame. The way in, the way out and Reset are a transition of a fixed length (5.3-Stage3, the
// user's bug: on the Snapdragon the way in leapt on its first frame and crawled through its second half): 240 ms along a
// cubic ease in and out (EasingCurves.CubicInOut) from where the camera was to where the target is at each step, each step
// advancing it at most one 60 Hz refresh, so a cold first frame (the card waking, the planes made) never leaps; it is
// interrupted by another at any moment (out during the way in starts from where the camera is). The user's own moves (an
// orbit, the wheel, the spacing) are chased exponentially. While it moves, or while the user orbits it, the paused player is
// drawn every refresh (CameraMoving). The flat picture and the 3D view meet at yaw 0, pitch 0, zoom 1 and no spread
// (ExplodedCamera): the way in starts there and the way out ends there, where the scenes become flat again. Each scene
// drawn in 3D is kept (ExplodedScene) for the UI thread's picking and labels.
public sealed partial class GpuPreviewEngine
{
    /// <summary>The 3D view's starting turn (degrees: from the right and from above), distance (times the fitted one) and spacing.</summary>
    public const double DefaultYaw = 30, DefaultPitch = 16, DefaultZoom = 1.85, DefaultSpacing = 0.16;

    /// <summary>How far apart the planes may be set, as fractions of the canvas's larger side.</summary>
    public const double MinSpacing = 0.02, MaxSpacing = 0.6;

    // The camera's time constant while it chases the user's own moves, seconds: 95 % of the way in three of them.
    private const double CameraSettle = 0.085;

    /// <summary>How long the way in, the way out and Reset take, seconds (5.3-Stage3).</summary>
    public const double CameraTransitionSeconds = 0.24;

    /// <summary>The most one step of the camera advances it, seconds: one refresh at 60 Hz, so a slow frame never makes it leap.</summary>
    public const double MaxCameraStep = 1 / 60.0;

    private readonly object _camera = new();

    // The target, set by the UI thread; the camera drawn, stepped by the pump (both under _camera).
    private bool _wantExploded;
    private double _yaw = DefaultYaw, _pitch = DefaultPitch, _zoom = DefaultZoom, _spacing = DefaultSpacing;
    private double _drawnYaw, _drawnPitch, _drawnZoom = 1, _drawnSpacing;
    private long _cameraTicks;

    // A transition under way (the way in, out, or Reset): the camera it left from, and how far through it the steps have taken
    // it (seconds; null: none).
    private CameraPose _transitionFrom;
    private double? _transitionClock;

    /// <summary>A pose of the 3D view's camera: its turn (degrees), its distance (times the fitted one) and the planes' spacing.</summary>
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
    /// One step of the camera (pure: the engine's, and the goldens' exploded-3d-transition-pacing): <paramref name="elapsed"/> seconds
    /// since the last step, held to <see cref="MaxCameraStep"/>. In a transition, the cubic ease in and out from where it left to the
    /// target as it is now, landing exactly on it once <see cref="CameraTransitionSeconds"/> of steps have passed (the clock then
    /// null); otherwise an exponential chase of the target, landing on it once near.
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

    // When the scenes became flat again (Stopwatch; 0 once the flat picture was drawn again for the worker to let go of the
    // 3D textures: it does so only as it draws, a while after the last 3D frame). The pump's own.
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

    /// <summary>Whether the player draws the 3D view now: from the toggle on until the way back to the flat picture has settled.</summary>
    public bool IsDrawingExploded => _drawingExploded;

    /// <summary>Into the 3D exploded view (from the flat picture, the camera swinging out) or back out of it; interruptible either way.</summary>
    public void SetExploded(bool on)
    {
        lock (_camera)
        {
            if (_wantExploded == on)
                return;
            _wantExploded = on;
            if (on && !_drawingExploded)
            {
                // From the flat picture: the drawn camera starts where the 2D view is, and the last time's scene is no one's.
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
    /// Where the camera is going (5.4D: a project tab put behind keeps it in its header): its turn, distance and spacing as the user
    /// left them, whether the view is on or not (the flat picture's pose is the way out's end, never this).
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
    /// Into the 3D view or out of it at once, with no way in or out (5.4D: a project tab put behind, or one brought back to the front as it
    /// was left): the camera drawn is where it is going from the next scene on. Into it at <paramref name="pose"/> (held to the camera's
    /// limits as an orbit, the wheel and the spacing are; the view's start when null or not a number).
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

    /// <summary>A pose held to what the camera can do: the turns short of edge on, the distance and the spacing within theirs, the view's start for what is not a number.</summary>
    internal static CameraPose HeldToLimits(CameraPose pose)
    {
        static double Held(double value, double fallback, double low, double high) =>
            double.IsFinite(value) ? Math.Max(low, Math.Min(value, high)) : fallback;

        return new(Held(pose.Yaw, DefaultYaw, -ExplodedCamera.MaxTurnDegrees, ExplodedCamera.MaxTurnDegrees),
            Held(pose.Pitch, DefaultPitch, -ExplodedCamera.MaxTurnDegrees, ExplodedCamera.MaxTurnDegrees),
            Held(pose.Zoom, DefaultZoom, ExplodedCamera.MinZoom, ExplodedCamera.MaxZoom),
            Held(pose.Spacing, DefaultSpacing, MinSpacing, MaxSpacing));
    }

    /// <summary>Turns the camera by so many degrees (positive yaw: towards the right; positive pitch: up), held short of edge on.</summary>
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

    /// <summary>How far apart the planes are, as a fraction of the canvas's larger side (<see cref="MinSpacing"/> to <see cref="MaxSpacing"/>).</summary>
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

    /// <summary>The camera's turn and distance as they start (the spacing is kept): 240 ms back there, as the way in.</summary>
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
    /// The camera's turn and distance set to <paramref name="pose"/>, held to its limits (5.4.1-M1: the front tab's last 3D angle when the
    /// view is turned on again by hand; the spacing stays the panel's): 240 ms there when the view is up, as a reset is.
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

    /// <summary>While the user holds an orbit (a drag), the paused player is drawn every refresh even once the camera has caught up.</summary>
    public void SetOrbiting(bool held)
    {
        _orbiting = held;
        Invalidate();
    }

    /// <summary>The clip selected in the editor (<see cref="GpuHash.Of"/> its id; 0 none): outlined in the 3D view.</summary>
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
    /// A layer lifted along the stack while it is dragged in depth (its clip's key; 0 puts it back): drawn so many places
    /// nearer (positive) or farther, a preview only, until the drag ends and its track is moved.
    /// </summary>
    public void LiftLayer(long clipKey, double places)
    {
        lock (_camera)
            (_liftedKey, _liftedBy) = (clipKey, clipKey == 0 || !double.IsFinite(places) ? 0 : places);
        Invalidate();
    }

    /// <summary>
    /// Copies the last scene drawn in 3D into <paramref name="into"/> when it is newer than the one it holds (UI thread: its
    /// picking and labels). False when there is nothing newer.
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
            // Out of the 3D view a while: the flat picture once more, so that the worker lets go of the 3D view's textures (up
            // to some hundreds of MB its Instant Seek cache would otherwise make room for) while the player stays paused too.
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
                // Nothing to draw on (the player hidden): the camera is where it is going at once, the flat picture included.
                (Drawn, _transitionClock) = (Target, null);
                if (!_wantExploded)
                    (_drawingExploded, _flatSince) = (false, Stopwatch.GetTimestamp());
                return;
            }

            moving = _orbiting || !Settled();
        }

        // While the camera turns, glides or is dragged, background work waits (5.4.1-M2): the worker's frames come first.
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
    /// A scene of <paramref name="plan"/> drawn in 3D with the camera at <paramref name="pose"/>, not stepped and not kept for picking
    /// (5.4D: a project tab's still as it was shown, GpuPreviewEngine.CaptureThumbnail; the pump's thread).
    /// </summary>
    private void WriteCameraAt(ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers, CameraPose pose, GpuScenePlan plan)
    {
        lock (_camera)
            WriteStack(ref header, layers, pose, plan);
    }

    /// <summary>The 3D view's camera and each layer's place, outline and lift written into a scene (under _camera).</summary>
    private void WriteStack(ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers, CameraPose pose, GpuScenePlan plan)
    {
        header.ViewMode = 1;
        header.Flags |= FrameSceneHeader.FlagDepthRails;
        header.CameraYaw = (float)pose.Yaw;
        header.CameraPitch = (float)pose.Pitch;
        header.CameraZoom = (float)pose.Zoom;
        header.ExplodedZSpread = (float)(pose.Spacing * Math.Max(Math.Max(header.CanvasWidth, header.CanvasHeight), 1));

        // Each layer at its track's place in the plan's stack, which is as deep as the plan whatever is on now (5.0-Release:
        // a caption between its cues, or a layer waiting for its texture, no longer moves the planes above it or the
        // camera's middle). The clip selected is outlined, and another clip of its track playing while it is not more
        // faintly, so the selected layer's plane never blinks off at its cuts; a layer dragged in depth is drawn where
        // the drag has it.
        var count = Math.Clamp(header.ActiveLayerCount, 0, layers.Length);
        header.ExplodedDepth = plan.PlaceLayers(layers, count);
        var selected = Interlocked.Read(ref _selectedClipKey);
        var selectedPlace = selected != 0 ? plan.PlaceOf(selected) : -1;
        for (var k = 0; k < count; k++)
        {
            ref var layer = ref layers[k];
            if ((layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0)
            {
                // A blur fill's own plane (5.2.0's re-cut): outlined while the Background Blur is selected.
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
    /// The camera a still as it is shown is drawn with (5.4D): where it is going while the 3D view is asked for (a way in
    /// under way is drawn as it will land); null for the flat picture.
    /// </summary>
    private CameraPose? ShownPose()
    {
        lock (_camera)
            return _wantExploded ? new CameraPose(_yaw, _pitch, _zoom, _spacing) : null;
    }

    /// <summary>
    /// The drawn camera one frame on (under _camera): by the time since the last step, at most one 60 Hz refresh; the first step
    /// after the camera stood still counts as one refresh, so a move never starts with a leap. Lands exactly on the target, so the
    /// way out ends on the flat picture itself.
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
