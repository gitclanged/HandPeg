using System.Numerics;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// Camera for the 3D exploded layer stack: the single source of maths for worker rendering and editor picking, so a click hits exactly
/// the plane under it. Pure C#, allocation-free.
/// <para><b>World</b> = canvas space: x right, y down in canvas pixels, z toward the viewer. Each layer is a plane at
/// z = <see cref="GpuLayerDescriptor.ZDepth"/> x <see cref="FrameSceneHeader.ExplodedZSpread"/> (canvas pixels per stack step) with its
/// flat box (rotation and anchor included).</para>
/// <para><b>Camera</b> orbits the canvas centre at half the stack depth: <see cref="FrameSceneHeader.CameraYaw"/> degrees about the
/// vertical (positive = from the right), <see cref="FrameSceneHeader.CameraPitch"/> degrees up (positive = from above), at
/// <see cref="FrameSceneHeader.CameraZoom"/> x the fitted distance, where the head-on canvas matches the flat player's letterboxed fit.
/// At yaw 0, pitch 0, zoom 1 and no spread the 3D image equals the 2D one, so the transition has no jump. Player zoom/pan (ViewScale,
/// ViewPan) do not apply: the 3D view always fills the output.</para>
/// <para><b>Screen</b> points are output pixels (render target size) from the top-left.</para>
/// </summary>
public readonly struct ExplodedCamera
{
    /// <summary>Vertical field of view: gentle perspective (~70 mm full-frame equivalent).</summary>
    public const float FieldOfViewDegrees = 34;

    /// <summary>Maximum yaw/pitch either way, degrees (never edge-on).</summary>
    public const float MaxTurnDegrees = 85;

    /// <summary>Camera distance limits as a factor of the fitted distance.</summary>
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

    /// <summary>Canvas pixels between adjacent stack planes.</summary>
    public float Spread { get; }

    /// <summary>Output pixels per canvas pixel of the flat fitted image (each plane's render scale).</summary>
    public float Fit { get; }

    /// <summary>Focal length in output pixels.</summary>
    public float Focal { get; }

    /// <summary>
    /// Departure from the flat view, 0..1 (largest of the turn, spread and distance terms): backdrop, frame, plane edges and outlines
    /// fade in by it, so the flat end of the animation is exactly the 2D image.
    /// </summary>
    public float Amount { get; }

    /// <summary>Camera position and its right, down and back (target-to-camera) unit vectors.</summary>
    public Vector3 Position { get; }
    public Vector3 Right { get; }
    public Vector3 Down { get; }
    public Vector3 Back { get; }

    /// <summary>World (row vector, w = 1) to clip space with w = view depth; matches HLSL row_major float4x4.</summary>
    public Matrix4x4 ViewProjection { get; }

    /// <summary>
    /// Camera for a scene (worker rendering, and editor picking from the last published scene): stack depth from the plan
    /// (<see cref="FrameSceneHeader.ExplodedDepth"/>), else the drawn layer count.
    /// </summary>
    public static ExplodedCamera From(in FrameSceneHeader header, int outputWidth, int outputHeight) =>
        Create(header.CanvasWidth, header.CanvasHeight, outputWidth, outputHeight, header.CameraYaw, header.CameraPitch, header.CameraZoom,
            header.ExplodedZSpread, StackDepthOf(header));

    /// <summary>Stack depth of a scene: the plan's depth, else its active layer count.</summary>
    public static int StackDepthOf(in FrameSceneHeader header) => header.ExplodedDepth > 0 ? header.ExplodedDepth : header.ActiveLayerCount;

    /// <summary>
    /// Camera for a canvas on an output: rotated by <paramref name="yawDegrees"/>/<paramref name="pitchDegrees"/>, at <paramref name="zoom"/> x the
    /// fitted distance (0 = 1), with <paramref name="layerCount"/> planes <paramref name="spread"/> canvas pixels apart (orbit at half their depth).
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

        // Camera basis: back (centre toward camera), right, and down (screen down).
        var (sy, cy) = MathF.SinCos(yaw * MathF.PI / 180f);
        var (sp, cp) = MathF.SinCos(pitch * MathF.PI / 180f);
        var back = new Vector3(sy * cp, -sp, cy * cp);
        var right = new Vector3(cy, 0, -sy);
        var down = new Vector3(sp * sy, cp, sp * cy);
        var position = pivot + back * distance;

        // World to camera: x along right, y along down, z = depth in front.
        var view = new Matrix4x4(
            right.X, down.X, -back.X, 0,
            right.Y, down.Y, -back.Y, 0,
            right.Z, down.Z, -back.Z, 0,
            -Vector3.Dot(position, right), -Vector3.Dot(position, down), Vector3.Dot(position, back), 1);

        // Camera to clip: x, y scaled by focal over output size, w = depth, z mapped near..far to 0..1. There is no depth buffer
        // (planes draw back to front); the near plane only culls what is behind the camera.
        var near = Math.Max(distance * 0.01f, 0.5f);
        var far = distance * 50 + gap * Math.Max(layerCount, 1) * 4 + 10_000;
        var a = far / (far - near);
        var projection = new Matrix4x4(
            2 * focal / ow, 0, 0, 0,
            0, -2 * focal / oh, 0, 0,
            0, 0, a, 1,
            0, 0, -near * a, 0);

        // Departure from flat: the largest of the turn, spread and distance terms.
        var spreadShare = gap / Math.Max(cw, ch);
        var amount = Math.Clamp(Math.Max(Math.Max(MathF.Abs(yaw) / 6f, MathF.Abs(pitch) / 6f), Math.Max(spreadShare / 0.012f, MathF.Abs(distanceFactor - 1) / 0.06f)), 0, 1);

        return new ExplodedCamera(cw, ch, ow, oh, gap, fit, focal, amount, position, right, down, back, view * projection);
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;

    /// <summary>World z for a stack position (a layer's ZDepth; fractional while dragged in depth).</summary>
    public float DepthOf(float zDepth) => zDepth * Spread;

    /// <summary>
    /// Distance from the camera to the plane at <paramref name="z"/> along the plane normal: the painter's sort key (planes are parallel,
    /// so the farther one draws first whatever the view direction).
    /// </summary>
    public float DistanceTo(float z) => MathF.Abs(z - Position.Z);

    /// <summary>
    /// Quad corner (0..1, 0..1, 0, 1) to clip space for a flat box (canvas pixels, rotated clockwise about its anchor as in Matrix4.Box)
    /// placed at depth <paramref name="z"/>. With tilt (<paramref name="tiltX"/> = pitch, <paramref name="tiltY"/> = yaw, degrees) it also
    /// rotates out of plane exactly as Matrix4.Tilted does (shared Matrix4.Turned) but without its own perspective: this camera supplies it.
    /// Untilted it is exactly the flat matrix.
    /// </summary>
    public Matrix4x4 PlaneMatrix(float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY, float z,
        float tiltX = 0, float tiltY = 0)
    {
        if (tiltX != 0 || tiltY != 0)
        {
            // Turned columns measure depth away from the viewer; world z points toward it.
            var (u, v, o) = Matrix4.Turned(width, height, rotationDegrees, anchorX, anchorY, tiltX, tiltY);
            var (pivotX, pivotY) = (x + anchorX * width, y + anchorY * height);
            var turned = new Matrix4x4(
                u.X, u.Y, -u.Z, 0,
                v.X, v.Y, -v.Z, 0,
                0, 0, 1, 0,
                pivotX + o.X, pivotY + o.Y, z - o.Z, 1);
            return turned * ViewProjection;
        }

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

    /// <summary>Camera-facing hairline quad between two world points, about <paramref name="pixels"/> output pixels wide at its midpoint.</summary>
    public Matrix4x4 LineMatrix(Vector3 from, Vector3 to, float pixels)
    {
        var way = to - from;
        var side = Vector3.Cross(way, Back);
        var length = side.Length();
        side = length > 1e-5f ? side / length : Right;
        var middle = (from + to) / 2;
        var across = side * (pixels * Vector3.Dot(Position - middle, Back) / Focal);
        var origin = from - across / 2;
        var place = new Matrix4x4(
            way.X, way.Y, way.Z, 0,
            across.X, across.Y, across.Z, 0,
            0, 0, 1, 0,
            origin.X, origin.Y, origin.Z, 1);
        return place * ViewProjection;
    }

    /// <summary>Projects a world point to output pixels; false when it is not in front of the camera.</summary>
    public bool TryProject(Vector3 world, out Vector2 screen)
    {
        // System.Numerics.Vector4: the compositor's own Vector4 is the constant-buffer type.
        var clip = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(world, 1), ViewProjection);
        if (clip.W <= 1e-3f)
        {
            screen = new Vector2(float.NaN, float.NaN);
            return false;
        }

        screen = new Vector2((clip.X / clip.W + 1) * OutputWidth / 2, (1 - clip.Y / clip.W) * OutputHeight / 2);
        return true;
    }

    /// <summary>Output position of a canvas point at depth <paramref name="z"/> (NaN, NaN when behind the camera); used for labels.</summary>
    public Vector2 Project(float x, float y, float z) => TryProject(new Vector3(x, y, z), out var screen) ? screen : new Vector2(float.NaN, float.NaN);

    /// <summary>Ray from the camera through an output point (unit direction).</summary>
    public void Ray(float screenX, float screenY, out Vector3 origin, out Vector3 direction)
    {
        origin = Position;
        var across = (screenX - OutputWidth / 2f) / Focal;
        var downward = (screenY - OutputHeight / 2f) / Focal;
        direction = Vector3.Normalize(Right * across + Down * downward - Back);
    }

    /// <summary>
    /// Intersection of the ray through an output point with the plane at depth <paramref name="z"/>, in canvas pixels; false when behind
    /// the camera or parallel (edge-on).
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

    /// <summary>
    /// Intersection of the ray through an output point with a box's plane as <see cref="PlaneMatrix"/> places it (tilted by
    /// <paramref name="tiltX"/>/<paramref name="tiltY"/> about its anchor): <paramref name="uv"/> in the box's own 0..1 frame and the world
    /// point. False when behind the camera or edge-on. Untilted it matches <see cref="HitPlane"/>.
    /// </summary>
    public bool HitBox(float screenX, float screenY, float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY,
        float z, float tiltX, float tiltY, out Vector2 uv, out Vector3 world)
    {
        (uv, world) = (default, default);
        Ray(screenX, screenY, out var origin, out var direction);

        // Plane corner (cu, cv) = P + cu * U + cv * V (PlaneMatrix rows), solved against origin + t * direction.
        Vector3 u3, v3, p;
        if (tiltX != 0 || tiltY != 0)
        {
            var (u, v, o) = Matrix4.Turned(width, height, rotationDegrees, anchorX, anchorY, tiltX, tiltY);
            (u3, v3) = (new Vector3(u.X, u.Y, -u.Z), new Vector3(v.X, v.Y, -v.Z));
            p = new Vector3(x + anchorX * width + o.X, y + anchorY * height + o.Y, z - o.Z);
        }
        else
        {
            var (sin, cos) = MathF.SinCos(rotationDegrees * MathF.PI / 180f);
            (u3, v3) = (new Vector3(width * cos, width * sin, 0), new Vector3(-height * sin, height * cos, 0));
            var corner = BoxPoint(x, y, width, height, rotationDegrees, anchorX, anchorY, 0, 0);
            p = new Vector3(corner.X, corner.Y, z);
        }

        // Solve a U + b V - t D = O - P by Cramer's rule (3x3 determinant = first column dotted with the cross of the others).
        var against = -direction;
        var determinant = Vector3.Dot(u3, Vector3.Cross(v3, against));
        if (MathF.Abs(determinant) < 1e-6f * MathF.Max(u3.Length() * v3.Length(), 1))
            return false;
        var r = origin - p;
        var a = Vector3.Dot(r, Vector3.Cross(v3, against)) / determinant;
        var b = Vector3.Dot(u3, Vector3.Cross(r, against)) / determinant;
        var t = Vector3.Dot(u3, Vector3.Cross(v3, r)) / determinant;
        if (!(t > 0) || !float.IsFinite(a) || !float.IsFinite(b))
            return false;
        (uv, world) = (new Vector2(a, b), origin + direction * t);
        return true;
    }

    /// <summary>World point at box coordinate (u, v) on a (tilted) box plane at depth <paramref name="z"/>, as <see cref="PlaneMatrix"/> places it.</summary>
    public static Vector3 OnBox(float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY, float z,
        float tiltX, float tiltY, float u, float v)
    {
        if (tiltX == 0 && tiltY == 0)
        {
            var flat = BoxPoint(x, y, width, height, rotationDegrees, anchorX, anchorY, u, v);
            return new Vector3(flat.X, flat.Y, z);
        }

        var (cu, cv, o) = Matrix4.Turned(width, height, rotationDegrees, anchorX, anchorY, tiltX, tiltY);
        return new Vector3(x + anchorX * width + o.X + u * cu.X + v * cv.X, y + anchorY * height + o.Y + u * cu.Y + v * cv.Y, z - o.Z - u * cu.Z - v * cv.Z);
    }

    /// <summary>Canvas point at box coordinate (u, v in 0..1), the box rotated about its anchor as drawn.</summary>
    public static Vector2 BoxPoint(float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY, float u, float v)
    {
        var (sin, cos) = MathF.SinCos(rotationDegrees * MathF.PI / 180f);
        var (ax, ay) = (x + anchorX * width, y + anchorY * height);
        var (ox, oy) = ((u - anchorX) * width, (v - anchorY) * height);
        return new Vector2(ax + ox * cos - oy * sin, ay + ox * sin + oy * cos);
    }

    /// <summary>True when a canvas point lies inside a box rotated about its anchor (negative sizes count as mirrored).</summary>
    public static bool InBox(Vector2 point, float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY)
    {
        var (sin, cos) = MathF.SinCos(rotationDegrees * MathF.PI / 180f);
        var (dx, dy) = (point.X - (x + anchorX * width), point.Y - (y + anchorY * height));

        // Rotate back (counter-clockwise) into the box frame, measured from its top-left corner.
        var u = dx * cos + dy * sin + anchorX * width;
        var v = -dx * sin + dy * cos + anchorY * height;
        return u >= Math.Min(0, width) && u <= Math.Max(0, width) && v >= Math.Min(0, height) && v <= Math.Max(0, height);
    }
}
