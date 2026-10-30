using System.Numerics;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// The camera of the 3D exploded layer stack (Pass 5.0-AudioAnd3D, 5.0D), the one piece of maths the worker draws with and
/// HandPeg picks with, so a press lands on exactly the plane under it. Pure C#, nothing allocated.
/// <para><b>The world</b> is the canvas: x right and y down in canvas pixels, z towards the viewer. Each layer is a plane at
/// z = <see cref="GpuLayerDescriptor.ZDepth"/> x <see cref="FrameSceneHeader.ExplodedZSpread"/> (canvas pixels per place
/// in the stack), its box where the flat picture has it (rotation and anchor included).</para>
/// <para><b>The camera</b> orbits the canvas's middle at half the stack's depth: <see cref="FrameSceneHeader.CameraYaw"/>
/// degrees round the vertical (positive: from the right), <see cref="FrameSceneHeader.CameraPitch"/> degrees up (positive:
/// from above), at <see cref="FrameSceneHeader.CameraZoom"/> times the fitted distance. The fitted distance is where the
/// canvas, seen straight on, is exactly the size the flat player fits it to (centred, as FlagLetterbox has it at no zoom),
/// so at yaw 0, pitch 0, zoom 1 and no spread the 3D picture is the 2D one, and the animation in and out of 3D has no
/// jump. The player's own zoom and pan (ViewScale, ViewPan) are not part of the 3D view: it always covers the whole output,
/// with no bars.</para>
/// <para><b>Screen</b> points are output pixels (the worker's render target, the player's pixel size), from the top left.</para>
/// </summary>
public readonly struct ExplodedCamera
{
    /// <summary>The vertical field of view: a gentle perspective, a lens of about 70 mm on a full frame.</summary>
    public const float FieldOfViewDegrees = 34;

    /// <summary>How far the camera may turn either way, in degrees (never edge on).</summary>
    public const float MaxTurnDegrees = 85;

    /// <summary>The camera's distance, as a factor of the fitted distance.</summary>
    public const float MinZoom = 0.3f, MaxZoom = 8;

    private ExplodedCamera(int canvasWidth, int canvasHeight, int outputWidth, int outputHeight, float spread, float fit, float focal, float amount,
        Vector3 position, Vector3 right, Vector3 down, Vector3 back, Matrix4x4 viewProjection)
    {
        (CanvasWidth, CanvasHeight, OutputWidth, OutputHeight) = (canvasWidth, canvasHeight, outputWidth, outputHeight);
        (Spread, Fit, Focal, Amount) = (spread, fit, focal, amount);
        (Position, Right, Down, Back) = (position, right, down, back);
        ViewProjection = viewProjection;
    }

    public int CanvasWidth { get; }
    public int CanvasHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }

    /// <summary>Canvas pixels from one place in the stack to the next.</summary>
    public float Spread { get; }

    /// <summary>Output pixels per canvas pixel of the flat, fitted picture (the size each plane's picture is drawn at).</summary>
    public float Fit { get; }

    /// <summary>The lens's focal length, in output pixels.</summary>
    public float Focal { get; }

    /// <summary>
    /// How far out of the flat picture the view is, 0 to 1 (from the turn, the spread and the distance): the backdrop, the
    /// frame, the planes' edges and the outlines come in by it, so the flat end of the animation is the 2D picture itself.
    /// </summary>
    public float Amount { get; }

    /// <summary>Where the camera is, and its right, down and backward (from what it looks at towards itself) directions.</summary>
    public Vector3 Position { get; }
    public Vector3 Right { get; }
    public Vector3 Down { get; }
    public Vector3 Back { get; }

    /// <summary>World (row vector, w 1) to clip space, w the depth before the camera: as HLSL's row_major float4x4 reads it.</summary>
    public Matrix4x4 ViewProjection { get; }

    /// <summary>
    /// The camera a scene is drawn with (the worker, and HandPeg picking from what it last published): its stack as deep as
    /// the scene's plan (<see cref="FrameSceneHeader.ExplodedDepth"/>), else as the layers drawn.
    /// </summary>
    public static ExplodedCamera From(in FrameSceneHeader header, int outputWidth, int outputHeight) =>
        Create(header.CanvasWidth, header.CanvasHeight, outputWidth, outputHeight, header.CameraYaw, header.CameraPitch, header.CameraZoom,
            header.ExplodedZSpread, StackDepthOf(header));

    /// <summary>How many places the stack of a scene has: its plan's depth, or (none given) its layers' count.</summary>
    public static int StackDepthOf(in FrameSceneHeader header) => header.ExplodedDepth > 0 ? header.ExplodedDepth : header.ActiveLayerCount;

    /// <summary>
    /// The camera for a canvas seen on an output: turned by <paramref name="yawDegrees"/> and <paramref name="pitchDegrees"/>,
    /// <paramref name="zoom"/> times the fitted distance (0 counts as 1), the planes <paramref name="spread"/> canvas pixels
    /// apart, <paramref name="layerCount"/> of them (the orbit's middle is half their depth).
    /// </summary>
    public static ExplodedCamera Create(int canvasWidth, int canvasHeight, int outputWidth, int outputHeight, float yawDegrees, float pitchDegrees,
        float zoom, float spread, int layerCount)
    {
        var (cw, ch) = (Math.Max(canvasWidth, 1), Math.Max(canvasHeight, 1));
        var (ow, oh) = (Math.Max(outputWidth, 1), Math.Max(outputHeight, 1));
        var yaw = Finite(Math.Clamp(yawDegrees, -MaxTurnDegrees, MaxTurnDegrees));
        var pitch = Finite(Math.Clamp(pitchDegrees, -MaxTurnDegrees, MaxTurnDegrees));
        var distanceFactor = zoom > 0 && float.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : 1;
        var gap = float.IsFinite(spread) ? Math.Max(spread, 0) : 0;

        var fit = Math.Min((float)ow / cw, (float)oh / ch);
        var focal = oh / 2f / MathF.Tan(FieldOfViewDegrees * MathF.PI / 360f);
        var distance = focal / fit * distanceFactor;
        var pivot = new Vector3(cw / 2f, ch / 2f, Math.Max(layerCount - 1, 0) * gap / 2);

        // The camera's frame: back (from the middle towards the camera), right, and down (the screen's down).
        var (sy, cy) = MathF.SinCos(yaw * MathF.PI / 180f);
        var (sp, cp) = MathF.SinCos(pitch * MathF.PI / 180f);
        var back = new Vector3(sy * cp, -sp, cy * cp);
        var right = new Vector3(cy, 0, -sy);
        var down = new Vector3(sp * sy, cp, sp * cy);
        var position = pivot + back * distance;

        // World to camera: x along right, y along down, z the depth in front of it.
        var view = new Matrix4x4(
            right.X, down.X, -back.X, 0,
            right.Y, down.Y, -back.Y, 0,
            right.Z, down.Z, -back.Z, 0,
            -Vector3.Dot(position, right), -Vector3.Dot(position, down), Vector3.Dot(position, back), 1);

        // Camera to clip: x and y by the lens over the output, w the depth, z from the near to the far plane (0 to 1). There
        // is no depth buffer (the planes are drawn back to front); the near plane only cuts what is behind the camera.
        var near = Math.Max(distance * 0.01f, 0.5f);
        var far = distance * 50 + gap * Math.Max(layerCount, 1) * 4 + 10_000;
        var a = far / (far - near);
        var projection = new Matrix4x4(
            2 * focal / ow, 0, 0, 0,
            0, -2 * focal / oh, 0, 0,
            0, 0, a, 1,
            0, 0, -near * a, 0);

        // Out of the flat picture by the turn, the spread and the distance, whichever is the most.
        var spreadShare = gap / Math.Max(cw, ch);
        var amount = Math.Clamp(Math.Max(Math.Max(MathF.Abs(yaw) / 6f, MathF.Abs(pitch) / 6f), Math.Max(spreadShare / 0.012f, MathF.Abs(distanceFactor - 1) / 0.06f)), 0, 1);

        return new ExplodedCamera(cw, ch, ow, oh, gap, fit, focal, amount, position, right, down, back, view * projection);
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;

    /// <summary>A place in the stack (a layer's ZDepth, fractions too while one is dragged in depth) as a world z.</summary>
    public float DepthOf(float zDepth) => zDepth * Spread;

    /// <summary>
    /// How far a plane at <paramref name="z"/> is from the camera along the planes' normal: the painter's key (the planes are
    /// parallel, so the one farther along it is drawn first, whichever way the camera looks).
    /// </summary>
    public float DistanceTo(float z) => MathF.Abs(z - Position.Z);

    /// <summary>
    /// A box's quad corner (0..1, 0..1, 0, 1) to clip space: the box of the flat picture (canvas pixels, turned degrees
    /// clockwise about its anchor, as Matrix4.Box has it) laid at depth <paramref name="z"/>, seen by this camera.
    /// </summary>
    public Matrix4x4 PlaneMatrix(float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY, float z)
    {
        var (sin, cos) = MathF.SinCos(rotationDegrees * MathF.PI / 180f);
        var (ax, ay) = (x + anchorX * width, y + anchorY * height);
        var (ox, oy) = (-anchorX * width, -anchorY * height);
        var place = new Matrix4x4(
            width * cos, width * sin, 0, 0,
            -height * sin, height * cos, 0, 0,
            0, 0, 1, 0,
            ax + ox * cos - oy * sin, ay + ox * sin + oy * cos, z, 1);
        return place * ViewProjection;
    }

    /// <summary>Where a world point is on the output; false when it is not in front of the camera.</summary>
    public bool TryProject(Vector3 world, out Vector2 screen)
    {
        // (System.Numerics' four-vector: the compositor's own Vector4 is the constant buffer's.)
        var clip = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(world, 1), ViewProjection);
        if (clip.W <= 1e-3f)
        {
            screen = new Vector2(float.NaN, float.NaN);
            return false;
        }

        screen = new Vector2((clip.X / clip.W + 1) * OutputWidth / 2, (1 - clip.Y / clip.W) * OutputHeight / 2);
        return true;
    }

    /// <summary>Where a canvas point at depth <paramref name="z"/> is on the output (NaN, NaN when behind the camera): the labels' places.</summary>
    public Vector2 Project(float x, float y, float z) => TryProject(new Vector3(x, y, z), out var screen) ? screen : new Vector2(float.NaN, float.NaN);

    /// <summary>The ray from the camera through an output point (its direction of length 1).</summary>
    public void Ray(float screenX, float screenY, out Vector3 origin, out Vector3 direction)
    {
        origin = Position;
        var across = (screenX - OutputWidth / 2f) / Focal;
        var downward = (screenY - OutputHeight / 2f) / Focal;
        direction = Vector3.Normalize(Right * across + Down * downward - Back);
    }

    /// <summary>
    /// Where the ray through an output point meets the plane at depth <paramref name="z"/>, in canvas pixels; false when it
    /// meets it behind the camera or not at all (seen edge on).
    /// </summary>
    public bool HitPlane(float screenX, float screenY, float z, out Vector2 canvas)
    {
        Ray(screenX, screenY, out var origin, out var direction);
        if (MathF.Abs(direction.Z) < 1e-6f)
        {
            canvas = default;
            return false;
        }

        var t = (z - origin.Z) / direction.Z;
        if (t <= 0)
        {
            canvas = default;
            return false;
        }

        var hit = origin + direction * t;
        canvas = new Vector2(hit.X, hit.Y);
        return true;
    }

    /// <summary>A point of a box (u, v from 0 to 1 across and down it) on the canvas, the box turned about its anchor as drawn.</summary>
    public static Vector2 BoxPoint(float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY, float u, float v)
    {
        var (sin, cos) = MathF.SinCos(rotationDegrees * MathF.PI / 180f);
        var (ax, ay) = (x + anchorX * width, y + anchorY * height);
        var (ox, oy) = ((u - anchorX) * width, (v - anchorY) * height);
        return new Vector2(ax + ox * cos - oy * sin, ay + ox * sin + oy * cos);
    }

    /// <summary>Whether a canvas point is inside a box turned about its anchor (a box of negative size counts as its mirror).</summary>
    public static bool InBox(Vector2 point, float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY)
    {
        var (sin, cos) = MathF.SinCos(rotationDegrees * MathF.PI / 180f);
        var (dx, dy) = (point.X - (x + anchorX * width), point.Y - (y + anchorY * height));

        // Turned back (anticlockwise), into the box's own frame, from its top left corner.
        var u = dx * cos + dy * sin + anchorX * width;
        var v = -dx * sin + dy * cos + anchorY * height;
        return u >= Math.Min(0, width) && u <= Math.Max(0, width) && v >= Math.Min(0, height) && v <= Math.Max(0, height);
    }
}
