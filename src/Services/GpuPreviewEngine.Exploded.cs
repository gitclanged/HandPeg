using System.Diagnostics;
using HandPegApp.Compositor;
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

    /// <summary>Only a blurred background (a whole-frame blur under its video): drawn, never picked.</summary>
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
            Planes[k] = new ExplodedPlane
            {
                ClipKey = layer.ClipKey, Index = k, Flags = layer.Flags, ZDepth = layer.ZDepth,
                X = layer.X, Y = layer.Y, Width = layer.Width, Height = layer.Height,
                Rotation = layer.Rotation, AnchorX = layer.AnchorX, AnchorY = layer.AnchorY,
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
// What the user asks for (on, the turn, the distance, the spacing) is the camera's target; the camera drawn chases it on
// the pump's thread, frame by frame, exponentially (a quarter of a second to settle, interrupted by a new target at any
// moment, the way in and the way out alike). While it moves, or while the user orbits it, the paused player is drawn every
// refresh (CameraMoving). The flat picture and the 3D view meet at yaw 0, pitch 0, zoom 1 and no spread
// (ExplodedCamera): the way in starts there and the way out ends there, where the scenes become flat again. Each scene
// drawn in 3D is kept (ExplodedScene) for the UI thread's picking and labels.
public sealed partial class GpuPreviewEngine
{
    /// <summary>The 3D view's starting turn (degrees: from the right and from above), distance (times the fitted one) and spacing.</summary>
    public const double DefaultYaw = 30, DefaultPitch = 16, DefaultZoom = 1.85, DefaultSpacing = 0.16;

    /// <summary>How far apart the planes may be set, as fractions of the canvas's larger side.</summary>
    public const double MinSpacing = 0.02, MaxSpacing = 0.6;

    // The camera's time constant, seconds: 95 % of the way in three of them.
    private const double CameraSettle = 0.085;

    private readonly object _camera = new();

    // The target, set by the UI thread; the camera drawn, stepped by the pump (both under _camera).
    private bool _wantExploded;
    private double _yaw = DefaultYaw, _pitch = DefaultPitch, _zoom = DefaultZoom, _spacing = DefaultSpacing;
    private double _drawnYaw, _drawnPitch, _drawnZoom = 1, _drawnSpacing;
    private long _cameraTicks;

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
                (_drawnYaw, _drawnPitch, _drawnZoom, _drawnSpacing) = (0, 0, 1, 0);
                _cameraTicks = 0;
                _lastExploded.Reset();
                _drawingExploded = true;
            }

            if (!on)
                (_liftedKey, _liftedBy, _orbiting) = (0, 0, false);
        }

        Invalidate();
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

    /// <summary>The camera's turn and distance as they start (the spacing is kept).</summary>
    public void ResetCamera()
    {
        lock (_camera)
            (_yaw, _pitch, _zoom) = (DefaultYaw, DefaultPitch, DefaultZoom);
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
                (_drawnYaw, _drawnPitch, _drawnZoom, _drawnSpacing) = _wantExploded ? (_yaw, _pitch, _zoom, _spacing) : (0.0, 0.0, 1.0, 0.0);
                if (!_wantExploded)
                    (_drawingExploded, _flatSince) = (false, Stopwatch.GetTimestamp());
                return;
            }

            moving = _orbiting || !Settled();
        }
    }

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

            header.ViewMode = 1;
            header.CameraYaw = (float)_drawnYaw;
            header.CameraPitch = (float)_drawnPitch;
            header.CameraZoom = (float)_drawnZoom;
            header.ExplodedZSpread = (float)(_drawnSpacing * Math.Max(Math.Max(header.CanvasWidth, header.CanvasHeight), 1));

            // Each layer at its track's place in the plan's stack, which is as deep as the plan whatever is on now (5.0-Release:
            // a caption between its cues, or a layer waiting for its texture, no longer moves the planes above it or the
            // camera's middle). The clip selected is outlined, and another clip of its track playing while it is not more
            // faintly, so the selected layer's plane never blinks off at its cuts; a layer dragged in depth is drawn where
            // the drag has it.
            var plan = Volatile.Read(ref _plan);
            var count = Math.Clamp(header.ActiveLayerCount, 0, layers.Length);
            header.ExplodedDepth = plan.PlaceLayers(layers, count);
            var selected = Interlocked.Read(ref _selectedClipKey);
            var selectedPlace = selected != 0 ? plan.PlaceOf(selected) : -1;
            for (var k = 0; k < count; k++)
            {
                ref var layer = ref layers[k];
                if ((layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) == 0 && selected != 0)
                {
                    if (layer.ClipKey == selected)
                        layer.Flags |= GpuLayerDescriptor.FlagSelected;
                    else if (selectedPlace >= 0 && layer.ZDepth == selectedPlace)
                        layer.Flags |= GpuLayerDescriptor.FlagSelectedTrack;
                }

                if (_liftedKey != 0 && layer.ClipKey == _liftedKey)
                    layer.ZDepth += (float)_liftedBy;
            }

            _lastExploded.Take(header, layers, plan, ++_explodedVersion);
        }
    }

    /// <summary>The drawn camera one frame nearer its target: exponentially, by the time since the last step (under _camera).</summary>
    private void StepCamera()
    {
        // A step after the camera stood still (or the first) counts as one refresh, so a new move never starts with a leap.
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _cameraTicks) / (double)Stopwatch.Frequency;
        if (_cameraTicks == 0 || elapsed is <= 0 or > 0.1)
            elapsed = 1 / 60.0;
        _cameraTicks = now;
        var share = 1 - Math.Exp(-elapsed / CameraSettle);

        var (yaw, pitch, zoom, spacing) = _wantExploded ? (_yaw, _pitch, _zoom, _spacing) : (0.0, 0.0, 1.0, 0.0);
        _drawnYaw += (yaw - _drawnYaw) * share;
        _drawnPitch += (pitch - _drawnPitch) * share;
        _drawnZoom += (zoom - _drawnZoom) * share;
        _drawnSpacing += (spacing - _drawnSpacing) * share;

        // Close enough is there: the last step lands exactly, so the way out ends on the flat picture itself.
        if (Settled())
            (_drawnYaw, _drawnPitch, _drawnZoom, _drawnSpacing) = (yaw, pitch, zoom, spacing);
    }

    /// <summary>Whether the drawn camera has reached its target (under _camera).</summary>
    private bool Settled()
    {
        var (yaw, pitch, zoom, spacing) = _wantExploded ? (_yaw, _pitch, _zoom, _spacing) : (0.0, 0.0, 1.0, 0.0);
        return Math.Abs(yaw - _drawnYaw) < 0.02 && Math.Abs(pitch - _drawnPitch) < 0.02 && Math.Abs(zoom - _drawnZoom) < 2e-4
            && Math.Abs(spacing - _drawnSpacing) < 2e-5;
    }
}
