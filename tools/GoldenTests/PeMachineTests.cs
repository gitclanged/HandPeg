using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// DependencyUpdater's check of a downloaded binary's PE header (5.0, the user's directive 3a): the machine type read
/// from a made-up header, and which machine types each HandPeg (x64, ARM64) takes for a program run beside it (every
/// tool is one since 5.0-Verify: the libmpv library loaded into HandPeg's own process is gone). Each answer is compared
/// with the rule as well as with the golden file.
/// </summary>
public static class PeMachineTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("deps-pe-machine",
            "# DependencyUpdater.PeMachineOf and IsMachineAccepted (5.0): x64 takes x64 only; ARM64 runs ARM64 or x64 programs.",
            Machines);
        yield return Case("deps-pinned-ffmpeg",
            "# DependencyUpdater.PinnedBuilds for FFmpeg (5.2.0's re-cut): each variant's tested build is a month's last (kept two years),\n"
            + "# of its own kind, checked by SHA-256 and never a known-broken build; online, GitHub still has each pin with that digest.",
            PinnedFfmpeg);
    }

    private static void PinnedFfmpeg(StringBuilder text, List<string> problems)
    {
        // x64 (the plain build and the 8.1 line for older NVIDIA drivers) and ARM64.
        string[] variants = ["", DependencyUpdater.OlderNvidiaVariant, DependencyUpdater.Arm64Variant];
        var pins = DependencyUpdater.PinnedBuilds.Where(b => b.Tool == DependencyUpdater.Ffmpeg).ToList();
        foreach (var variant in variants)
        {
            var builds = pins.Where(b => b.Variant == variant).ToList();
            Line(text, $"variant \"{variant}\": {builds.Count} listed");
            if (builds.Count == 0)
            {
                problems.Add($"no pinned FFmpeg for the variant \"{variant}\"");
                continue;
            }

            for (var rank = 0; rank < builds.Count; rank++)
            {
                var build = builds[rank];
                var monthEnd = MonthEndTag(build.Tag);
                Line(text, $"  {(rank == 0 ? "tested" : "stand-in")} {build.Label}: {build.Tag} {build.Asset} ({build.Size} bytes), month's last build {(monthEnd ? "yes" : "no")}");
                if (!DependencyUpdater.IsVariantAsset(DependencyUpdater.Ffmpeg, variant, build.Asset))
                    problems.Add($"{build.Asset} is not a build of the variant \"{variant}\"");
                if (build.Sha256.Length != 64 || !build.Sha256.All(Uri.IsHexDigit))
                    problems.Add($"{build.Asset} has no SHA-256");
                if (DependencyUpdater.BrokenAssets.Contains(build.Asset) || DependencyUpdater.IsPassedOver(build.Asset))
                    problems.Add($"{build.Asset} is pinned but known not to start");
                if (!monthEnd)
                    problems.Add($"{build.Tag} is a daily build, which BtbN keeps only two weeks");
                if (!build.Version.EndsWith(build.Tag.Substring("autobuild-".Length, 10).Replace("-", "", StringComparison.Ordinal), StringComparison.Ordinal))
                    problems.Add($"{build.Label}'s version {build.Version} is not of its release's day");
            }
        }

        foreach (var broken in DependencyUpdater.BrokenAssets.Order(StringComparer.Ordinal))
            Line(text, $"never taken: {broken}");

        // Online: each pinned release still holds its file, with the digest and size pinned. Only GitHub's definite answers
        // count (gone, or a different file); no connection or the hourly limit is a note, so an offline run still passes.
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HandPeg-GoldenTests/1.0");
        foreach (var tag in pins.Select(b => b.Tag).Distinct())
        {
            try
            {
                using var response = http.GetAsync($"https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/{tag}").GetAwaiter().GetResult();
                if ((int)response.StatusCode == 404)
                {
                    problems.Add($"GitHub no longer has the release {tag}");
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  deps-pinned-ffmpeg: GitHub answered {(int)response.StatusCode} for {tag}; not checked online");
                    continue;
                }

                using var json = System.Text.Json.JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var assets = json.RootElement.GetProperty("assets").EnumerateArray()
                    .ToDictionary(a => a.GetProperty("name").GetString() ?? "", a => a);
                foreach (var build in pins.Where(b => b.Tag == tag))
                {
                    if (!assets.TryGetValue(build.Asset, out var asset))
                        problems.Add($"the release {tag} no longer has {build.Asset}");
                    else if (asset.GetProperty("digest").GetString() is var digest && !string.Equals(digest, "sha256:" + build.Sha256, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{build.Asset}: GitHub's digest {digest} is not the pinned SHA-256");
                    else if (asset.GetProperty("size").GetInt64() != build.Size)
                        problems.Add($"{build.Asset}: GitHub's size {asset.GetProperty("size").GetInt64()} is not the pinned {build.Size}");
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                Console.WriteLine($"  deps-pinned-ffmpeg: GitHub could not be reached ({ex.Message}); not checked online");
            }
        }
    }

    // "autobuild-2026-10-31-12-59": built on the last day of its month, the build BtbN keeps for two years.
    private static bool MonthEndTag(string tag) =>
        DateTime.TryParseExact(tag.AsSpan("autobuild-".Length, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
        && day.AddDays(1).Month != day.Month;

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }

    private static void Line(StringBuilder text, FormattableString line) =>
        text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Machines(StringBuilder text, List<string> problems)
    {
        // The header as a linker writes it: "MZ", the new header's place at 0x3C, then "PE\0\0" and the machine type.
        (string Name, ushort Machine)[] machines =
        [
            ("x64", DependencyUpdater.MachineAmd64),
            ("ARM64", DependencyUpdater.MachineArm64),
            ("x86", 0x014C),
        ];

        foreach (var (name, machine) in machines)
        {
            var read = DependencyUpdater.PeMachineOf(new MemoryStream(Header(machine)));
            Line(text, $"header {name} (0x{machine:X4}) reads as {(read is { } r ? $"0x{r:X4}" : "nothing")}");
            if (read != machine)
                problems.Add($"the {name} header read as {read?.ToString("X4", CultureInfo.InvariantCulture) ?? "nothing"}");
        }

        // What is not a PE file at all reads as nothing: too short, no "MZ", a new header past the end, no "PE\0\0".
        var broken = new (string Name, byte[] Bytes)[]
        {
            ("too short", [0x4D, 0x5A, 0, 0]),
            ("no MZ", Patched(Header(DependencyUpdater.MachineAmd64), 0, 0x00)),
            ("header past the end", Patched(Header(DependencyUpdater.MachineAmd64), 0x3D, 0x10)),
            ("no PE signature", Patched(Header(DependencyUpdater.MachineAmd64), 0x80, (byte)'X')),
        };
        foreach (var (name, bytes) in broken)
        {
            var read = DependencyUpdater.PeMachineOf(new MemoryStream(bytes));
            Line(text, $"{name}: {(read is { } r ? $"0x{r:X4}" : "nothing")}");
            if (read is not null)
                problems.Add($"\"{name}\" read as a PE file (0x{read:X4})");
        }

        (string Name, Architecture Architecture)[] processes = [("x64", Architecture.X64), ("ARM64", Architecture.Arm64)];
        foreach (var (process, architecture) in processes)
        {
            var taken = new List<string>();
            foreach (var (name, machine) in machines)
            {
                var accepted = DependencyUpdater.IsMachineAccepted(machine, architecture);
                if (accepted)
                    taken.Add(name);

                var expected = architecture == Architecture.X64
                    ? machine == DependencyUpdater.MachineAmd64
                    : machine is DependencyUpdater.MachineArm64 or DependencyUpdater.MachineAmd64;
                if (accepted != expected)
                    problems.Add($"a {process} HandPeg {(accepted ? "took" : "refused")} an {name} program");
            }

            Line(text, $"{process} HandPeg, program: takes {(taken.Count == 0 ? "nothing" : string.Join(", ", taken))}");
        }

        // A processor HandPeg is never built for takes nothing.
        if (DependencyUpdater.IsMachineAccepted(DependencyUpdater.MachineAmd64, Architecture.X86))
            problems.Add("an x86 HandPeg took an x64 program");
    }

    /// <summary>A 0x100-byte file holding a PE header for <paramref name="machine"/>, the new header at 0x80.</summary>
    private static byte[] Header(ushort machine)
    {
        var bytes = new byte[0x100];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        BitConverter.TryWriteBytes(bytes.AsSpan(0x3C), 0x80u);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BitConverter.TryWriteBytes(bytes.AsSpan(0x84), machine);
        return bytes;
    }

    private static byte[] Patched(byte[] bytes, int at, byte value)
    {
        bytes[at] = value;
        return bytes;
    }
}
