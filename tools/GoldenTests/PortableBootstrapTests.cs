using System.IO;
using System.Text;
using System.Text.Json;
using HandPegBootstrap;

namespace HandPegGoldenTests;

/// <summary>
/// portable-bootstrap (Pass 5.2.1): the portable zips' HandPeg.exe, which runs HandPeg.dll on a .NET 8 Desktop Runtime it finds or
/// downloads (tools/PortableBootstrap). Where it looks, in order, on layouts made on disk (this copy's portable\dotnet, 5.2.0's
/// hostfxr.dll and shared\ beside it, DOTNET_ROOT_&lt;ARCH&gt; then DOTNET_ROOT, the installed one, Program Files\dotnet\x64 for an x64
/// copy on an ARM64 PC); what counts as a root (both frameworks of major 8 and a hostfxr, the newest); which of Microsoft's zips it
/// takes from a release list (the newest release with both for the processor, https and a SHA-512 only). And, where they have been
/// published (Release.ps1), that both HandPeg.exe builds are native programs of their processor: no .NET header, a window program,
/// small. Every line says what it proved.
/// </summary>
public static class PortableBootstrapTests
{
    public static GraphResult Run()
    {
        var text = new StringBuilder("# The portable zips' HandPeg.exe (5.2.1): .NET 8 found (this copy's own first) or Microsoft's downloaded; no script.\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        var scratch = Path.Combine(Path.GetTempPath(), "handpeg-bootstrap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Roots(Expect, scratch);
            Releases(Expect);
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

    // A .NET root made of empty files: host\fxr\<fxr>\hostfxr.dll (or hostfxr.dll at its top) and each framework's folder with a file in it.
    private static string Root(string folder, string? fxr, params string[] frameworks)
    {
        Directory.CreateDirectory(folder);
        if (fxr == "beside")
        {
            File.WriteAllText(Path.Combine(folder, "hostfxr.dll"), "");
        }
        else if (fxr is not null)
        {
            Directory.CreateDirectory(Path.Combine(folder, "host", "fxr", fxr));
            File.WriteAllText(Path.Combine(folder, "host", "fxr", fxr, "hostfxr.dll"), "");
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

    private const string Core8 = "Microsoft.NETCore.App 8.0.33", Desktop8 = "Microsoft.WindowsDesktop.App 8.0.33";

    private static void Roots(Action<bool, string> expect, string scratch)
    {
        var empty = Root(Path.Combine(scratch, "empty"), null);
        expect(DotNetRoots.Usable(empty, "x") is null, "an empty folder is no root");
        var noDesktop = Root(Path.Combine(scratch, "core-only"), "8.0.33", Core8);
        expect(DotNetRoots.Usable(noDesktop, "x") is null, "nor a .NET with no Windows Desktop runtime");
        var nine = Root(Path.Combine(scratch, "nine"), "9.0.22", "Microsoft.NETCore.App 9.0.22", "Microsoft.WindowsDesktop.App 9.0.22");
        expect(DotNetRoots.Usable(nine, "x") is null, "nor one of another major version (HandPeg asks for 8)");
        var noFxr = Root(Path.Combine(scratch, "no-fxr"), null, Core8, Desktop8);
        expect(DotNetRoots.Usable(noFxr, "x") is null, "nor one with no hostfxr.dll");
        var emptyFramework = Root(Path.Combine(scratch, "hollow"), "8.0.33", Core8);
        Directory.CreateDirectory(Path.Combine(emptyFramework, "shared", "Microsoft.WindowsDesktop.App", "8.0.33"));
        expect(DotNetRoots.Usable(emptyFramework, "x") is null, "nor one whose framework folder is empty (a download cut off)");

        var many = Root(Path.Combine(scratch, "many"), "8.0.1", Core8, Desktop8, "Microsoft.NETCore.App 10.0.14", "Microsoft.WindowsDesktop.App 10.0.14");
        Root(many, "10.0.14");
        Root(many, "9.0.0-rc.2");
        var found = DotNetRoots.Usable(many, "x");
        expect(found is not null && found.HostFxr.EndsWith(Path.Combine("fxr", "10.0.14", "hostfxr.dll"), StringComparison.OrdinalIgnoreCase),
            "a root with several hostfxr versions runs on the newest (as .NET's own host does)");
        expect(DotNetRoots.VersionOf("9.0.0-rc.2") == new Version(9, 0, 0) && DotNetRoots.VersionOf("current") is null, "a preview's folder reads as its version; a name that is none is passed over");

        // The order: this copy's portable\dotnet, then 5.2.0's layout beside HandPeg.exe, then the environment's, then the installed one.
        var app = Path.Combine(scratch, "app");
        var system = Root(Path.Combine(scratch, "Program Files", "dotnet"), "8.0.33", Core8, Desktop8);
        var registered = Root(Path.Combine(scratch, "registered"), "8.0.33", Core8, Desktop8);
        var arm = Root(Path.Combine(scratch, "env-arm64"), "8.0.33", Core8, Desktop8);
        var plain = Root(Path.Combine(scratch, "env"), "8.0.33", Core8, Desktop8);
        var environment = new Dictionary<string, string> { ["DOTNET_ROOT_ARM64"] = arm, ["DOTNET_ROOT"] = plain };
        string? Env(string name) => environment.GetValueOrDefault(name);
        DotNetRoot? Find(bool withSystem, bool x64OnArm64 = false, string arch = "arm64") =>
            DotNetRoots.Find(DotNetRoots.Candidates(app, withSystem, Env, registered, Path.Combine(scratch, "Program Files"), arch, x64OnArm64));

        Directory.CreateDirectory(app);
        expect(Find(true)?.Folder == Path.GetFullPath(arm), "a copy with no .NET of its own runs on DOTNET_ROOT_ARM64 first (an ARM64 copy)");
        environment.Remove("DOTNET_ROOT_ARM64");
        expect(Find(true)?.Folder == Path.GetFullPath(plain), "then on DOTNET_ROOT");
        environment.Clear();
        expect(Find(true) is { Where: "the installed .NET" } r && r.Folder == Path.GetFullPath(registered), "then on the .NET its installer registered");
        expect(DotNetRoots.Find(DotNetRoots.Candidates(app, true, Env, null, Path.Combine(scratch, "Program Files"), "arm64", false))?.Folder == Path.GetFullPath(system),
            "then on Program Files\\dotnet");
        var x64 = Root(Path.Combine(scratch, "Program Files", "dotnet", "x64"), "8.0.33", Core8, Desktop8);
        expect(DotNetRoots.Find(DotNetRoots.Candidates(app, true, Env, null, Path.Combine(scratch, "Program Files"), "x64", true))?.Folder == Path.GetFullPath(x64),
            "an x64 copy on an ARM64 PC: Program Files\\dotnet\\x64, the x64 .NET's place there");
        expect(Find(false) is null, "looking only in the copy's own folder (HANDPEG_BOOTSTRAP_NO_SYSTEM_DOTNET), none: it downloads");

        Root(app, "beside", Core8, Desktop8);
        expect(Find(true) is { Where: "this copy's folder" } legacy && legacy.HostFxr == Path.Combine(Path.GetFullPath(app), "hostfxr.dll"),
            "5.2.0's script's layout (hostfxr.dll and shared\\ beside HandPeg.exe) is this copy's own, before the PC's");
        var portable = Root(Path.Combine(app, DotNetRoots.PortableFolder), "8.0.33", Core8, Desktop8);
        expect(Find(true) is { Where: "this copy's portable\\dotnet" } own && own.Folder == Path.GetFullPath(portable), "and portable\\dotnet, where the download puts it, first of all");
    }

    private static void Releases(Action<bool, string> expect)
    {
        static string File(string prefix, string rid, string url = "https://builds.dotnet.microsoft.com/x.zip", int hash = 128) =>
            $$"""{ "name": "{{prefix}}-win-{{rid}}.zip", "rid": "win-{{rid}}", "url": "{{url}}", "hash": "{{new string('a', hash)}}" }""";
        static string Release(string version, string runtime, string desktop) =>
            $$"""{ "release-version": "{{version}}", "runtime": { "files": [ {{runtime}} ] }, "windowsdesktop": { "files": [ {{desktop}} ] } }""";

        var list = $$"""
            { "channel-version": "8.0", "latest-release": "8.0.34", "releases": [
              {{Release("8.0.34", File("dotnet-runtime", "arm64"), File("windowsdesktop-runtime", "x64"))}},
              {{Release("8.0.33", File("dotnet-runtime", "arm64") + ", " + File("dotnet-runtime", "x64"), File("windowsdesktop-runtime", "arm64") + ", " + File("windowsdesktop-runtime", "x64"))}},
              {{Release("8.0.32", File("dotnet-runtime", "x64", url: "http://example.com/x.zip"), File("windowsdesktop-runtime", "x64"))}},
              {{Release("8.0.31", File("dotnet-runtime", "x64"), File("windowsdesktop-runtime", "x64", hash: 64))}},
              {{Release("8.0.30", File("dotnet-runtime", "x64"), File("windowsdesktop-runtime", "x64"))}}
            ] }
            """;
        using var document = JsonDocument.Parse(list);
        var arm = PortableDotNet.Pick(document, "win-arm64");
        expect(arm is { Version: "8.0.33" } && arm.Runtime.Name == "dotnet-runtime-win-arm64.zip" && arm.Desktop.Name == "windowsdesktop-runtime-win-arm64.zip",
            "ARM64: the newest release with both zips for win-arm64 (one without its Windows Desktop zip is passed over)");
        var x64 = PortableDotNet.Pick(document, "win-x64");
        expect(x64 is { Version: "8.0.33" }, "x64: the same release, its x64 zips");
        using var withoutGood = JsonDocument.Parse(list.Replace("\"release-version\": \"8.0.33\"", "\"release-version\": \"\"", StringComparison.Ordinal));
        expect(PortableDotNet.Pick(withoutGood, "win-x64") is { Version: "8.0.30" }, "a zip from an http address, or with no SHA-512 to check, is never taken");
        expect(PortableDotNet.Pick(JsonDocument.Parse("""{ "releases": [] }"""), "win-x64") is null && PortableDotNet.Pick(JsonDocument.Parse("{}"), "win-x64") is null,
            "a list with no release offers nothing");
    }

    private static void Programs(Action<bool, string> expect)
    {
        // The repository's tools folder: the first folder up from this test's own with PortableBootstrap in it.
        var tools = new DirectoryInfo(AppContext.BaseDirectory);
        while (tools is not null && !Directory.Exists(Path.Combine(tools.FullName, "PortableBootstrap")))
            tools = tools.Parent;
        var folder = Path.Combine(tools?.FullName ?? "", "PortableBootstrap", "bin", "Release", "net8.0");
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
        using var reader = new BinaryReader(System.IO.File.OpenRead(path));
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
