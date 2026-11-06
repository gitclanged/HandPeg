using System.IO;
using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;

namespace HandPegApp.Compositor;

/// <summary>
/// The constant buffer every pass reads (cbuffer Pass, register b0, in Shaders/Common.hlsli): 20 registers, 320 bytes,
/// laid out exactly as HLSL packs it. The packets sit where the contract puts them: nine scalars from register 0 (and
/// a second packet from register 3, for an effect sent as two).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
internal struct PassConstants
{
    public const int SizeInBytes = 320;

    [FieldOffset(0)] public GpuEffectPacket Packet;
    [FieldOffset(48)] public GpuEffectPacket Packet1;
    [FieldOffset(96)] public float SrcTexelX;
    [FieldOffset(100)] public float SrcTexelY;
    [FieldOffset(104)] public float DstWidth;
    [FieldOffset(108)] public float DstHeight;
    [FieldOffset(112)] public float U0;
    [FieldOffset(116)] public float V0;
    [FieldOffset(120)] public float U1;
    [FieldOffset(124)] public float V1;
    [FieldOffset(128)] public Matrix4 Transform;
    [FieldOffset(192)] public Vector4 Color;
    [FieldOffset(208)] public Vector4 Params;
    [FieldOffset(224)] public Vector4 Params2;
    [FieldOffset(240)] public Vector4 Params3;
    [FieldOffset(256)] public Vector4 YuvRow0;
    [FieldOffset(272)] public Vector4 YuvRow1;
    [FieldOffset(288)] public Vector4 YuvRow2;
    [FieldOffset(304)] public Vector4 Params4;

    /// <summary>Sets the input's texel size and the target's size.</summary>
    public void Sizes(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        SrcTexelX = 1f / Math.Max(sourceWidth, 1);
        SrcTexelY = 1f / Math.Max(sourceHeight, 1);
        DstWidth = targetWidth;
        DstHeight = targetHeight;
    }

    public void WholeUv() => (U0, V0, U1, V1) = (0, 0, 1, 1);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Vector4(float x, float y, float z, float w)
{
    public float X = x, Y = y, Z = z, W = w;
}

/// <summary>A 4 x 4 matrix, row-major, for row vectors (p' = p * M), as HLSL's row_major float4x4 reads it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Matrix4
{
    public float M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44;

    public static Matrix4 Identity => new() { M11 = 1, M22 = 1, M33 = 1, M44 = 1 };

    /// <summary>
    /// The quad's corner (0..1, 0..1) to clip space for a box on a target: scaled to the box, turned about its anchor
    /// (degrees clockwise, y down), moved into place, then pixels to clip space. Tilted (5.2A, true 3D: <paramref name="tiltX"/> its
    /// Pitch, <paramref name="tiltY"/> its Yaw, in degrees), it is also turned out of the frame's plane about its anchor and seen in
    /// perspective (<see cref="Tilted"/>); untilted, the matrix is the flat one, number for number.
    /// </summary>
    public static Matrix4 Box(float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY, float targetWidth, float targetHeight,
        float tiltX = 0, float tiltY = 0)
    {
        if (tiltX != 0 || tiltY != 0)
            return Tilted(x, y, width, height, rotationDegrees, anchorX, anchorY, targetWidth, targetHeight, tiltX, tiltY);

        var radians = rotationDegrees * MathF.PI / 180f;
        var (cos, sin) = (MathF.Cos(radians), MathF.Sin(radians));
        var (ax, ay) = (x + anchorX * width, y + anchorY * height);

        // Corner (u, v): local point (u*w - anchorX*w, v*h - anchorY*h), turned clockwise in a y-down frame, then + anchor.
        //   px = ax + (u*w - axw) * cos - (v*h - ayh) * sin
        //   py = ay + (u*w - axw) * sin + (v*h - ayh) * cos
        var (ox, oy) = (-anchorX * width, -anchorY * height);
        var px0 = ax + ox * cos - oy * sin;
        var py0 = ay + ox * sin + oy * cos;

        // Pixels to clip space: x' = px * 2/W - 1, y' = 1 - py * 2/H.
        var (sx, sy) = (2f / targetWidth, -2f / targetHeight);
        return new Matrix4
        {
            M11 = width * cos * sx,
            M12 = width * sin * sy,
            M21 = -height * sin * sx,
            M22 = height * cos * sy,
            M33 = 1,
            M41 = px0 * sx - 1,
            M42 = py0 * sy + 1,
            M44 = 1,
        };
    }

    /// <summary>
    /// A box turned in 3D (5.2A) and seen in perspective: its local point (u*w - anchorX*w, v*h - anchorY*h, 0) is turned by its Roll in its
    /// own plane (as the flat box is), swung by its Yaw about the upright axis (its right edge away for a positive one), tilted by its Pitch
    /// about the horizontal one (its top away), and seen by a camera on the anchor's line a distance D in front of the frame: a point at depth
    /// z lands at anchor + (x, y) * D / (D + z). As a homogeneous matrix: w' = 1 + z / D, so the card divides and interpolates the quad's uv
    /// in perspective (VSQuad: nothing else changes). D is 1.2 times the target's diagonal (a moderate lens), further back for a box so
    /// large that a corner would come near the camera.
    /// </summary>
    private static Matrix4 Tilted(float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY, float targetWidth, float targetHeight,
        float tiltX, float tiltY)
    {
        // The corner (u, v) is u * U + v * V + O, each turned: the three columns of the linear map.
        var (u, v, o) = Turned(width, height, rotationDegrees, anchorX, anchorY, tiltX, tiltY);
        var (ax, ay) = (x + anchorX * width, y + anchorY * height);
        var distance = TiltDistance(u, v, o, targetWidth, targetHeight);

        // Clip space: X = sx * px * w' - w', Y = sy * py * w' + w', with px * w' = ax * w' + x, py * w' = ay * w' + y.
        var (sx, sy) = (2f / targetWidth, -2f / targetHeight);
        var (wu, wv, wo) = (u.Z / distance, v.Z / distance, 1 + o.Z / distance);
        var (kx, ky) = (sx * ax - 1, sy * ay + 1);
        return new Matrix4
        {
            M11 = sx * u.X + kx * wu,
            M12 = sy * u.Y + ky * wu,
            M14 = wu,
            M21 = sx * v.X + kx * wv,
            M22 = sy * v.Y + ky * wv,
            M24 = wv,
            M33 = 1,
            M41 = sx * o.X + kx * wo,
            M42 = sy * o.Y + ky * wo,
            M44 = wo,
        };
    }

    /// <summary>
    /// A box's three columns turned in 3D about its anchor (5.2A): its local point (u*w - anchorX*w, v*h - anchorY*h, 0) through its Roll in its
    /// own plane, its Yaw about the upright axis (its right edge away) and its Pitch about the horizontal one (its top away), each tilt held to
    /// 89 degrees. The corner (u, v) lies at anchor + u * U + v * V + O, Z its depth away from the viewer. <see cref="Tilted"/> and the 3D exploded
    /// view's planes (ExplodedCamera.PlaneMatrix, 5.2B) turn a box by these, so the two lay it alike.
    /// </summary>
    internal static ((float X, float Y, float Z) U, (float X, float Y, float Z) V, (float X, float Y, float Z) O) Turned(float width, float height,
        float rotationDegrees, float anchorX, float anchorY, float tiltX, float tiltY)
    {
        var roll = rotationDegrees * MathF.PI / 180f;
        var yaw = Math.Clamp(tiltY, -89f, 89f) * MathF.PI / 180f;
        var pitch = Math.Clamp(tiltX, -89f, 89f) * MathF.PI / 180f;
        var (cosR, sinR) = (MathF.Cos(roll), MathF.Sin(roll));
        var (cosY, sinY) = (MathF.Cos(yaw), MathF.Sin(yaw));
        var (cosP, sinP) = (MathF.Cos(pitch), MathF.Sin(pitch));

        // A local vector (y down) through the three turns: roll in the plane, yaw about the upright axis, pitch about the horizontal one.
        (float X, float Y, float Z) Turn(float lx, float ly)
        {
            var (x1, y1) = (lx * cosR - ly * sinR, lx * sinR + ly * cosR);
            var (x2, z2) = (x1 * cosY, x1 * sinY);
            return (x2, y1 * cosP + z2 * sinP, -y1 * sinP + z2 * cosP);
        }

        return (Turn(width, 0), Turn(0, height), Turn(-anchorX * width, -anchorY * height));
    }

    // The distance a tilted box is seen from (Tilted): 1.2 times the target's diagonal, further back for a box so large that a corner would come near.
    private static float TiltDistance(in (float X, float Y, float Z) u, in (float X, float Y, float Z) v, in (float X, float Y, float Z) o, float targetWidth, float targetHeight)
    {
        var deepest = MathF.Max(MathF.Max(MathF.Abs(o.Z), MathF.Abs(o.Z + u.Z)), MathF.Max(MathF.Abs(o.Z + v.Z), MathF.Abs(o.Z + u.Z + v.Z)));
        return MathF.Max(1.2f * MathF.Sqrt(targetWidth * targetWidth + targetHeight * targetHeight), 1.5f * deepest + 1);
    }

    /// <summary>
    /// The four corners (TL, TR, BR, BL) of a box, in the target's pixels, as Box/Tilted lay them (perspective divided); untilted it is the
    /// flat turned box (5.2B, the user's Q4: the Canvas's and the player's outlines and handles of a tilted clip follow its picture).
    /// </summary>
    internal static (float X, float Y)[] Corners(float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY,
        float targetWidth, float targetHeight, float tiltX = 0, float tiltY = 0)
    {
        var corners = new (float X, float Y)[4];
        var (ax, ay) = (x + anchorX * width, y + anchorY * height);
        if (tiltX == 0 && tiltY == 0)
        {
            // As Box turns it: the local point about the anchor, turned clockwise in a y-down frame.
            var radians = rotationDegrees * MathF.PI / 180f;
            var (cos, sin) = (MathF.Cos(radians), MathF.Sin(radians));
            for (var k = 0; k < 4; k++)
            {
                var (lx, ly) = (((k is 1 or 2) ? 1 : 0) * width - anchorX * width, (k >= 2 ? 1 : 0) * height - anchorY * height);
                corners[k] = (ax + lx * cos - ly * sin, ay + lx * sin + ly * cos);
            }

            return corners;
        }

        // As Tilted sees it: a point at depth z lands at anchor + (x, y) * D / (D + z), D its distance (never behind the eye: the depth is held
        // short of it, as no corner of a box Tilted draws comes so near).
        var (u, v, o) = Turned(width, height, rotationDegrees, anchorX, anchorY, tiltX, tiltY);
        var distance = TiltDistance(u, v, o, Math.Max(targetWidth, 1), Math.Max(targetHeight, 1));
        for (var k = 0; k < 4; k++)
        {
            var (cu, cv) = ((k is 1 or 2) ? 1f : 0f, k >= 2 ? 1f : 0f);
            var (px, py, pz) = (cu * u.X + cv * v.X + o.X, cu * u.Y + cv * v.Y + o.Y, cu * u.Z + cv * v.Z + o.Z);
            var scale = distance / Math.Max(distance + pz, distance * 0.05f);
            corners[k] = (ax + px * scale, ay + py * scale);
        }

        return corners;
    }
}

/// <summary>
/// The compiled shaders, embedded in the program at build time (HandPegApp.csproj, CompileShaders: d3dcompiler_47 on
/// every .hlsl under Compositor/Shaders, one blob per entry point named VS* or PS*): loaded and made into shader
/// objects once per device, a few milliseconds, no compiler at run time.
/// </summary>
internal sealed class ShaderLibrary : IDisposable
{
    private readonly Dictionary<string, nint> _shaders = new(StringComparer.Ordinal);

    public ShaderLibrary(GpuDevice gpu)
    {
        var assembly = typeof(ShaderLibrary).Assembly;
        const string prefix = "HandPegApp.Shaders.";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".cso", StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            var code = new byte[stream.Length];
            stream.ReadExactly(code);
            var entry = name[prefix.Length..^4];
            _shaders[entry] = entry.StartsWith("VS", StringComparison.Ordinal) ? gpu.Device.CreateVertexShader(code) : gpu.Device.CreatePixelShader(code);
        }
    }

    public int Count => _shaders.Count;

    public bool Has(string entry) => _shaders.ContainsKey(entry);

    /// <summary>The shader of an entry point; throws for one that was not built (a missing entry is a build fault, never data).</summary>
    public nint this[string entry] => _shaders.TryGetValue(entry, out var shader) ? shader : throw new InvalidDataException($"The shader {entry} is not in this build.");

    public void Dispose()
    {
        foreach (var key in _shaders.Keys.ToList())
        {
            var shader = _shaders[key];
            Com.Release(ref shader);
        }

        _shaders.Clear();
    }
}
