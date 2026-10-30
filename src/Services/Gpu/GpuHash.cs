using System.Runtime.InteropServices;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// A 64-bit FNV-1a hash built up value by value (each <c>Add</c> gives the hash with that value added: chain them):
/// what the GPU engine's resources are told apart and deduplicated by (a mask's recipe, a still's words and style, a
/// LUT's file). Values are hashed by their bits, never by their text, so no culture or formatting can make two equal
/// recipes differ.
/// </summary>
public readonly struct GpuHash
{
    private const ulong Basis = 0xcbf29ce484222325UL, Prime = 0x100000001b3UL;

    // Kept as the value minus the basis, so that default(GpuHash) is the empty hash too.
    private readonly ulong _state;

    private GpuHash(ulong value) => _state = value - Basis;

    private ulong Current => _state + Basis;

    public long Value => (long)Current;

    public GpuHash Add(long value)
    {
        var hash = Current;
        for (var i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= Prime;
        }

        return new GpuHash(hash);
    }

    public GpuHash Add(int value) => Add((long)value);

    public GpuHash Add(bool value) => Add(value ? 1L : 0L);

    // (0 and -0 are one value.)
    public GpuHash Add(double value) => Add(BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value));

    public GpuHash Add(string? text)
    {
        var hash = Current;
        foreach (var b in MemoryMarshal.AsBytes((text ?? "").AsSpan()))
        {
            hash ^= b;
            hash *= Prime;
        }

        // The length too, so that "ab" + "c" is not "a" + "bc".
        return new GpuHash(hash).Add((text ?? "").Length);
    }

    /// <summary>Bytes as they are (a scene's layer descriptors: <see cref="GpuProgramStrip"/>), and their count.</summary>
    public GpuHash Add(ReadOnlySpan<byte> bytes)
    {
        var hash = Current;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= Prime;
        }

        return new GpuHash(hash).Add(bytes.Length);
    }

    /// <summary>A clip's id as a layer's key (<see cref="GpuLayerDescriptor.ClipKey"/>): the same id always gives the same key, across plans and sessions.</summary>
    public static long Of(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return default(GpuHash).Add(MemoryMarshal.Read<long>(bytes)).Add(MemoryMarshal.Read<long>(bytes[8..])).Value;
    }
}
