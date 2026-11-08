using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace HandPegBootstrap;

/// <summary>A .NET root HandPeg can run on: the folder hostfxr is told is the root (its shared frameworks under it), and its hostfxr.dll.</summary>
internal sealed record DotNetRoot(string Folder, string HostFxr, string Where);

/// <summary>What kind of copy of HandPeg this HandPeg.exe belongs to, which decides where its own .NET goes.</summary>
internal enum CopyKind
{
    /// <summary>A portable zip's: a "portable" folder beside HandPeg.exe, where everything the copy writes stays.</summary>
    Portable,

    /// <summary>
    /// A copy made self-contained by portable.txt beside HandPeg.exe or by --portable (as HandPeg's AppPaths.FindLocalRoot reads them):
    /// everything it writes is beside the program, its own .NET too.
    /// </summary>
    SelfContained,

    /// <summary>An installed copy (Velopack's, or its portable archive): HandPeg.exe in a folder named "current" with Update.exe one up.</summary>
    Installed,

    /// <summary>Anything else: a publish folder copied somewhere.</summary>
    Other,
}

/// <summary>Where this copy keeps a .NET of its own (<paramref name="Folder"/>, a whole .NET root), and what it is called in a message.</summary>
internal sealed record OwnDotNet(CopyKind Kind, string Folder, string Where);

/// <summary>
/// Where a .NET 10 Desktop Runtime for this processor is looked for, in order (Pass 5.2.1; the copy's own root by its kind, Pass 5.3-Stage1):
/// this copy's own (<see cref="OwnRoot"/>: portable\dotnet for a portable zip, dotnet beside HandPeg.exe for a portable.txt or --portable
/// copy, the install folder's dotnet beside "current" for an installed copy, %LOCALAPPDATA%\HandPeg\dotnet for anything else; or
/// hostfxr.dll and shared\ beside HandPeg.exe, as 5.2.0's script left it), then DOTNET_ROOT_X64 / DOTNET_ROOT_ARM64 and DOTNET_ROOT, then
/// the one installed on the PC (where its installer registered it, else Program Files\dotnet; for an x64 copy on an ARM64 PC, Program
/// Files\dotnet\x64). A root counts only with a hostfxr.dll of this process's processor and both Microsoft.NETCore.App and
/// Microsoft.WindowsDesktop.App of a released version of major 10 (HandPeg.runtimeconfig.json asks for 10.0.0, rolling forward within 10,
/// and never to a preview). Only directories and file headers are looked at: no process, no network. Every root found is kept in order
/// (<see cref="AllUsable"/>), so that one hostfxr refuses passes to the next. An installed copy runs on the newer of its own .NET and the
/// PC's (<see cref="InOrder"/>, Pass 5.3-Stage2). Shared with the golden tests (portable-bootstrap), which build layouts on disk and ask.
/// </summary>
internal static class DotNetRoots
{
    /// <summary>The .NET major version HandPeg runs on (its TargetFramework's; the golden test checks they agree).</summary>
    public const int Major = 10;

    /// <summary>Where a portable zip's download unpacks .NET, under HandPeg.exe's folder.</summary>
    public static readonly string PortableFolder = Path.Combine("portable", "dotnet");

    /// <summary>Where a portable.txt or --portable copy's download unpacks .NET: beside HandPeg.exe, with that copy's deps, data and temp.</summary>
    public const string SelfContainedFolder = "dotnet";

    /// <summary>The file beside the program that makes a copy self-contained (HandPeg's AppPaths.PortableSentinel; the golden test checks they agree).</summary>
    public const string PortableSentinel = "portable.txt";

    /// <summary>The argument that does the same (HandPeg's AppPaths.PortableArgument), in any letter case.</summary>
    public const string PortableArgument = "--portable";

    /// <summary>This process's processor, as Microsoft's file names have it ("x64", "arm64").</summary>
    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>This process's processor as a PE header names it (0xAA64 ARM64, 0x8664 x64): the only hostfxr.dll it can load.</summary>
    public static ushort ProcessMachine => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? (ushort)0xAA64 : (ushort)0x8664;

    /// <summary>
    /// The kind of copy HandPeg.exe in <paramref name="app"/> belongs to, and the root its own .NET goes in, first match winning, in the
    /// order HandPeg's AppPaths.FindLocalRoot decides where a copy keeps what it writes: a "portable" folder beside it (a portable zip:
    /// portable\dotnet, as in 5.2.1; it is also what makes HandPeg keep everything in that folder); portable.txt beside it, or --portable
    /// among <paramref name="arguments"/> (the very list HandPeg is given): a copy that keeps everything beside the program (deps, data,
    /// temp), so dotnet beside it too, never in %LOCALAPPDATA% and never in a "portable" folder, which would move that copy's data into it;
    /// a folder named "current" with Update.exe one up (Velopack's install, or its portable archive: dotnet beside "current", which an
    /// update leaves alone and the uninstaller takes away); else %LOCALAPPDATA%\HandPeg\dotnet. An installed copy's own .NET is never put
    /// in "current\portable": that folder would turn it into a portable copy. The file system is asked through
    /// <paramref name="directoryExists"/> and <paramref name="fileExists"/> (the golden tests' own layouts).
    /// </summary>
    public static OwnDotNet OwnRoot(string app, IReadOnlyList<string> arguments, Func<string, bool> directoryExists, Func<string, bool> fileExists, string localAppData)
    {
        var folder = Path.TrimEndingDirectorySeparator(app);
        if (directoryExists(Path.Combine(folder, "portable")))
            return new OwnDotNet(CopyKind.Portable, Path.Combine(folder, PortableFolder), "this copy's portable\\dotnet");

        if (fileExists(Path.Combine(folder, PortableSentinel)) || arguments.Any(a => a.Equals(PortableArgument, StringComparison.OrdinalIgnoreCase)))
            return new OwnDotNet(CopyKind.SelfContained, Path.Combine(folder, SelfContainedFolder), "this copy's dotnet");

        if (string.Equals(Path.GetFileName(folder), "current", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(folder) is { Length: > 0 } installed && fileExists(Path.Combine(installed, "Update.exe")))
            return new OwnDotNet(CopyKind.Installed, Path.Combine(installed, "dotnet"), "HandPeg's own .NET");

        return new OwnDotNet(CopyKind.Other, Path.Combine(localAppData, "HandPeg", "dotnet"), "HandPeg's own .NET");
    }

    /// <summary>
    /// The places to look, in order, with what each is called in an error: this copy's own root and its own folder, then the
    /// environment's, then the installed one. <paramref name="system"/> false leaves out all but this copy's own (a test standing in
    /// for a PC with no .NET).
    /// </summary>
    public static IEnumerable<(string Folder, string Where)> Candidates(OwnDotNet own, string app, bool system, Func<string, string?> environment, string? registered,
        string programFiles, string arch, bool x64OnArm64)
    {
        yield return (own.Folder, own.Where);
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
    public static DotNetRoot? Find(IEnumerable<(string Folder, string Where)> candidates) => AllUsable(candidates).FirstOrDefault();

    /// <summary>
    /// Every one of the places that HandPeg can run on, in order, each root once (DOTNET_ROOT and the registered location may both name
    /// Program Files\dotnet): the first is the one to run on, and the rest are where to go when hostfxr refuses it (a root that looks whole
    /// but cannot start). Looked at lazily, one place at a time, so a start that runs on the first looks no further.
    /// </summary>
    public static IEnumerable<DotNetRoot> AllUsable(IEnumerable<(string Folder, string Where)> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, where) in candidates)
        {
            if (Usable(folder, where) is { } root && seen.Add(Path.TrimEndingDirectorySeparator(root.Folder)))
                yield return root;
        }
    }

    /// <summary>
    /// Every usable root in the order to try (Pass 5.3-Stage2, the user's answer): an installed copy, and any other copy that is not a
    /// portable one (its own root is %LOCALAPPDATA%\HandPeg\dotnet, which installed copies share), runs on whichever is newer of its own
    /// .NET (<paramref name="own"/>'s root, or 5.2.0's layout in <paramref name="app"/>) and the PC's first (DOTNET_ROOT_&lt;ARCH&gt;,
    /// DOTNET_ROOT, the registered .NET, Program Files), its own on a tie: a .NET the PC's updates keep patched is taken over the copy
    /// HandPeg downloaded for itself once it is newer. A portable zip and a portable.txt or --portable copy keep their own first (they
    /// carry it). The rest follow in their order, each once, as where to go when hostfxr refuses one. Looked at lazily: no further than
    /// the PC's first root.
    /// </summary>
    public static IEnumerable<DotNetRoot> InOrder(OwnDotNet own, string app, IEnumerable<(string Folder, string Where)> candidates)
    {
        using var roots = AllUsable(candidates).GetEnumerator();
        if (own.Kind is CopyKind.Portable or CopyKind.SelfContained)
        {
            while (roots.MoveNext())
                yield return roots.Current;
            yield break;
        }

        // The candidates list this copy's own places first: those found are held back until the PC's first root is found (or none is).
        var ownFolders = new[] { own.Folder, app }.Select(Comparable).ToArray();
        var held = new List<DotNetRoot>();
        DotNetRoot? pc = null;
        while (pc is null && roots.MoveNext())
        {
            if (ownFolders.Contains(Comparable(roots.Current.Folder), StringComparer.OrdinalIgnoreCase))
                held.Add(roots.Current);
            else
                pc = roots.Current;
        }

        if (pc is not null && held.Count > 0 && RuntimeVersion(pc.Folder) is { } theirs && (RuntimeVersion(held[0].Folder) is not { } mine || theirs > mine))
        {
            yield return pc;
            pc = null;
        }

        foreach (var root in held)
            yield return root;
        if (pc is not null)
            yield return pc;
        while (roots.MoveNext())
            yield return roots.Current;
    }

    /// <summary>
    /// The .NET a root runs HandPeg on: the lower of its newest released 10.x Microsoft.NETCore.App and Microsoft.WindowsDesktop.App
    /// (hostfxr takes each framework's newest patch; a Desktop Runtime puts both at one version). Null when either is missing.
    /// </summary>
    public static Version? RuntimeVersion(string folder)
    {
        try
        {
            if (FrameworkFolder(folder, "Microsoft.NETCore.App") is not { } core || FrameworkFolder(folder, "Microsoft.WindowsDesktop.App") is not { } desktop
                || VersionOf(Path.GetFileName(core)) is not { } coreVersion || VersionOf(Path.GetFileName(desktop)) is not { } desktopVersion)
                return null;
            return coreVersion < desktopVersion ? coreVersion : desktopVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // A folder as roots are compared: full, without a trailing separator (in any letter case, by the caller).
    private static string Comparable(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>
    /// A folder as a root HandPeg can run on: its hostfxr.dll (the newest under host\fxr, else one beside it), built for this process's
    /// processor (the x64 and ARM64 installers share %LOCALAPPDATA%\HandPeg, so the other's .NET may be there), and both frameworks of
    /// major 10 as released versions; null otherwise.
    /// </summary>
    public static DotNetRoot? Usable(string folder, string where)
    {
        try
        {
            if (!Directory.Exists(folder) || !HasFramework(folder, "Microsoft.NETCore.App") || !HasFramework(folder, "Microsoft.WindowsDesktop.App"))
                return null;

            var newest = Newest(Path.Combine(folder, "host", "fxr"), v => true, dir => File.Exists(Path.Combine(dir, "hostfxr.dll")));
            var hostFxr = newest is not null ? Path.Combine(newest, "hostfxr.dll") : Path.Combine(folder, "hostfxr.dll");
            return File.Exists(hostFxr) && MachineOf(hostFxr) == ProcessMachine ? new DotNetRoot(Path.GetFullPath(folder), Path.GetFullPath(hostFxr), where) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// What tells a .NET in a folder from another put in the same folder later (a root hostfxr refused is known by it, so a download that
    /// has since replaced it is not taken for it): its hostfxr.dll's path and when that file was made. A download unpacks every file anew in
    /// a folder of its own and moving that folder into place keeps the time, so a replacement always has another. Empty when it cannot be read.
    /// </summary>
    public static string StampOf(DotNetRoot root)
    {
        try
        {
            return root.HostFxr + "|" + File.GetCreationTimeUtc(root.HostFxr).Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    /// <summary>A PE file's machine from its header (0x8664 x64, 0xAA64 ARM64); 0 when the file is no PE file.</summary>
    public static ushort MachineOf(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        Span<byte> header = stackalloc byte[64];
        if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || header[0] != (byte)'M' || header[1] != (byte)'Z')
            return 0;

        // e_lfanew: where the "PE\0\0" signature is, the machine right after it.
        var pe = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        if (pe < header.Length)
            return 0;

        file.Position = pe;
        Span<byte> signature = stackalloc byte[6];
        if (file.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) < signature.Length
            || signature[0] != (byte)'P' || signature[1] != (byte)'E' || signature[2] != 0 || signature[3] != 0)
            return 0;
        return BinaryPrimitives.ReadUInt16LittleEndian(signature[4..]);
    }

    // A framework of major 10 in a released version's folder ("10.0.14") with something in it. A preview's or release candidate's
    // ("10.0.0-rc.2.25502.107") never counts: HandPeg.runtimeconfig.json asks for the release 10.0.0, which hostfxr never rolls forward to a
    // pre-release of (10.0.0-rc.2 is lower than 10.0.0), so a root holding only such frameworks would fail every start. (A hostfxr of any
    // version will do: its folder's name is read as its version, dash or not.)
    private static bool HasFramework(string folder, string name) => FrameworkFolder(folder, name) is not null;

    // The folder of the newest such version of a framework; null for none.
    private static string? FrameworkFolder(string folder, string name) =>
        Newest(Path.Combine(folder, "shared", name), v => v.Major == Major,
            dir => !Path.GetFileName(dir).Contains('-') && Directory.EnumerateFileSystemEntries(dir).Any());

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

    /// <summary>A folder's name as a version ("10.0.14", "9.0.0-rc.2" as 9.0.0); null when it is none.</summary>
    public static Version? VersionOf(string name)
    {
        var dash = name.IndexOf('-');
        return Version.TryParse(dash > 0 ? name[..dash] : name, out var version) ? version : null;
    }
}
