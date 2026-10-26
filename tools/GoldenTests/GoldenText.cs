using System.Globalization;
using System.Text;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Commands and graphs written out for a golden file, so that a change reads as the few lines it changed: a command an
/// argument to a line (as FFmpeg receives them), each filter graph after it a chain to a block and a filter to a line.
/// What differs from one computer to the next (the Windows fonts folder) is written as a placeholder.
/// </summary>
public static class GoldenText
{
    // The options whose value is a filter graph (FfmpegRunner sends the same ones to FFmpeg in files).
    private static readonly HashSet<string> GraphOptions = ["-filter_complex", "-lavfi", "-vf", "-af", "-filter:v", "-filter:a"];

    /// <summary>A command line: its arguments, then its graphs. <paramref name="graphs"/> receives the graphs, for the checks that read them.</summary>
    public static string Command(string commandLine, List<string>? graphs = null)
    {
        var text = new StringBuilder();
        var match = commandLine.Trim();
        var space = match.IndexOf(' ');
        var arguments = FfmpegRunner.SplitArguments(space < 0 ? "" : match[(space + 1)..]);
        var found = new List<(string Option, string Graph)>();

        text.AppendLine("command:");
        text.Append("  ").AppendLine(space < 0 ? match : match[..space]);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (GraphOptions.Contains(arguments[i]) && i + 1 < arguments.Count)
            {
                found.Add((arguments[i], arguments[i + 1]));
                text.Append("  ").Append(arguments[i]).Append(" <graph ").Append(found.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(">");
                i++;
                continue;
            }

            text.Append("  ").AppendLine(Normalize(arguments[i]));
        }

        for (var n = 0; n < found.Count; n++)
        {
            text.AppendLine();
            text.Append(Graph(found[n].Graph, $"graph {n + 1} ({found[n].Option})"));
            graphs?.Add(found[n].Graph);
        }

        return text.ToString();
    }

    /// <summary>A filter graph: a block per chain, numbered, a filter to a line.</summary>
    public static string Graph(string graph, string title)
    {
        var text = new StringBuilder();
        var chains = FilterGraph.SplitTopLevel(graph, ';');
        text.Append(title).Append(": ").Append(chains.Count.ToString(CultureInfo.InvariantCulture)).Append(" chains, ")
            .Append(graph.Length.ToString(CultureInfo.InvariantCulture)).AppendLine(" characters");
        for (var c = 0; c < chains.Count; c++)
        {
            var filters = FilterGraph.SplitTopLevel(chains[c], ',');
            var number = (c + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3);
            for (var f = 0; f < filters.Count; f++)
                text.Append(f == 0 ? number + "| " : "   |   ,").AppendLine(Normalize(filters[f]));
        }

        return text.ToString();
    }

    /// <summary>The same text on any Windows computer: the fonts folder (where drawtext's fonts are) as a placeholder.</summary>
    public static string Normalize(string text)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (fonts.Length == 0)
            return text;

        // As the graph writes a path inside quotes (FilterGraphBuilder.EscapeFilterPath), and as it is.
        var escaped = fonts.Replace('\\', '/').Replace(":", @"\:");
        return text.Replace(escaped, "<fonts>", StringComparison.OrdinalIgnoreCase).Replace(fonts, "<fonts>", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A number as the golden files write one: invariant, at most six decimals.</summary>
    public static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>A line diff of a golden file and what the code makes now, with a little context around each change.</summary>
public static class LineDiff
{
    public static string Describe(string expected, string actual, int context = 2, int maxLines = 80)
    {
        var a = expected.ReplaceLineEndings("\n").Split('\n');
        var b = actual.ReplaceLineEndings("\n").Split('\n');

        // The longest common subsequence, by dynamic programming: golden files are a few hundred lines at most.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        }

        var lines = new List<(char Kind, int Old, int New, string Text)>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
                lines.Add((' ', ++x, ++y, a[x - 1]));
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
                lines.Add(('+', x, ++y, b[y - 1]));
            else
                lines.Add(('-', ++x, y, a[x - 1]));
        }

        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == ' ')
                continue;
            for (var k = Math.Max(0, i - context); k <= Math.Min(lines.Count - 1, i + context); k++)
                keep[k] = true;
        }

        var text = new StringBuilder();
        var written = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!keep[i])
                continue;
            if (i > 0 && !keep[i - 1])
                text.AppendLine("    ...");
            if (++written > maxLines)
            {
                text.AppendLine($"    ... and {lines.Count(l => l.Kind != ' ') - lines.Take(i).Count(l => l.Kind != ' ')} more changed lines");
                break;
            }

            var (kind, old, @new, line) = lines[i];
            var where = kind == '+' ? $"     {@new,4}" : $"{old,4}     ";
            text.Append(kind == ' ' ? "  " : kind + " ").Append(where).Append(" ").AppendLine(line);
        }

        return text.ToString();
    }
}
