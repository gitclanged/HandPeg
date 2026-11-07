using System.IO;

namespace HandPegBootstrap;

/// <summary>
/// A download of .NET 10, whole and checked, waits beside <see cref="Root"/> (PortableDotNet.PendingFolder) because the .NET there, which
/// hostfxr refused in this start, cannot be moved aside by this process (5.3): a new start of HandPeg.exe puts it in place first.
/// </summary>
internal sealed class PendingSwapException(string root)
    : IOException($"The new .NET waits beside {root} for HandPeg to start again.")
{
    public string Root { get; } = root;
}
