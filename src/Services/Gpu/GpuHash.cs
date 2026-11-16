using System.Runtime.InteropServices;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// Immutable, chainable 64-bit FNV-1a hash that identifies and deduplicates GPU resources (mask recipes, still text and style,
/// LUT files). Values hash by their bits, never their text, so culture or formatting cannot split equal recipes.
/// </summary>
public readonly struct GpuHash
{
    private const ulong Basis = 0xcbf29ce484222325UL, Prime = 0x100000001b3UL;

    // Stored as value minus basis so default(GpuHash) is the empty hash.
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

    // 0 and -0 hash identically.
    public GpuHash Add(double value) => Add(BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value));

    public GpuHash Add(string? text)
    {
        var hash = Current;
        foreach (var b in MemoryMarshal.AsBytes((text ?? "").AsSpan()))
        {
            hash ^= b;
            hash *= Prime;
        }

        // Mix in the length so "ab" + "c" differs from "a" + "bc".
        return new GpuHash(hash).Add((text ?? "").Length);
    }

    /// <summary>Raw bytes (e.g. layer descriptors for <see cref="GpuProgramStrip"/>) followed by their count.</summary>
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

    /// <summary>Stable layer key for a clip id (<see cref="GpuLayerDescriptor.ClipKey"/>), identical across plans and sessions.</summary>
    public static long Of(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return default(GpuHash).Add(MemoryMarshal.Read<long>(bytes)).Add(MemoryMarshal.Read<long>(bytes[8..])).Value;
    }
}
