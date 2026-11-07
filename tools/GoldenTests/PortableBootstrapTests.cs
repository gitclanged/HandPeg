using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using HandPegBootstrap;

namespace HandPegGoldenTests;

/// <summary>
/// portable-bootstrap (Pass 5.2.1; Pass 5.3-Stage1: .NET 10, and the installers' HandPeg.exe too): HandPeg.exe of the portable zips and
/// both Setup installers, which runs HandPeg.dll on a .NET 10 Desktop Runtime it finds, or gets one the user chooses (tools/PortableBootstrap).
/// Where it looks, in order, on layouts made on disk (this copy's own root by its kind, 5.2.0's hostfxr.dll and shared\ beside it,
/// DOTNET_ROOT_&lt;ARCH&gt; then DOTNET_ROOT, the installed one, Program Files\dotnet\x64 for an x64 copy on an ARM64 PC), and every usable
/// root in order, each once (where it goes when hostfxr refuses one); what counts as a root (both frameworks of major 10 as released
/// versions and a hostfxr of this processor, the newest); each kind of copy's own root (portable.txt and --portable included) and where a
/// download goes (never a "portable" folder in an installed copy), each attempt in a folder of its own under a lock per root, what a later
/// one clears, and a .NET already there moved aside, never deleted where it is nor while in use; which of Microsoft's files it takes from a
/// release list (released versions only, https and a SHA-512); what it does with no .NET (an installer's hook and a background process end
/// quietly, anything else asks); what Microsoft's installer's exit codes mean; that its .NET is the one HandPeg is built for and its
/// portable marks HandPeg's own; when Explorer's thumbnail handler has a .NET it can start on.
/// And, where they have been published (Release.ps1), that both HandPeg.exe builds are native programs of their processor: no .NET header,
/// a window program, small. Every line says what it proved; the tables show each answer.
/// </summary>
public static class PortableBootstrapTests
{
    public static GraphResult Run()
    {
        var text = new StringBuilder("# HandPeg.exe of the portable zips and both installers (5.3.0): .NET 10 found (this copy's own first) or got as the user chooses; no script.\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        void Note(string line) => text.Append("  ").Append(line).Append('\n');

        var scratch = Path.Combine(Path.GetTempPath(), "handpeg-bootstrap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Roots(Expect, scratch);
            Copies(Expect, scratch);
            Replacing(Expect, scratch);
            Releases(Expect);
            Installers(Expect);
            Decisions(Expect, Note);
            Outcomes(Expect, Note);
            Constants(Expect);
            HandlerRuntimes(Expect, scratch);
            Programs(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }

        return new GraphResult("portable-bootstrap", text.ToString(), problems, 0);
    }

    // A .NET root made of stand-in files: host\fxr\<fxr>\hostfxr.dll (or hostfxr.dll at its top) holding only a PE header of this process's
    // processor, and each framework's folder with a file in it.
    private static string Root(string folder, string? fxr, params string[] frameworks) => RootFor(DotNetRoots.ProcessMachine, folder, fxr, frameworks);

    // The same with a hostfxr.dll of <machine>.
    private static string RootFor(ushort machine, string folder, string? fxr, params string[] frameworks)
    {
        Directory.CreateDirectory(folder);
        if (fxr == "beside")
        {
            File.WriteAllBytes(Path.Combine(folder, "hostfxr.dll"), PeStub(machine));
        }
        else if (fxr is not null)
        {
            Directory.CreateDirectory(Path.Combine(folder, "host", "fxr", fxr));
            File.WriteAllBytes(Path.Combine(folder, "host", "fxr", fxr, "hostfxr.dll"), PeStub(machine));
        }

        foreach (var framework in frameworks)
        {
            var (name, version) = (framework[..framework.IndexOf(' ')], framework[(framework.IndexOf(' ') + 1)..]);
            var at = Path.Combine(folder, "shared", name, version);
            Directory.CreateDirectory(at);
            File.WriteAllText(Path.Combine(at, "coreclr.dll"), "");
        }

        return folder;
    }

    // A file that is only a PE header: "MZ", e_lfanew (at 0x3C) pointing just past the 64-byte DOS header, "PE\0\0" and the machine.
    private static byte[] PeStub(ushort machine)
    {
        var bytes = new byte[0x46];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x40);
        bytes[0x40] = (byte)'P';
        bytes[0x41] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x44), machine);
        return bytes;
    }

    // The other processor's machine: a hostfxr.dll this process could not load.
    private static ushort OtherMachine => DotNetRoots.ProcessMachine == 0xAA64 ? (ushort)0x8664 : (ushort)0xAA64;

    private const string Core10 = "Microsoft.NETCore.App 10.0.14", Desktop10 = "Microsoft.WindowsDesktop.App 10.0.14";

    private static void Roots(Action<bool, string> expect, string scratch)
    {
        var empty = Root(Path.Combine(scratch, "empty"), null);
        expect(DotNetRoots.Usable(empty, "x") is null, "an empty folder is no root");
        var noDesktop = Root(Path.Combine(scratch, "core-only"), "10.0.14", Core10);
        expect(DotNetRoots.Usable(noDesktop, "x") is null, "nor a .NET with no Windows Desktop runtime");
        var eight = Root(Path.Combine(scratch, "eight"), "8.0.33", "Microsoft.NETCore.App 8.0.33", "Microsoft.WindowsDesktop.App 8.0.33");
        var eleven = Root(Path.Combine(scratch, "eleven"), "11.0.0", "Microsoft.NETCore.App 11.0.0", "Microsoft.WindowsDesktop.App 11.0.0");
        expect(DotNetRoots.Usable(eight, "x") is null && DotNetRoots.Usable(eleven, "x") is null,
            "nor one of another major version: HandPeg asks for 10 (a PC with only .NET 8, or only 11, has none)");
        var noFxr = Root(Path.Combine(scratch, "no-fxr"), null, Core10, Desktop10);
        expect(DotNetRoots.Usable(noFxr, "x") is null, "nor one with no hostfxr.dll");
        var emptyFramework = Root(Path.Combine(scratch, "hollow"), "10.0.14", Core10);
        Directory.CreateDirectory(Path.Combine(emptyFramework, "shared", "Microsoft.WindowsDesktop.App", "10.0.14"));
        expect(DotNetRoots.Usable(emptyFramework, "x") is null, "nor one whose framework folder is empty (a download cut off)");
        var foreign = RootFor(OtherMachine, Path.Combine(scratch, "foreign"), "10.0.14", Core10, Desktop10);
        expect(DotNetRoots.Usable(foreign, "x") is null && DotNetRoots.MachineOf(Path.Combine(foreign, "host", "fxr", "10.0.14", "hostfxr.dll")) == OtherMachine,
            "nor one whose hostfxr.dll is built for the other processor (the x64 and ARM64 installers share %LOCALAPPDATA%\\HandPeg)");
        var notProgram = Root(Path.Combine(scratch, "not-a-program"), "10.0.14", Core10, Desktop10);
        File.WriteAllText(Path.Combine(notProgram, "host", "fxr", "10.0.14", "hostfxr.dll"), "");
        expect(DotNetRoots.Usable(notProgram, "x") is null && DotNetRoots.MachineOf(Path.Combine(notProgram, "host", "fxr", "10.0.14", "hostfxr.dll")) == 0,
            "nor one whose hostfxr.dll is no program at all");
        const string Candidate = "10.0.0-rc.2.25502.107";
        var preview = Root(Path.Combine(scratch, "preview-only"), Candidate, $"Microsoft.NETCore.App {Candidate}", $"Microsoft.WindowsDesktop.App {Candidate}");
        expect(DotNetRoots.Usable(preview, "x") is null,
            "nor one whose only .NET 10 frameworks are a release candidate's (10.0.0-rc.2): HandPeg asks for the release 10.0.0, which hostfxr never rolls forward to a preview");
        Root(preview, null, Core10, Desktop10);
        expect(DotNetRoots.Usable(preview, "x") is { } released && released.HostFxr.EndsWith(Path.Combine("fxr", Candidate, "hostfxr.dll"), StringComparison.OrdinalIgnoreCase),
            "with released 10.0.14 frameworks beside them it is one (any hostfxr will do, a preview's too)");

        var many = Root(Path.Combine(scratch, "many"), "10.0.1", Core10, Desktop10, "Microsoft.NETCore.App 10.0.1", "Microsoft.WindowsDesktop.App 10.0.1",
            "Microsoft.NETCore.App 8.0.33", "Microsoft.WindowsDesktop.App 8.0.33");
        Root(many, "10.0.14");
        Root(many, "9.0.0-rc.2");
        var found = DotNetRoots.Usable(many, "x");
        expect(found is not null && found.HostFxr.EndsWith(Path.Combine("fxr", "10.0.14", "hostfxr.dll"), StringComparison.OrdinalIgnoreCase),
            "a root with several hostfxr versions runs on the newest (as .NET's own host does)");
        expect(DotNetRoots.VersionOf("9.0.0-rc.2") == new Version(9, 0, 0) && DotNetRoots.VersionOf("current") is null,
            "a preview hostfxr's folder reads as its version (for hostfxr alone); a name that is none is passed over");

        // The order: this copy's own root (portable\dotnet once the zip's portable folder is there), then 5.2.0's layout beside HandPeg.exe,
        // then the environment's, then the installed one.
        var app = Path.Combine(scratch, "app");
        var local = Path.Combine(scratch, "app-local");
        var programFiles = Path.Combine(scratch, "Program Files");
        var system = Root(Path.Combine(programFiles, "dotnet"), "10.0.14", Core10, Desktop10);
        var registered = Root(Path.Combine(scratch, "registered"), "10.0.14", Core10, Desktop10);
        var arm = Root(Path.Combine(scratch, "env-arm64"), "10.0.14", Core10, Desktop10);
        var plain = Root(Path.Combine(scratch, "env"), "10.0.14", Core10, Desktop10);
        var environment = new Dictionary<string, string> { ["DOTNET_ROOT_ARM64"] = arm, ["DOTNET_ROOT"] = plain };
        string? Env(string name) => environment.GetValueOrDefault(name);
        DotNetRoot? Find(bool withSystem, string? registeredAt, string arch = "arm64", bool x64OnArm64 = false) =>
            DotNetRoots.Find(DotNetRoots.Candidates(DotNetRoots.OwnRoot(app, [], Directory.Exists, File.Exists, local), app, withSystem, Env, registeredAt, programFiles, arch, x64OnArm64));

        Directory.CreateDirectory(app);
        expect(Find(true, registered)?.Folder == Path.GetFullPath(arm), "a copy with no .NET of its own runs on DOTNET_ROOT_ARM64 first (an ARM64 copy)");
        environment.Remove("DOTNET_ROOT_ARM64");
        expect(Find(true, registered)?.Folder == Path.GetFullPath(plain), "then on DOTNET_ROOT");
        environment.Clear();
        expect(Find(true, registered) is { Where: "the installed .NET" } r && r.Folder == Path.GetFullPath(registered), "then on the .NET its installer registered");
        expect(Find(true, null)?.Folder == Path.GetFullPath(system), "then on Program Files\\dotnet");
        var x64 = Root(Path.Combine(programFiles, "dotnet", "x64"), "10.0.14", Core10, Desktop10);
        expect(Find(true, null, "x64", x64OnArm64: true)?.Folder == Path.GetFullPath(x64), "an x64 copy on an ARM64 PC: Program Files\\dotnet\\x64, the x64 .NET's place there");
        expect(Find(false, registered) is null, "looking only in the copy's own places (HANDPEG_BOOTSTRAP_NO_SYSTEM_DOTNET), none: it asks");

        Root(app, "beside", Core10, Desktop10);
        expect(Find(true, registered) is { Where: "this copy's folder" } legacy && legacy.HostFxr == Path.Combine(Path.GetFullPath(app), "hostfxr.dll"),
            "5.2.0's script's layout (hostfxr.dll and shared\\ beside HandPeg.exe) is this copy's own, before the PC's");
        var portable = Root(Path.Combine(app, DotNetRoots.PortableFolder), "10.0.14", Core10, Desktop10);
        expect(Find(true, registered) is { Where: "this copy's portable\\dotnet" } own && own.Folder == Path.GetFullPath(portable), "and portable\\dotnet, where a zip's download puts it, first of all");
    }

    private static void Copies(Action<bool, string> expect, string scratch)
    {
        var local = Path.Combine(scratch, "Local");
        OwnDotNet Own(string app) => DotNetRoots.OwnRoot(app, [], Directory.Exists, File.Exists, local);
        OwnDotNet OwnWith(string app, params string[] arguments) => DotNetRoots.OwnRoot(app, arguments, Directory.Exists, File.Exists, local);
        string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine(parts)).FullName;
        void Mark(string folder, string name) => File.WriteAllText(Path.Combine(folder, name), "");

        // Velopack's install: %LOCALAPPDATA%\HandPeg\{Update.exe, current\HandPeg.exe}.
        var install = Folder(local, "HandPeg");
        var current = Folder(install, "current");
        Mark(install, "Update.exe");
        var installed = Own(current);
        expect(installed is { Kind: CopyKind.Installed } && installed.Folder == Path.Combine(install, "dotnet") && Own(current + Path.DirectorySeparatorChar).Folder == installed.Folder,
            "an installed copy (current\\HandPeg.exe, Update.exe one up): its own .NET is dotnet beside current, which an update leaves and the uninstaller takes");
        var custom = Folder(scratch, "Apps", "HandPeg");
        Mark(custom, "Update.exe");
        expect(Own(Folder(custom, "Current")) is { Kind: CopyKind.Installed } elsewhere && elsewhere.Folder == Path.Combine(custom, "dotnet"),
            "so is one installed elsewhere, its folder named Current in any case");
        var carried = Folder(scratch, "Carry", "HandPeg");
        Mark(carried, "Update.exe");
        Mark(carried, ".portable");
        expect(Own(Folder(carried, "current")) is { Kind: CopyKind.Installed } velopackPortable && velopackPortable.Folder == Path.Combine(carried, "dotnet"),
            "Velopack's portable archive (.portable and Update.exe beside current): dotnet beside current, which travels with the folder");
        var loose = Folder(scratch, "Loose", "current");
        expect(Own(loose) is { Kind: CopyKind.Other } looseOwn && looseOwn.Folder == Path.Combine(local, "HandPeg", "dotnet"),
            "a folder named current with no Update.exe one up is no install: %LOCALAPPDATA%\\HandPeg\\dotnet");
        var zip = Folder(scratch, "Zip");
        Folder(zip, "portable");
        expect(Own(zip) is { Kind: CopyKind.Portable } zipOwn && zipOwn.Folder == Path.Combine(zip, "portable", "dotnet"),
            "a portable zip (its portable folder beside HandPeg.exe): portable\\dotnet");
        var odd = Folder(scratch, "Odd", "HandPeg");
        Mark(odd, "Update.exe");
        var oddCurrent = Folder(odd, "current");
        Folder(oddCurrent, "portable");
        expect(Own(oddCurrent) is { Kind: CopyKind.Portable } oddOwn && oddOwn.Folder == Path.Combine(oddCurrent, "portable", "dotnet"),
            "a portable folder wins even in an install's current (it is what makes HandPeg keep everything there)");
        var publish = Folder(scratch, "Publish");
        var other = Own(publish);
        expect(other is { Kind: CopyKind.Other } && other.Folder == Path.Combine(local, "HandPeg", "dotnet"), "anything else (a publish copied somewhere): %LOCALAPPDATA%\\HandPeg\\dotnet");

        // portable.txt beside HandPeg.exe, or --portable among its arguments (as HandPeg's own AppPaths reads them): everything the copy
        // writes is beside the program, its own .NET too.
        static bool InPortable(string path, string app)
        {
            var portable = Path.Combine(app, "portable");
            return path.Equals(portable, StringComparison.OrdinalIgnoreCase) || path.StartsWith(portable + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static string Target(OwnDotNet copy) => PortableDotNet.DownloadTarget(copy);
        var attempt = new Guid("0123456789abcdef0123456789abcdef");
        var sentinel = Folder(scratch, "Sentinel");
        Mark(sentinel, DotNetRoots.PortableSentinel);
        var bySentinel = Own(sentinel);
        var sentinelPlan = PortableDotNet.DownloadPlan(Target(bySentinel), attempt);
        expect(bySentinel is { Kind: CopyKind.SelfContained } && Target(bySentinel) == Path.Combine(sentinel, "dotnet") && !InPortable(Target(bySentinel), sentinel)
               && Path.GetDirectoryName(sentinelPlan.Work) == sentinel && Path.GetDirectoryName(sentinelPlan.Aside) == sentinel,
            "a copy with portable.txt beside HandPeg.exe keeps its own .NET beside it (dotnet, by its deps, data and temp), downloaded beside it: "
            + "never in %LOCALAPPDATA%, never in a portable folder (which would move that copy's data into it)");
        var flagged = Folder(scratch, "Flagged");
        expect(OwnWith(flagged, @"C:\Videos\Quick cuts.hproj", "--PORTABLE") is { Kind: CopyKind.SelfContained } byArgument && Target(byArgument) == Path.Combine(flagged, "dotnet")
               && Own(flagged) is { Kind: CopyKind.Other },
            "so does a copy started with --portable (in any letter case, anywhere among its arguments); the same folder without it is any other copy");
        expect(OwnWith(current, "--portable") is { Kind: CopyKind.SelfContained } installedFlag && Target(installedFlag) == Path.Combine(current, "dotnet")
               && Own(zip) is { Kind: CopyKind.Portable } && OwnWith(zip, "--portable") is { Kind: CopyKind.Portable },
            "in AppPaths' order: --portable comes before an install's current (where that copy then keeps its data), a portable folder before both");

        // Where a download goes (PortableDotNet.DownloadTarget, the root Program gives Install): the copy's own root; for an installed or any
        // other copy never the program's portable folder (which would make it a portable copy).
        expect(Target(installed) == Path.Combine(install, "dotnet") && !InPortable(Target(installed), current) && Target(Own(current + Path.DirectorySeparatorChar)) == Target(installed)
               && Target(other) == Path.Combine(local, "HandPeg", "dotnet") && !InPortable(Target(other), publish),
            "an installed copy downloads into dotnet beside current, any other copy into %LOCALAPPDATA%\\HandPeg\\dotnet: never into the program's portable folder");
        expect(Target(Own(zip)) == Path.Combine(zip, "portable", "dotnet"), "a portable zip downloads into portable\\dotnet, as in 5.2.1");

        // Each attempt in a folder of its own beside the root, under a lock per root; what a later one clears is only what attempts left.
        var root = Target(installed);
        var (planned, work, aside) = PortableDotNet.DownloadPlan(root, attempt);
        var another = PortableDotNet.DownloadPlan(root, Guid.NewGuid());
        expect(planned == root && work == root + "-download-0123456789abcdef0123456789abcdef" && aside == root + "-old-0123456789abcdef0123456789abcdef"
               && Path.GetDirectoryName(work) == install && Path.GetDirectoryName(aside) == install && another.Work != work && another.Aside != aside,
            "each attempt downloads in a folder of its own beside the root (dotnet-download-<attempt>) and moves a .NET already there aside beside it "
            + "(dotnet-old-<attempt>): two attempts never share one, and putting it in place is a rename");
        string Beside(string name) => Path.Combine(install, name);
        expect(PortableDotNet.IsLeftOver(root, work) && PortableDotNet.IsLeftOver(root, aside) && PortableDotNet.IsLeftOver(root, Beside("dotnet-download"))
               && PortableDotNet.IsLeftOver(root, Beside("DOTNET-OLD-0123456789ABCDEF0123456789ABCDEF"))
               && !PortableDotNet.IsLeftOver(root, root) && !PortableDotNet.IsLeftOver(root, current) && !PortableDotNet.IsLeftOver(root, Beside("dotnet-old-backup"))
               && !PortableDotNet.IsLeftOver(root, Beside("dotnet-download-0123")) && !PortableDotNet.IsLeftOver(root, Beside("packages-download-0123456789abcdef0123456789abcdef"))
               && !PortableDotNet.IsLeftOver(root, Path.Combine(scratch, "Elsewhere", "dotnet-download-0123456789abcdef0123456789abcdef")),
            "a later download clears beside the root only what downloads into it left (dotnet-download-<attempt>, dotnet-old-<attempt>, 5.2.1's dotnet-download): "
            + "never the root, current, another name or another folder's");
        var gate = PortableDotNet.MutexName(root);
        expect(gate == PortableDotNet.MutexName(root.ToUpperInvariant()) && gate == PortableDotNet.MutexName(root + Path.DirectorySeparatorChar)
               && gate != PortableDotNet.MutexName(Target(Own(zip))) && gate.StartsWith("Local\\HandPeg.DotNet.", StringComparison.Ordinal)
               && gate.Length == "Local\\HandPeg.DotNet.".Length + 16 && gate["Local\\".Length..].IndexOf('\\') < 0,
            "one download at a time into a root: its lock is named for the root (the same in any letter case or with a trailing separator, another "
            + "root's another), for this session, with no backslash past Local\\");
        expect(Target(other) == root && PortableDotNet.MutexName(Target(other)) == gate,
            "an installed copy and any other copy share %LOCALAPPDATA%\\HandPeg\\dotnet by default (the x64 and ARM64 ones too), and so share its lock");

        // An installed copy's own .NET before everything of the PC's; one the other processor's installer left there passed over.
        var ownRoot = Root(installed.Folder, "10.0.14", Core10, Desktop10);
        var environmentRoot = Root(Path.Combine(scratch, "env-copies"), "10.0.14", Core10, Desktop10);
        var registered = Root(Path.Combine(scratch, "registered-copies"), "10.0.14", Core10, Desktop10);
        var programFiles = Path.Combine(scratch, "PF-copies");
        Root(Path.Combine(programFiles, "dotnet"), "10.0.14", Core10, Desktop10);
        string? Env(string name) => (name is "DOTNET_ROOT_ARM64" or "DOTNET_ROOT") ? environmentRoot : null;
        DotNetRoot? Find() => DotNetRoots.Find(DotNetRoots.Candidates(installed, current, true, Env, registered, programFiles, "arm64", false));
        expect(Find() is { } first && first.Folder == Path.GetFullPath(ownRoot) && first.Where == installed.Where,
            "an installed copy runs on its own .NET first, before DOTNET_ROOT_<ARCH>, DOTNET_ROOT, the registered .NET and Program Files\\dotnet");

        // Every root found, in order, each once: HandPeg.exe goes to the next when hostfxr refuses one (a root that looks whole but is damaged).
        var machineDotNet = Path.Combine(programFiles, "dotnet");
        var all = DotNetRoots.AllUsable(DotNetRoots.Candidates(installed, current, true, name => name == "DOTNET_ROOT" ? machineDotNet : null,
            machineDotNet + Path.DirectorySeparatorChar, programFiles, "arm64", false)).ToList();
        expect(all.Count == 2 && all[0].Folder == Path.GetFullPath(ownRoot) && all[1].Folder == Path.GetFullPath(machineDotNet) && all[1].Where == "DOTNET_ROOT",
            "every usable root in order, each once (DOTNET_ROOT, the registered location and Program Files naming one .NET are one): HandPeg runs on the first, "
            + "and on the next when hostfxr refuses it");
        File.WriteAllBytes(Path.Combine(ownRoot, "host", "fxr", "10.0.14", "hostfxr.dll"), PeStub(OtherMachine));
        expect(Find()?.Folder == Path.GetFullPath(environmentRoot), "its own .NET of the other processor (the other installer's) is passed over, and a download replaces it");
    }

    private static void Replacing(Action<bool, string> expect, string scratch)
    {
        // A download put in place of a .NET already there (PortableDotNet.PutInPlace): the old one is moved aside whole, never deleted where
        // it is; left as it was when it is in use or cannot be moved; put back when the new one cannot go in.
        static string Tree(string folder, string mark)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, mark), "");
            return folder;
        }

        static bool Refused(Action put)
        {
            try
            {
                put();
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        var root = Tree(Path.Combine(scratch, "replace", "dotnet"), "old.txt");
        var (_, work, aside) = PortableDotNet.DownloadPlan(root, Guid.NewGuid());
        var unpacked = Tree(Path.Combine(work, "dotnet"), "new.txt");
        PortableDotNet.PutInPlace(unpacked, root, aside);
        expect(File.Exists(Path.Combine(root, "new.txt")) && !File.Exists(Path.Combine(root, "old.txt")) && File.Exists(Path.Combine(aside, "old.txt")) && !Directory.Exists(unpacked),
            "a download goes in place of the .NET there, which is moved aside whole (and deleted after, best effort), never deleted where it is");

        // In use: its coreclr.dll held open, as a running HandPeg holds it (the other processor's copy shares the root).
        var busy = Root(Path.Combine(scratch, "replace-busy", "dotnet"), "10.0.14", Core10, Desktop10);
        var (_, busyWork, busyAside) = PortableDotNet.DownloadPlan(busy, Guid.NewGuid());
        var busyNew = Tree(Path.Combine(busyWork, "dotnet"), "new.txt");
        var inUse = false;
        using (new FileStream(Path.Combine(busy, "shared", "Microsoft.NETCore.App", "10.0.14", "coreclr.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            inUse = Refused(() => PortableDotNet.PutInPlace(busyNew, busy, busyAside));
        expect(inUse && DotNetRoots.Usable(busy, "x") is not null && !Directory.Exists(busyAside) && Directory.Exists(busyNew),
            "a .NET in use (a HandPeg runs on it) is neither moved nor deleted: the download stops with a message and leaves it as it was");

        var stuck = Root(Path.Combine(scratch, "replace-stuck", "dotnet"), "10.0.14", Core10, Desktop10);
        var (_, stuckWork, stuckAside) = PortableDotNet.DownloadPlan(stuck, Guid.NewGuid());
        var stuckNew = Tree(Path.Combine(stuckWork, "dotnet"), "new.txt");
        Tree(stuckAside, "in-the-way.txt");
        expect(Refused(() => PortableDotNet.PutInPlace(stuckNew, stuck, stuckAside)) && DotNetRoots.Usable(stuck, "x") is not null && Directory.Exists(stuckNew),
            "one that cannot be moved aside is left as it was");

        var back = Root(Path.Combine(scratch, "replace-back", "dotnet"), "10.0.14", Core10, Desktop10);
        var (_, backWork, backAside) = PortableDotNet.DownloadPlan(back, Guid.NewGuid());
        expect(Refused(() => PortableDotNet.PutInPlace(Path.Combine(backWork, "dotnet"), back, backAside)) && DotNetRoots.Usable(back, "x") is not null
               && !Directory.Exists(backAside),
            "and one moved aside is put back when the new one cannot go in");

        // A root hostfxr refused is known by its stamp, so the download replaces it although it looks whole, yet takes one another HandPeg
        // has put in its place since (here: the refused one made an hour ago, its replacement now).
        static string Stamp(string folder) => DotNetRoots.Usable(folder, "x") is { } found ? DotNetRoots.StampOf(found) : "";
        var refusedRoot = Root(Path.Combine(scratch, "stamp", "dotnet"), "10.0.14", Core10, Desktop10);
        File.SetCreationTimeUtc(Path.Combine(refusedRoot, "host", "fxr", "10.0.14", "hostfxr.dll"), DateTime.UtcNow.AddHours(-1));
        var stamp = Stamp(refusedRoot);
        var (_, stampWork, stampAside) = PortableDotNet.DownloadPlan(refusedRoot, Guid.NewGuid());
        var replacement = Root(Path.Combine(stampWork, "dotnet"), "10.0.14", Core10, Desktop10);
        var again = Stamp(refusedRoot);
        PortableDotNet.PutInPlace(replacement, refusedRoot, stampAside);
        expect(stamp.Length > 0 && again == stamp && Stamp(refusedRoot) is { Length: > 0 } now && now != stamp,
            "a .NET hostfxr refused is known by its stamp (its hostfxr.dll and when that was made): read again it is the same, and a download put in its place has another");

        // A refused .NET this process keeps loaded (.NET's host pins hostpolicy.dll) cannot be moved aside by it: the download waits beside
        // it as <root>-pending-<attempt> and the next start puts it in place before it looks at any .NET (PortableDotNet.FinishPending).
        var pinnedRoot = Root(Path.Combine(scratch, "pending", "dotnet"), "10.0.14", Core10, Desktop10);
        var waiting = PortableDotNet.PendingFolder(pinnedRoot, Guid.NewGuid());
        Root(waiting, "10.0.14", Core10, Desktop10);
        File.WriteAllText(Path.Combine(waiting, "new.txt"), "the download");
        var halfWay = PortableDotNet.PendingFolder(pinnedRoot, Guid.NewGuid());
        Directory.CreateDirectory(halfWay);
        var movedAside = PortableDotNet.DownloadPlan(pinnedRoot, Guid.NewGuid()).Aside;
        Directory.CreateDirectory(movedAside);
        expect(PortableDotNet.IsPending(pinnedRoot, waiting) && !PortableDotNet.IsPending(pinnedRoot, pinnedRoot)
               && !PortableDotNet.IsPending(pinnedRoot, Path.Combine(scratch, "pending", "dotnet-pending-0123")) && !PortableDotNet.IsLeftOver(pinnedRoot, waiting),
            "a download waiting for a new start is named for its root (dotnet-pending-<attempt>), and no later download's clean-up takes it for a leftover");
        PortableDotNet.FinishPending(pinnedRoot);
        expect(File.Exists(Path.Combine(pinnedRoot, "new.txt")) && DotNetRoots.Usable(pinnedRoot, "x") is not null && !Directory.Exists(waiting) && !Directory.Exists(halfWay) && !Directory.Exists(movedAside)
               && !Directory.EnumerateDirectories(Path.Combine(scratch, "pending")).Any(folder => PortableDotNet.IsLeftOver(pinnedRoot, folder)),
            "the next start puts the waiting download in place first (the old .NET moved aside and deleted), and deletes a waiting folder that is not whole and a .NET an earlier start moved aside");
        PortableDotNet.FinishPending(pinnedRoot);
        expect(File.Exists(Path.Combine(pinnedRoot, "new.txt")), "with nothing waiting, a start changes nothing");
    }

    private static void Releases(Action<bool, string> expect)
    {
        static string ZipEntry(string prefix, string rid, string url = "https://builds.dotnet.microsoft.com/x.zip", int hash = 128, char digit = 'a') =>
            $$"""{ "name": "{{prefix}}-win-{{rid}}.zip", "rid": "win-{{rid}}", "url": "{{url}}", "hash": "{{new string(digit, hash)}}" }""";
        static string Release(string version, string runtime, string desktop) =>
            $$"""{ "release-version": "{{version}}", "runtime": { "files": [ {{runtime}} ] }, "windowsdesktop": { "files": [ {{desktop}} ] } }""";

        var list = $$"""
            { "channel-version": "10.0", "latest-release": "10.0.14", "releases": [
              {{Release("10.0.14", ZipEntry("dotnet-runtime", "arm64"), ZipEntry("windowsdesktop-runtime", "x64"))}},
              {{Release("10.0.13", ZipEntry("dotnet-runtime", "arm64") + ", " + ZipEntry("dotnet-runtime", "x64"), ZipEntry("windowsdesktop-runtime", "arm64") + ", " + ZipEntry("windowsdesktop-runtime", "x64"))}},
              {{Release("10.0.12", ZipEntry("dotnet-runtime", "x64", url: "http://example.com/x.zip"), ZipEntry("windowsdesktop-runtime", "x64"))}},
              {{Release("10.0.11", ZipEntry("dotnet-runtime", "x64"), ZipEntry("windowsdesktop-runtime", "x64", hash: 64))}},
              {{Release("10.0.10", ZipEntry("dotnet-runtime", "x64", digit: 'g'), ZipEntry("windowsdesktop-runtime", "x64"))}},
              {{Release("10.0.9", ZipEntry("dotnet-runtime", "x64"), ZipEntry("windowsdesktop-runtime", "x64"))}}
            ] }
            """;
        using var document = JsonDocument.Parse(list);
        var arm = PortableDotNet.Pick(document, "win-arm64");
        expect(arm is { Version: "10.0.13" } && arm.Runtime.Name == "dotnet-runtime-win-arm64.zip" && arm.Desktop.Name == "windowsdesktop-runtime-win-arm64.zip",
            "ARM64: the newest release with both zips for win-arm64 (one without its Windows Desktop zip is passed over)");
        var x64 = PortableDotNet.Pick(document, "win-x64");
        expect(x64 is { Version: "10.0.13" }, "x64: the same release, its x64 zips");
        using var withoutGood = JsonDocument.Parse(list.Replace("\"release-version\": \"10.0.13\"", "\"release-version\": \"\"", StringComparison.Ordinal));
        expect(PortableDotNet.Pick(withoutGood, "win-x64") is { Version: "10.0.9" }, "a zip from an http address, or with no SHA-512 to check (too short, not hex), is never taken");
        expect(PortableDotNet.Pick(JsonDocument.Parse("""{ "releases": [] }"""), "win-x64") is null && PortableDotNet.Pick(JsonDocument.Parse("{}"), "win-x64") is null,
            "a list with no release offers nothing");

        using var previewFirst = JsonDocument.Parse($$"""
            { "releases": [
              {{Release("10.0.0-rc.2", ZipEntry("dotnet-runtime", "x64"), ZipEntry("windowsdesktop-runtime", "x64"))}},
              {{Release("10.0.0", ZipEntry("dotnet-runtime", "x64"), ZipEntry("windowsdesktop-runtime", "x64"))}}
            ] }
            """);
        using var previewOnly = JsonDocument.Parse($$"""{ "releases": [ {{Release("10.0.0-preview.7", ZipEntry("dotnet-runtime", "x64"), ZipEntry("windowsdesktop-runtime", "x64"))}} ] }""");
        expect(PortableDotNet.Pick(previewFirst, "win-x64") is { Version: "10.0.0" } && PortableDotNet.Pick(previewOnly, "win-x64") is null,
            "a preview or release candidate is never taken, even listed first; before the first release there is nothing to take");
    }

    private static void Installers(Action<bool, string> expect)
    {
        static string Exe(string rid, string url = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/x/windowsdesktop-runtime-x.exe", int hash = 128, char digit = 'a',
            string? fileRid = null, string extension = ".exe") =>
            $$"""{ "name": "windowsdesktop-runtime-win-{{rid}}{{extension}}", "rid": "win-{{fileRid ?? rid}}", "url": "{{url}}", "hash": "{{new string(digit, hash)}}" }""";
        static string Release(string version, params string[] files) =>
            $$"""{ "release-version": "{{version}}", "windowsdesktop": { "files": [ {{string.Join(", ", files)}} ] } }""";

        using var document = JsonDocument.Parse($$"""
            { "releases": [
              {{Release("10.0.0-rc.2", Exe("x64"), Exe("arm64"))}},
              {{Release("10.0.14", Exe("x64", url: "http://example.com/x.exe"), Exe("x64", extension: ".zip"), Exe("arm64"))}},
              {{Release("10.0.13", Exe("x64", hash: 64))}},
              {{Release("10.0.12", Exe("x64", digit: 'g'))}},
              {{Release("10.0.11", Exe("x64", fileRid: "arm64"))}},
              {{Release("10.0.10", Exe("x64"))}}
            ] }
            """);
        var x64 = SystemDotNet.PickInstaller(document, "win-x64");
        var arm = SystemDotNet.PickInstaller(document, "win-arm64");
        expect(x64 is { Version: "10.0.10" } && x64.File.Name == "windowsdesktop-runtime-win-x64.exe" && x64.File.Url.StartsWith("https://", StringComparison.Ordinal)
               && arm is { Version: "10.0.14" } && arm.File.Name == "windowsdesktop-runtime-win-arm64.exe",
            "the system-wide install takes Microsoft's windowsdesktop-runtime-win-<arch>.exe of the newest release: never a release candidate, a zip, "
            + "an http address, a SHA-512 too short or not hex, or another processor's");
        expect(SystemDotNet.PickInstaller(JsonDocument.Parse("""{ "releases": [] }"""), "win-x64") is null && SystemDotNet.PickInstaller(JsonDocument.Parse("{}"), "win-x64") is null,
            "a list with no release offers no installer");
        expect(SystemDotNet.InstallerArguments == "/install /quiet /norestart", "Microsoft's installer is run as /install /quiet /norestart: no window of its own, never a restart");
    }

    private static void Decisions(Action<bool, string> expect, Action<string> note)
    {
        string[][] hooks = [["--veloapp-install", "5.3.0"], ["--veloapp-uninstall", "5.3.0"], ["--VELOAPP-Updated", "5.3.0"], ["--veloapp-obsolete"]];
        string[][] background = [["--compositor-worker", "HandPeg-1234", "0"], ["--hw-benchmark", "clip.mp4"], ["--crash-sentinel", "1234", "HandPegCrash", @"C:\Crashes"]];
        string[][] users = [[], [""], [@"C:\Videos\Quick cuts.hproj"], ["--portable"], ["clip.mp4", "--compositor-worker"]];
        static string Line(string[] arguments) => arguments.Length == 0 ? "(no arguments)" : string.Join(" ", arguments.Select(a => a.Length == 0 ? "\"\"" : a));

        bool Table(string[][] cases, bool found, StartAction action, int? code)
        {
            var all = true;
            foreach (var arguments in cases)
            {
                var decided = Launch.Decide(found, arguments);
                var exit = Launch.QuietExitCode(arguments);
                note($"{(found ? ".NET 10 found" : "no .NET 10")}, {Line(arguments)}: {decided}{(decided == StartAction.QuietExit ? $", exit {exit}" : "")}");
                all &= decided == action && (code is null || exit == code);
            }

            return all;
        }

        expect(Table(hooks, false, StartAction.QuietExit, 0),
            "an installer's hook (--veloapp-*, in any case) with no .NET 10 ends at once with exit 0: no window, no download, and Setup hears of no failure");
        expect(Table(background, false, StartAction.QuietExit, 1), "HandPeg's own background processes (their argument first) with no .NET end with exit 1, showing nothing");
        expect(Table(users, false, StartAction.Ask, null), "any other start with no .NET 10 asks (none, a project to open, a background argument that is not first)");
        expect(Table([.. hooks, .. background, .. users], true, StartAction.Host, null), "with .NET 10 found every start runs HandPeg, an installer's hook too (its hook code is HandPeg's)");
    }

    private static void Outcomes(Action<bool, string> expect, Action<string> note)
    {
        (int Code, InstallerOutcome Expected)[] cases =
        [
            (0, InstallerOutcome.Installed), (3010, InstallerOutcome.RestartSuggested), (1641, InstallerOutcome.RestartSuggested),
            (Launch.ErrorCancelled, InstallerOutcome.Cancelled), (1602, InstallerOutcome.Cancelled), (1618, InstallerOutcome.Busy),
            (1603, InstallerOutcome.Failed), (5, InstallerOutcome.Failed), (-1, InstallerOutcome.Failed),
        ];
        var all = true;
        foreach (var (code, expected) in cases)
        {
            var outcome = Launch.OutcomeOf(code);
            note($"installer exit {code}: {outcome}");
            all &= outcome == expected;
        }

        expect(all, "Microsoft's installer's exit code: 0 installed; 3010 and 1641 installed with a restart wanted; 1223 (the UAC prompt declined) and 1602 cancelled, "
                    + "back to the dialog; 1618 another installation running; anything else a failure shown with its code");
    }

    private static void Constants(Action<bool, string> expect)
    {
        // The .NET HandPeg itself is built for: a retarget that forgets the bootstrapper fails here.
        var framework = typeof(HandPegApp.Services.AppPaths).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "";
        var at = framework.IndexOf("Version=v", StringComparison.Ordinal);
        var appMajor = at >= 0 && Version.TryParse(framework[(at + "Version=v".Length)..], out var version) ? version.Major : -1;
        expect(DotNetRoots.Major == 10 && appMajor == DotNetRoots.Major, $"it looks for .NET {DotNetRoots.Major}, the .NET HandPeg is built for ({framework})");
        expect(PortableDotNet.Sites.Length == 2
               && PortableDotNet.Sites.All(s => s.StartsWith("https://", StringComparison.Ordinal) && s.EndsWith($"/release-metadata/{DotNetRoots.Major}.0/releases.json", StringComparison.Ordinal)),
            "and asks Microsoft's .NET 10 release list over https, from its download site and the site's older address");
        expect(DotNetRoots.PortableSentinel == HandPegApp.Services.AppPaths.PortableSentinel && DotNetRoots.PortableArgument == HandPegApp.Services.AppPaths.PortableArgument,
            $"its self-contained copy is HandPeg's own: {DotNetRoots.PortableSentinel} beside the program or {DotNetRoots.PortableArgument}, as AppPaths reads them");
    }

    private static void HandlerRuntimes(Action<bool, string> expect, string scratch)
    {
        var programFiles = Path.Combine(scratch, "PF-shell");
        var machine = Root(Path.Combine(programFiles, "dotnet"), "10.0.14", Core10, Desktop10);
        var environment = new Dictionary<string, string>();
        string? Env(string name) => environment.GetValueOrDefault(name);
        string? Handler(string? registered, string files, string arch = "arm64", bool x64OnArm64 = false) =>
            HandPegApp.Services.ExplorerThumbnails.HandlerRuntime(Env, registered, files, arch, x64OnArm64, DotNetRoots.Major);

        expect(Handler(null, programFiles) == machine, "Explorer's thumbnail handler: a .NET 10 Desktop Runtime in Program Files\\dotnet is one its comhost starts on");
        var registered = Root(Path.Combine(scratch, "registered-shell"), "10.0.14", Core10, "Microsoft.WindowsDesktop.App 8.0.33");
        expect(Handler(registered, programFiles) is null, "the location .NET's installer registered is the only one looked at once set (there with no Windows Desktop 10: none)");
        var forArch = Root(Path.Combine(scratch, "env-shell"), "10.0.14", Core10, Desktop10);
        environment["DOTNET_ROOT_ARM64"] = forArch;
        expect(Handler(registered, programFiles) == forArch, "DOTNET_ROOT_<ARCH> comes before it, as in .NET's host");
        environment.Clear();
        var eight = Path.Combine(scratch, "PF-eight");
        Root(Path.Combine(eight, "dotnet"), "8.0.33", "Microsoft.NETCore.App 8.0.33", "Microsoft.WindowsDesktop.App 8.0.33");
        expect(Handler(null, eight) is null, "a PC with only .NET 8: none, so the handler is not registered (and one left from before is taken out)");
        var preview = Path.Combine(scratch, "PF-preview");
        const string Candidate = "10.0.0-rc.2.25502.107";
        Root(Path.Combine(preview, "dotnet"), Candidate, $"Microsoft.NETCore.App {Candidate}", $"Microsoft.WindowsDesktop.App {Candidate}");
        expect(Handler(null, preview) is null, "a PC whose only .NET 10 is a release candidate's: none, as comhost could not start the handler on it");

        // DOTNET_ROOT set to a folder that holds no .NET (here the one HandPeg's own dotnet is in): the only place looked at, as in .NET's host.
        var localApp = Path.Combine(scratch, "Local-shell", "HandPeg");
        Root(Path.Combine(localApp, "dotnet"), "10.0.14", Core10, Desktop10);
        environment["DOTNET_ROOT"] = localApp;
        expect(Handler(null, programFiles) is null,
            "a DOTNET_ROOT holding no .NET is the only place looked at once set: none, with no fall-back to Program Files\\dotnet or to HandPeg's own .NET beneath it");
        environment.Clear();
        var x64 = Root(Path.Combine(programFiles, "dotnet", "x64"), "10.0.14", Core10, Desktop10);
        expect(Handler(null, programFiles, "x64", x64OnArm64: true) == x64, "an x64 process on an ARM64 PC: Program Files\\dotnet\\x64");
    }

    private static void Programs(Action<bool, string> expect)
    {
        // The repository's tools folder: the first folder up from this test's own with PortableBootstrap in it.
        var tools = new DirectoryInfo(AppContext.BaseDirectory);
        while (tools is not null && !Directory.Exists(Path.Combine(tools.FullName, "PortableBootstrap")))
            tools = tools.Parent;
        var folder = Path.Combine(tools?.FullName ?? "", "PortableBootstrap", "bin", "Release", "net10.0");
        var wrong = new List<string>();
        foreach (var (rid, machine) in new[] { ("win-x64", (ushort)0x8664), ("win-arm64", (ushort)0xAA64) })
        {
            var exe = Path.GetFullPath(Path.Combine(folder, rid, "publish", "HandPegBootstrap.exe"));
            if (!File.Exists(exe))
            {
                Console.WriteLine($"        {rid} HandPeg.exe not published here ({exe}): its header is checked by Release.ps1 and when published");
                continue;
            }

            var header = PeHeader(exe);
            Console.WriteLine($"        {rid} HandPeg.exe: machine 0x{header.Machine:X4}, subsystem {header.Subsystem}, .NET header {header.HasClrHeader}, {new FileInfo(exe).Length / 1024} KB");
            if (header.Machine != machine || header.Subsystem != 2 || header.HasClrHeader || new FileInfo(exe).Length > 4 << 20)
                wrong.Add(rid);
        }

        expect(wrong.Count == 0, "each published HandPeg.exe is a native window program of its processor (x64 0x8664, ARM64 0xAA64), with no .NET header, under 4 MB"
            + (wrong.Count > 0 ? $": not {string.Join(", ", wrong)}" : ""));
    }

    /// <summary>A PE file's machine, subsystem, and whether it has a .NET (CLR) header: data directory 14 of its optional header.</summary>
    private static (ushort Machine, ushort Subsystem, bool HasClrHeader) PeHeader(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 0x3C;
        var pe = reader.ReadInt32();
        reader.BaseStream.Position = pe + 4;
        var machine = reader.ReadUInt16();
        reader.BaseStream.Position = pe + 24;
        var magic = reader.ReadUInt16();
        reader.BaseStream.Position = pe + 24 + 68;
        var subsystem = reader.ReadUInt16();
        var directories = pe + 24 + (magic == 0x20B ? 112 : 96);
        reader.BaseStream.Position = directories + 14 * 8;
        var clr = reader.ReadUInt32();
        return (machine, subsystem, clr != 0);
    }
}
