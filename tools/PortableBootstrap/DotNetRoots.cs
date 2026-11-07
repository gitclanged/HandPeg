using System.IO;
using System.Runtime.InteropServices;

namespace HandPegBootstrap;

/// <summary>A .NET root HandPeg can run on: the folder hostfxr is told is the root (its shared frameworks under it), and its hostfxr.dll.</summary>
internal sealed record DotNetRoot(string Folder, string HostFxr, string Where);

/// <summary>
/// Where a .NET 8 Desktop Runtime for this processor is looked for, in order (Pass 5.2.1): this copy's own (portable\dotnet, as the
/// download leaves it; or hostfxr.dll and shared\ beside HandPeg.exe, as 5.2.0's script left it), then DOTNET_ROOT_X64 / DOTNET_ROOT_ARM64
/// and DOTNET_ROOT, then the one installed on the PC (where its installer registered it, else Program Files\dotnet; for an x64 copy on an
/// ARM64 PC, Program Files\dotnet\x64). A root counts only with a hostfxr.dll and both Microsoft.NETCore.App and Microsoft.WindowsDesktop.App
/// of major version 8 (HandPeg.runtimeconfig.json asks for 8.0.0, rolling forward within 8). Only directories are looked at: no process,
/// no network. Shared with the golden tests (portable-bootstrap), which build layouts on disk and ask.
/// </summary>
internal static class DotNetRoots
{
    /// <summary>The .NET major version HandPeg runs on.</summary>
    public const int Major = 8;

    /// <summary>Where the download unpacks .NET, under HandPeg.exe's folder.</summary>
    public static readonly string PortableFolder = Path.Combine("portable", "dotnet");

    /// <summary>This process's processor, as Microsoft's file names have it ("x64", "arm64").</summary>
    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>
    /// The places to look, in order, with what each is called in an error: this copy's own two, then the environment's, then the
    /// installed one. <paramref name="system"/> false leaves out all but this copy's own (a test standing in for a PC with no .NET).
    /// </summary>
    public static IEnumerable<(string Folder, string Where)> Candidates(string app, bool system, Func<string, string?> environment, string? registered, string programFiles, string arch, bool x64OnArm64)
    {
        yield return (Path.Combine(app, PortableFolder), "this copy's portable\\dotnet");
        yield return (app, "this copy's folder");
        if (!system)
            yield break;

        foreach (var name in new[] { $"DOTNET_ROOT_{arch.ToUpperInvariant()}", "DOTNET_ROOT" })
        {
            if (environment(name) is { Length: > 0 } root)
                yield return (root, name);
        }

        if (registered is { Length: > 0 })
            yield return (registered, "the installed .NET");
        yield return (x64OnArm64 ? Path.Combine(programFiles, "dotnet", "x64") : Path.Combine(programFiles, "dotnet"), "the installed .NET");
    }

    /// <summary>The first of the places that HandPeg can run on; null for none.</summary>
    public static DotNetRoot? Find(IEnumerable<(string Folder, string Where)> candidates)
    {
        foreach (var (folder, where) in candidates)
        {
            if (Usable(folder, where) is { } root)
                return root;
        }

        return null;
    }

    /// <summary>A folder as a root HandPeg can run on: its hostfxr.dll (the newest under host\fxr, else one beside it) and both frameworks of major 8; null otherwise.</summary>
    public static DotNetRoot? Usable(string folder, string where)
    {
        try
        {
            if (!Directory.Exists(folder) || !HasFramework(folder, "Microsoft.NETCore.App") || !HasFramework(folder, "Microsoft.WindowsDesktop.App"))
                return null;

            var newest = Newest(Path.Combine(folder, "host", "fxr"), v => true, dir => File.Exists(Path.Combine(dir, "hostfxr.dll")));
            var hostFxr = newest is not null ? Path.Combine(newest, "hostfxr.dll") : Path.Combine(folder, "hostfxr.dll");
            return File.Exists(hostFxr) ? new DotNetRoot(Path.GetFullPath(folder), Path.GetFullPath(hostFxr), where) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool HasFramework(string folder, string name) =>
        Newest(Path.Combine(folder, "shared", name), v => v.Major == Major, dir => Directory.EnumerateFileSystemEntries(dir).Any()) is not null;

    /// <summary>The subfolder of <paramref name="parent"/> named for the highest version that <paramref name="wanted"/> takes and <paramref name="holds"/> holds; null for none.</summary>
    private static string? Newest(string parent, Func<Version, bool> wanted, Func<string, bool> holds)
    {
        if (!Directory.Exists(parent))
            return null;

        return Directory.EnumerateDirectories(parent)
            .Select(dir => (Dir: dir, Version: VersionOf(Path.GetFileName(dir))))
            .Where(d => d.Version is not null && wanted(d.Version))
            .OrderByDescending(d => d.Version)
            .Select(d => d.Dir)
            .FirstOrDefault(holds);
    }

    /// <summary>A folder's name as a version ("8.0.33", "9.0.0-rc.2" as 9.0.0); null when it is none.</summary>
    public static Version? VersionOf(string name)
    {
        var dash = name.IndexOf('-');
        return Version.TryParse(dash > 0 ? name[..dash] : name, out var version) ? version : null;
    }
}
