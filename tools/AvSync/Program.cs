using System.Globalization;
using System.IO;
using HandPegApp.Compositor;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// The A/V sync harness on the GPU engine (tools\AvSyncTest.ps1 runs it; see tools\README.md). Opens a project in the app's
/// own view model and takes the state the player is given (MainViewModel.LivePreviewState). "test": at each moment it
/// draws a few seconds of the picture with a real compositor worker (GpuScenePlan, every frame exact, read back) and renders
/// the same seconds of sound with the real mixer (AudioProgram, AudioMixer.Render), writes both into one file on the
/// sequence's clock, and measures the picture and the sound of that against the files they came from. "session" plays in
/// real time instead, through a real GpuPreviewEngine and its WASAPI clock, in the steps the app takes (a seek while paused
/// then Play, a seek while playing, a change of speed, Loop's wrap), and compares each frame put on the screen with the
/// sound being heard then. No window is shown: the worker draws off screen, and the session's player is a window off the
/// desktop that never takes the focus.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The compositor's worker is started as this program again (WorkerProcess.ProgramPath is the running program), as
        // HandPeg starts HandPeg.exe: the same entry point, nothing else.
        if (args.Length > 0 && args[0] == CompositorWorker.Argument)
            return CompositorWorker.Run(args);

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var options = Options.Parse(args);
            Media.Ffmpeg = options.Get("ffmpeg", Path.Combine(AppPaths.Deps, "ffmpeg", "ffmpeg.exe"));
            Media.Ffprobe = options.Get("ffprobe", Path.Combine(AppPaths.Deps, "ffmpeg", "ffprobe.exe"));
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

    /// <summary>The engine's picture and sound at each moment, with and without the proxies, measured.</summary>
    private static int Test(Options options)
    {
        var points = options.Points();
        var proxyRuns = options.Get("proxies", "off,on").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim() == "on").ToList();
        var seconds = options.Number("seconds", 3);
        var tolerance = options.Number("tolerance", 15);
        var nominal = options.Number("nominal", 0.5);
        var longest = (int)options.Number("size", 1280);
        var work = options.WorkFolder();
        var mainProxy = options.Has("main-proxy") ? Path.GetFullPath(options.Get("main-proxy")) : null;

        // Known answers, for checking the harness itself: --video-shift-ms N draws every picture N ms late (the plan is
        // evaluated N ms earlier); --audio-shift-ms N renders the sound N ms late. The A/V column must read the difference.
        var shift = new Shift(options.Number("video-shift-ms", 0) / 1000, options.Number("audio-shift-ms", 0) / 1000);

        using var project = ProjectSession.Open(Path.GetFullPath(options.Get("project")), Console.Out);
        var missing = project.WaitForFacts(TimeSpan.FromSeconds(90));
        if (missing.Count > 0)
            Console.WriteLine($"Not known yet (the moments are worked out without): {string.Join(", ", missing)}");

        using var renderer = GpuRenderer.Start();
        Console.WriteLine($"Compositor: {renderer.Description}");
        if (shift.Any)
            Console.WriteLine($"Known answers: the picture {shift.VideoSeconds * 1000:0.###} ms late, the sound {shift.AudioSeconds * 1000:0.###} ms late: A/V should read {shift.ExpectedMs:+0.###;-0.###;0} ms.");

        var results = new List<(bool Proxies, PointResult Result, GpuRun Run)>();
        foreach (var proxies in proxyRuns)
        {
            var inUse = project.UseProxies(proxies, proxies ? mainProxy : null);
            Console.WriteLine();
            Console.WriteLine($"Proxies {(proxies ? "on" : "off")}: {(inUse.Count == 0 ? "none in use" : string.Join(", ", inUse.Select(p => $"{Path.GetExtension(p.Key)} -> {Path.GetFileName(p.Value)}")))}");
            if (proxies && inUse.Count == 0)
                Console.WriteLine("  (no proxy is ready for this project's files: the run with proxies is the same as without)");

            var state = project.State();
            var plan = renderer.Plan(state);
            var program = AudioProgram.Build(state);
            foreach (var at in points)
            {
                var length = Math.Min(seconds, plan.SequenceSeconds - at - 0.05);
                if (length < 1)
                {
                    Console.WriteLine($"  {at:0.###} s is too near the sequence's end ({plan.SequenceSeconds:0.###} s): skipped");
                    continue;
                }

                var output = Path.Combine(work, $"out_{(proxies ? "on" : "off")}_{at.ToString("0.###", CultureInfo.InvariantCulture)}.mkv");
                var run = renderer.Render(plan, program, state, at, length, output, longest, shift);
                var result = run.Error is { } error
                    ? new PointResult(at, "nothing", null, null, 0, 0, null, 0, error)
                    : Measurement.Measure(state, plan, run, at, length, shift);
                results.Add((proxies, result, run));
                Console.WriteLine("  " + Row(proxies, result, run, tolerance));
                if (!options.Has("keep") && File.Exists(output))
                    File.Delete(output);
            }
        }

        Console.WriteLine();
        Console.WriteLine("proxies  at (s)   what                      video (ms)                frames   audio (ms)  score   A/V (ms)  ready (ms)  result");
        foreach (var (proxies, result, run) in results)
            Console.WriteLine(Row(proxies, result, run, tolerance));
        foreach (var (_, result, _) in results.Where(r => r.Result.Notes.Length > 0))
            Console.WriteLine($"  at {result.At:0.###} s: {result.Notes}");

        var failed = results.Count(r => !r.Result.Passes(tolerance));
        var off = results.Count(r => r.Result.SyncMs is not { } sync || Math.Abs(sync - shift.ExpectedMs) >= nominal);
        Console.WriteLine(failed == 0 ? $"PASS: every point within {tolerance:0.#} ms." : $"FAIL: {failed} of {results.Count} points outside {tolerance:0.#} ms.");
        Console.WriteLine(off == 0 ? $"Nominal: every point within {nominal:0.0#} ms." : $"Not nominal: {off} of {results.Count} points {nominal:0.0#} ms or more off.");
        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    private static string Row(bool proxies, PointResult r, GpuRun run, double tolerance)
    {
        static string Ms(double? value) => value is { } v ? v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) : "-";
        var range = r.VideoRange is { } v ? $"[{Ms(v.Lo)}, {Ms(v.Hi)}]" : "";
        return $"{(proxies ? "on " : "off"),-8} {r.At,7:0.###}  {r.What,-24}  {Ms(r.VideoLateMs),7} {range,-17}  {r.FramesMatched,3}/{r.FramesCompared,-3}  "
               + $"{Ms(r.AudioLateMs),9}  {r.AudioScore,6:0.000}  {Ms(r.SyncMs),8}  {run.FirstFrameMs,10}  {(r.Passes(tolerance) ? "PASS" : "FAIL")}";
    }
}

/// <summary>Known answers (seconds): how late the picture and the sound are made on purpose, and the A/V that must then be read.</summary>
internal readonly record struct Shift(double VideoSeconds, double AudioSeconds)
{
    public bool Any => VideoSeconds != 0 || AudioSeconds != 0;

    public double ExpectedMs => (AudioSeconds - VideoSeconds) * 1000;
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

    /// <summary>The moments to measure at (--points, seconds of the sequence, comma-separated).</summary>
    public List<double> Points() => Get("points", "100,300,600,1500").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToList();

    /// <summary>Where outputs go: --work, or a folder in the temporary folder.</summary>
    public string WorkFolder()
    {
        var folder = Path.GetFullPath(Get("work", Path.Combine(Path.GetTempPath(), "HandPeg-AvSync")));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
