using System.Globalization;
using System.IO;
using HandPegApp.Services;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// The A/V sync harness (tools\AvSyncTest.ps1 runs it; see tools\README.md). Opens a project in the app's own view
/// model, builds the live graph the app sends the player with a seek to each moment, has libmpv play it headless and
/// write what it plays (mpv's encoding mode, on the sequence's clock), and measures the picture and the sound of that
/// against the files they came from. "session" plays in real time instead, through the steps the app takes (a seek
/// while paused, the sound settling, Play, a seek while playing, the rebuild while playing), and reads mpv's clocks.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var options = Options.Parse(args);
            Media.Ffmpeg = options.Get("ffmpeg", Path.Combine(AppPaths.Deps, "ffmpeg", "ffmpeg.exe"));
            Media.Ffprobe = options.Get("ffprobe", Path.Combine(AppPaths.Deps, "ffmpeg", "ffprobe.exe"));
            MpvSession.Echo = options.Has("echo");
            return options.Mode switch
            {
                "test" => Test(options),
                "session" => Session.Run(options),
                _ => throw new ArgumentException($"Unknown mode: {options.Mode} (test or session)."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or TimeoutException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    /// <summary>The player's output at each moment, with and without the proxies, measured.</summary>
    private static int Test(Options options)
    {
        var points = options.Get("points", "100,300,600,1500").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToList();
        var proxyRuns = options.Get("proxies", "off,on").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim() == "on").ToList();
        var seconds = options.Number("seconds", 3);
        var tolerance = options.Number("tolerance", 15);
        var surface = options.Size("surface", (1280, 720));
        var work = options.WorkFolder();
        var libmpv = options.Get("libmpv", Path.Combine(AppPaths.Deps, "mpv", "libmpv-2.dll"));
        var mainProxy = options.Has("main-proxy") ? Path.GetFullPath(options.Get("main-proxy")) : null;

        using var project = ProjectSession.Open(Path.GetFullPath(options.Get("project")), Console.Out);
        var missing = project.WaitForFacts(TimeSpan.FromSeconds(90));
        if (missing.Count > 0)
            Console.WriteLine($"Not known yet (the graphs are built without): {string.Join(", ", missing)}");

        var results = new List<(bool Proxies, PointResult Result, EncodedRun Run)>();
        foreach (var proxies in proxyRuns)
        {
            var inUse = project.UseProxies(proxies, proxies ? mainProxy : null);
            Console.WriteLine();
            Console.WriteLine($"Proxies {(proxies ? "on" : "off")}: {(inUse.Count == 0 ? "none in use" : string.Join(", ", inUse.Select(p => $"{Path.GetExtension(p.Key)} -> {Path.GetFileName(p.Value)}")))}");
            if (proxies && inUse.Count == 0)
                Console.WriteLine("  (no proxy is ready for this project's files: the run with proxies is the same as without)");

            var source = project.PlayerSource;
            var state = project.State();
            foreach (var at in points)
            {
                var graph = project.Graph(at, surface);
                var text = Validation.Apply(options.Has("graph-file") ? File.ReadAllText(options.Get("graph-file")).Trim() : graph.Text, options);
                var output = Path.Combine(work, $"out_{(proxies ? "on" : "off")}_{at.ToString("0.###", CultureInfo.InvariantCulture)}.mkv");
                File.WriteAllText(Path.ChangeExtension(output, ".graph.txt"), text);
                var run = EncodedRun.Play(libmpv, source, text, at, seconds, output);
                var result = run.Error is { } error
                    ? new PointResult(at, "nothing", null, null, 0, 0, null, 0, error)
                    : Measurement.Measure(state, text, output, at, seconds, inUse);
                results.Add((proxies, result, run));
                Console.WriteLine("  " + Row(proxies, result, run, tolerance));
                if (!options.Has("keep") && File.Exists(output))
                    File.Delete(output);
            }
        }

        Console.WriteLine();
        Console.WriteLine("proxies  at (s)   what                      video (ms)              frames   audio (ms)  score   A/V (ms)  ready (ms)  result");
        foreach (var (proxies, result, run) in results)
            Console.WriteLine(Row(proxies, result, run, tolerance));
        foreach (var (_, result, _) in results.Where(r => r.Result.Notes.Length > 0))
            Console.WriteLine($"  at {result.At:0.###} s: {result.Notes}");

        var failed = results.Count(r => !r.Result.Passes(tolerance));
        Console.WriteLine(failed == 0 ? $"PASS: every point within {tolerance:0.#} ms." : $"FAIL: {failed} of {results.Count} points outside {tolerance:0.#} ms.");
        return failed == 0 ? 0 : 1;
    }

    private static string Row(bool proxies, PointResult r, EncodedRun run, double tolerance)
    {
        static string Ms(double? value) => value is { } v ? v.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) : "-";
        var range = r.VideoRange is { } v ? $"[{Ms(v.Lo)}, {Ms(v.Hi)}]" : "";
        return $"{(proxies ? "on " : "off"),-8} {r.At,7:0.###}  {r.What,-24}  {Ms(r.VideoLateMs),7} {range,-15}  {r.FramesMatched,3}/{r.FramesCompared,-3}  "
               + $"{Ms(r.AudioLateMs),9}  {r.AudioScore,6:0.000}  {Ms(r.SyncMs),8}  {run.RestartMs,10}  {(r.Passes(tolerance) ? "PASS" : "FAIL")}";
    }
}

/// <summary>
/// Known answers, for checking the harness itself: a graph changed by a known amount must read as that amount.
/// --audio-shift-ms N delays the whole sound by N ms; --video-shift-ms N shows every video layer N ms late.
/// </summary>
internal static class Validation
{
    public static string Apply(string graph, Options options)
    {
        if (options.Has("audio-shift-ms"))
            graph = graph.Replace("[ao]", $",adelay={options.Number("audio-shift-ms", 0).ToString("0.###", CultureInfo.InvariantCulture)}:all=1[ao]", StringComparison.Ordinal);
        if (options.Has("video-shift-ms"))
        {
            var shift = options.Number("video-shift-ms", 0) / 1000;
            graph = System.Text.RegularExpressions.Regex.Replace(graph, @",setpts=PTS([+-][0-9.]+)/TB", m =>
                $",setpts=PTS{(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + shift).ToString("+0.######;-0.######", CultureInfo.InvariantCulture)}/TB");
        }

        return graph;
    }
}

/// <summary>The command line: a mode first, then --name value pairs (a name alone is a switch).</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Mode { get; private init; } = "test";

    public static Options Parse(string[] args)
    {
        var hasMode = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal);
        var options = new Options { Mode = hasMode ? args[0] : "test" };
        for (var i = hasMode ? 1 : 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument: {args[i]}");

            var name = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            options._values[name] = value;
        }

        return options;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string Get(string name, string? fallback = null) =>
        _values.TryGetValue(name, out var value) ? value : fallback ?? throw new ArgumentException($"--{name} is required.");

    public double Number(string name, double fallback) =>
        _values.TryGetValue(name, out var value) ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public (int Width, int Height) Size(string name, (int, int) fallback)
    {
        if (!_values.TryGetValue(name, out var value))
            return fallback;
        var parts = value.Split('x');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Where outputs go: --work, or a folder in the temporary folder.</summary>
    public string WorkFolder()
    {
        var folder = Path.GetFullPath(Get("work", Path.Combine(Path.GetTempPath(), "HandPeg-AvSync")));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
