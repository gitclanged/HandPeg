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
    /// (degrees clockwise, y down), moved into place, then pixels to clip space.
    /// </summary>
    public static Matrix4 Box(float x, float y, float width, float height, float rotationDegrees, float anchorX, float anchorY, float targetWidth, float targetHeight)
    {
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
