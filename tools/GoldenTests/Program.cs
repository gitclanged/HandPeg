using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace HandPegGoldenTests;

/// <summary>
/// HandPeg's golden-file tests: what FilterGraphBuilder writes for each fixture, the .hproj format's reading and writing,
/// and FfmpegRunner's handling of a command, compared with the files under golden\, plus the checks made by code (the
/// A/V invariant, the project round trip). Run by tools\GoldenTests.ps1; "--update" writes the golden files anew.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Everything HandPeg would keep (settings, caches, logs, the masks folder) goes into a folder of the test's own,
        // never the user's: set before any of HandPeg's code runs.
        var data = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("HANDPEG_DATA_DIR", data);

        var exit = 1;
        var thread = new Thread(() => exit = Run(args, data));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try
        {
            Directory.Delete(data, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return exit;
    }

    private static int Run(string[] args, string data)
    {
        var started = Stopwatch.StartNew();
        var update = args.Contains("--update");
        var filter = Value(args, "--filter");
        var root = Value(args, "--root") ?? FindRoot();
        var goldenFolder = Path.Combine(root, "golden");

        var results = new List<GraphResult>();
        var graphs = GraphCases.All().ToList();
        results.AddRange(graphs);
        results.AddRange(ProjectTests.All(Path.Combine(root, "fixtures"), Path.Combine(data, "scratch")));
        results.AddRange(RunnerTests.All(graphs));
        results.AddRange(TimelineDocumentTests.All());
        results.AddRange(TimelineGraphCases.All());
        results.AddRange(CaptionTests.All());
        results.AddRange(WaveformTests.All());
        if (filter is not null)
            results = results.Where(r => r.Golden.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var (failed, written, moments) = (0, 0, 0);
        foreach (var result in results)
        {
            moments += result.CheckedMoments;
            var path = Path.Combine(goldenFolder, result.Golden + ".txt");
            var actual = result.Text.ReplaceLineEndings("\n");
            var expected = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
            var problems = new List<string>(result.Problems);

            if (expected != actual)
            {
                if (update)
                {
                    Directory.CreateDirectory(goldenFolder);
                    File.WriteAllText(path, actual, new UTF8Encoding(false));
                    written++;
                }
                else
                {
                    problems.Insert(0, expected is null
                        ? $"no golden file (golden\\{result.Golden}.txt): run with -Update to write it"
                        : $"differs from golden\\{result.Golden}.txt (- golden, + now):\n{LineDiff.Describe(expected, actual)}");
                }
            }

            if (problems.Count == 0)
            {
                Console.WriteLine($"  ok    {result.Golden}");
                continue;
            }

            failed++;
            Console.WriteLine($"  FAIL  {result.Golden}");
            foreach (var problem in problems)
                Console.WriteLine("        " + problem.Replace("\n", "\n        ", StringComparison.Ordinal).TrimEnd());
        }

        // Golden files no case writes any more.
        if (filter is null && Directory.Exists(goldenFolder))
        {
            var names = results.Select(r => r.Golden + ".txt").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in Directory.EnumerateFiles(goldenFolder, "*.txt").Where(f => !names.Contains(Path.GetFileName(f))))
                Console.WriteLine($"  note  golden\\{Path.GetFileName(stale)} belongs to no test any more");
        }

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{results.Count - failed} of {results.Count} passed{(update ? $", {written} golden file(s) written" : "")}; {moments} moments followed by the A/V check; {started.Elapsed.TotalSeconds:0.00} s"));
        return failed == 0 ? 0 : 1;
    }

    private static string? Value(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>The project's folder (where golden\ and fixtures\ are), found upwards from the built program.</summary>
    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "GoldenTests.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("GoldenTests.csproj was not found above the program: pass --root.");
    }
}
