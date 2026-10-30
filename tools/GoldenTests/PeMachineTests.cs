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
    }

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
