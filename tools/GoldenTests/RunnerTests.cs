using System.Text;
using CliWrap;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// FfmpegRunner's handling of a command: the arguments split exactly as FFmpeg splits them, passed on through CliWrap's
/// quoting unchanged (to the character, for any text), and the graphs swapped for files the way each FFmpeg takes them.
/// </summary>
public static class RunnerTests
{
    public static IEnumerable<GraphResult> All(IReadOnlyList<GraphResult> commands)
    {
        yield return Quoting(commands);
        yield return GraphFiles();
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

    /// <summary>How the graphs of a command are given to each kind of FFmpeg: the newer option, the older one, or left on the line.</summary>
    private static GraphResult GraphFiles()
    {
        var problems = new List<string>();
        var shortGraph = "[0:v]scale=640:-2[v]";
        var longGraph = "[0:v]" + string.Concat(Enumerable.Repeat("null,", 2000)) + "format=yuv420p[v]";
        var arguments = new List<string> { "-hide_banner", "-i", "D:/media/a b.mp4", "-filter_complex", shortGraph, "-map", "[v]", "-vf", "hflip", "-af", "volume=0.5", "-filter:v:0", "vflip", "D:/out/x.mp4" };

        var text = new StringBuilder("# How a command's graphs reach FFmpeg (FfmpegRunner.WithGraphFiles): the arguments after the change, a graph file shown as <file N: its text>.\n");
        void Show(string title, IReadOnlyList<string> input, bool always, FfmpegRunner.GraphFileForm form)
        {
            var written = new List<string>();
            var result = FfmpegRunner.WithGraphFiles(input, always, form, graph =>
            {
                written.Add(graph);
                return $"<file {written.Count}>";
            });

            text.AppendLine().Append(title).Append(" (needs a file: ").Append(FfmpegRunner.NeedsGraphFile(input, always) ? "yes" : "no").AppendLine(")");
            foreach (var argument in result)
                text.Append("  ").AppendLine(argument.Length > 120 ? $"{argument[..60]}... ({argument.Length} characters)" : argument);
            for (var i = 0; i < written.Count; i++)
                text.Append("  <file ").Append(i + 1).Append(">: ").AppendLine(written[i].Length > 120 ? $"{written[i][..60]}... ({written[i].Length} characters)" : written[i]);

            // What FFmpeg reads from the files, put back where the files are named, is the command as it was.
            var restored = result.Select((a, i) => a.StartsWith("<file ", StringComparison.Ordinal) ? written[int.Parse(a[6..^1]) - 1] : a).ToList();
            var options = input.Select((a, i) => (a, i)).Where(p => p.a.StartsWith('-')).Select(p => p.i).ToHashSet();
            for (var i = 0; i < restored.Count; i++)
            {
                if (restored[i] != input[i] && !options.Contains(i))
                    problems.Add($"{title}: argument {i} is \"{restored[i]}\" where the command had \"{input[i]}\"");
            }
        }

        Show("Every graph in a file, FFmpeg 7 or later", arguments, always: true, FfmpegRunner.GraphFileForm.LoadOption);
        Show("Every graph in a file, an older FFmpeg", arguments, always: true, FfmpegRunner.GraphFileForm.ScriptOption);
        Show("Only long graphs (a run that does not always send them in files)", arguments, always: false, FfmpegRunner.GraphFileForm.LoadOption);
        Show("A long graph, a run that does not always send them in files", arguments.Select(a => a == shortGraph ? longGraph : a).ToList(), always: false, FfmpegRunner.GraphFileForm.LoadOption);
        Show("An FFmpeg that takes neither", arguments, always: true, FfmpegRunner.GraphFileForm.None);

        if (FfmpegRunner.NeedsGraphFile(["-i", "x", "-c", "copy", "y"], always: true))
            problems.Add("a command without a graph was said to need a graph file");
        return new GraphResult("runner-graph-files", text.ToString(), problems, 0);
    }
}
