using System.Text;
using CliWrap;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// FfmpegRunner's handling of a command: the arguments split exactly as FFmpeg splits them, passed on through CliWrap's
/// quoting unchanged (to the character, for any text). (5.2-Arm64AndNpu: no graph is handed to FFmpeg in a file any more, and
/// runner-graph-files went with that.)
/// </summary>
public static class RunnerTests
{
    public static IEnumerable<GraphResult> All(IReadOnlyList<GraphResult> commands)
    {
        yield return Quoting(commands);
    }

    /// <summary>
    /// Every command line the fixtures make, and thousands of argument lists made of the characters quoting can get wrong
    /// (quotes, backslashes before them and at the end, spaces, tabs, empty arguments, letters beyond ASCII), quoted by
    /// CliWrap and split again as FFmpeg splits its command line: what comes back is what went in.
    /// </summary>
    private static GraphResult Quoting(IReadOnlyList<GraphResult> commands)
    {
        var problems = new List<string>();
        var checkedLists = 0;
        void RoundTrip(IReadOnlyList<string> arguments, string what)
        {
            checkedLists++;
            var line = Cli.Wrap("ffmpeg").WithArguments(arguments).Arguments;
            var back = FfmpegRunner.SplitArguments(line);
            if (!back.SequenceEqual(arguments, StringComparer.Ordinal) && problems.Count < 10)
                problems.Add($"{what}: [{string.Join("] [", arguments)}] came back as [{string.Join("] [", back)}]");
        }

        // The fixtures' own commands, as the builder writes them: split, quoted, split again.
        foreach (var command in commands.Select(c => c.CommandLine).OfType<string>())
            RoundTrip(FfmpegRunner.SplitArguments(command[(command.IndexOf(' ') + 1)..]), "a fixture's command");

        var random = new Random(4101);
        const string alphabet = "ab \"\\\t'é:;,[]=%^&|<>\u00A0\u4E2D";
        for (var n = 0; n < 4000; n++)
        {
            var arguments = new List<string>();
            for (var a = random.Next(1, 6); a > 0; a--)
            {
                var text = new StringBuilder();
                for (var c = random.Next(0, 12); c > 0; c--)
                    text.Append(alphabet[random.Next(alphabet.Length)]);
                arguments.Add(text.ToString());
            }

            RoundTrip(arguments, "a random argument list");
        }

        // The cases quoting rules differ on, one at a time.
        foreach (var argument in new[] { "", " ", "\\", "\\\\", "a\\", "a\\\\", "\"", "\\\"", "a\"b", "a \\\"b\\\\\"", "end\\", "\"\"", "x\"\"y", "-filter_complex" })
            RoundTrip(["-i", argument, "out"], "a quoting edge");

        return new GraphResult("runner-quoting", "# Arguments quoted by CliWrap and split as FFmpeg splits them (CommandLineToArgvW) come back unchanged.\n"
                                                 + $"# argument lists checked: {checkedLists}\n", problems, 0);
    }
}
