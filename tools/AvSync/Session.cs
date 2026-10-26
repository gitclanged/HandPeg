using System.Globalization;
using System.IO;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// The steps the app takes within a session, played by libmpv in real time (no window, no speakers), each as the app
/// sends it (Services/MpvPlayer.cs: the seek first, then its graph):
///   paused  - a seek while paused with the quick graph without sound, the graph with sound 300 ms later (ArmSoundSettle:
///             laid out from the moment, with an exact seek to it), then Play;
///   seek    - a seek while playing, with the graph with sound laid out from where it lands;
///   rebuild - playing on past the moment the graph holds (LiveGraph.RebuildAt), then the graph built again for where
///             playback is, with an exact seek there.
/// After each step mpv's clocks are read every 20 ms for a few seconds: where the sound it plays is (audio-pts) against
/// where the picture is (time-pos), and its own A/V difference (avsync).
///
/// mpv's clocks say whether the sound and the picture it plays are lined up by their time stamps, not whether those time
/// stamps say the right part of the file: that is what "test" measures, on the output itself. Each step here is a seek
/// with a graph laid out from the seek's target, as "test" plays it, so the two together cover the session.
/// </summary>
internal static class Session
{
    public static int Run(Options options)
    {
        var tolerance = options.Number("tolerance", 15);
        var surface = options.Size("surface", (1280, 720));
        var libmpv = options.Get("libmpv", Path.Combine(HandPegApp.Services.AppPaths.Deps, "mpv", "libmpv-2.dll"));
        var points = options.Get("points", "100,300,600,1500").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToList();
        var proxies = options.Has("proxies") && options.Get("proxies") == "on";

        using var project = ProjectSession.Open(Path.GetFullPath(options.Get("project")), Console.Out);
        project.WaitForFacts(TimeSpan.FromSeconds(90));
        project.UseProxies(proxies, proxies && options.Has("main-proxy") ? Path.GetFullPath(options.Get("main-proxy")) : null);

        using var mpv = MpvSession.Start(libmpv,
        [
            ("config", "no"), ("terminal", "no"), ("idle", "yes"), ("keep-open", "yes"), ("pause", "yes"),
            ("hr-seek", "yes"), ("hr-seek-framedrop", "yes"), ("vd-lavc-fast", "yes"), ("audio-display", "no"), ("sid", "no"),
            ("profile", "fast"), ("vd-lavc-threads", "0"), ("hwdec", "no"), ("vo", "null"), ("ao", "null"),
        ]);
        mpv.Command("loadfile", project.PlayerSource, "replace");
        if (mpv.WaitRestarts(0, TimeSpan.FromSeconds(60)) < 0)
            throw new TimeoutException("The player's source did not open within a minute.");

        // The clocks' own bias, with nothing of the app's in the way: the main video played as the file is, without a graph.
        // (time-pos moves in whole frames, audio-pts does not, so a file in step reads some milliseconds apart.)
        var rows = new List<(string Step, double At, Reading Reading)>();
        var plainAt = options.Number("plain-at", 20);
        Send(mpv, "", plainAt);
        mpv.SetProperty("pause", "no");
        var plain = Read(mpv, TimeSpan.FromSeconds(3));
        rows.Add(("plain", plainAt, plain));
        mpv.SetProperty("pause", "yes");

        foreach (var at in points)
        {
            // Paused: a seek with the quick graph (its restart is the wait for the frame), the sound settling 300 ms
            // later, then Play.
            var quick = Send(mpv, project.Graph(at, surface, sound: false).Text, at);
            Thread.Sleep(300);
            Send(mpv, project.Graph(at, surface).Text, at);
            mpv.SetProperty("pause", "no");
            rows.Add(("paused", at, Read(mpv, TimeSpan.FromSeconds(3)) with { RestartMs = quick }));

            // A seek while playing, 20 s on.
            var jump = at + 20;
            var playing = Send(mpv, project.Graph(jump, surface).Text, jump);
            rows.Add(("seek", jump, Read(mpv, TimeSpan.FromSeconds(3)) with { RestartMs = playing }));

            mpv.SetProperty("pause", "yes");
        }

        // Played on past what the graph holds (a clip about to end or begin), then built again for where playback is.
        // (A graph holds what starts within LiveLookAhead of where it was built, so it is rebuilt while playing only when a
        // clip begins more than 30 s after it was built: 151.5 s suits the Pass 4.1A test project, whose second layer clip
        // begins at 182.2 s.)
        var rebuilds = options.Get("rebuild-from", "151.5").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.Parse(p, CultureInfo.InvariantCulture));
        foreach (var from in rebuilds)
        {
            var graph = project.Graph(from, surface);
            if (!double.IsFinite(graph.RebuildAt) || graph.RebuildAt - from > 35)
            {
                Console.WriteLine($"  the graph for {from:0.###} s holds to {graph.RebuildAt:0.###} s: no rebuild within 35 s, skipped");
                continue;
            }

            Send(mpv, graph.Text, from);
            mpv.SetProperty("pause", "no");
            mpv.WaitPosition(p => p >= graph.RebuildAt, TimeSpan.FromSeconds(45));
            var now = mpv.Position;
            var rebuilt = Send(mpv, project.Graph(now, surface).Text, now);
            rows.Add(("rebuild", now, Read(mpv, TimeSpan.FromSeconds(3)) with { RestartMs = rebuilt }));
            mpv.SetProperty("pause", "yes");
        }

        Console.WriteLine();
        Console.WriteLine($"proxies {(proxies ? "on" : "off")}");
        Console.WriteLine("step      at (s)   samples  audio-pts - time-pos (ms): median  worst   against plain (ms)  avsync (ms): median  worst   restart (ms)  result");
        var failed = 0;
        foreach (var (step, at, r) in rows)
        {
            var apart = r.Median - plain.Median;
            var passes = r.Samples > 10 && Math.Abs(apart) < tolerance && Math.Abs(r.AvsyncMedian) < tolerance;
            failed += passes ? 0 : 1;
            Console.WriteLine($"{step,-8} {at,8:0.###}  {r.Samples,7}  {r.Median,34:+0.0;-0.0;0.0}  {r.Worst,6:+0.0;-0.0;0.0}  {apart,18:+0.0;-0.0;0.0}  {r.AvsyncMedian,19:+0.0;-0.0;0.0}  {r.AvsyncWorst,6:+0.0;-0.0;0.0}  {(step == "plain" ? "" : r.RestartMs.ToString(CultureInfo.InvariantCulture)),12}  {(passes ? "PASS" : "FAIL")}");
        }

        Console.WriteLine(failed == 0 ? $"PASS: every step within {tolerance:0.#} ms." : $"FAIL: {failed} of {rows.Count} steps outside {tolerance:0.#} ms.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// A graph as MpvPlayer sends it with a seek: the exact seek to its moment first, then the graph. Returns the
    /// milliseconds from the seek to playback restarting (-1: not within 30 s).
    /// </summary>
    private static long Send(MpvSession mpv, string graph, double at)
    {
        var restarts = mpv.Restarts;
        mpv.Seek((long)Math.Round(at * 1000), exact: true);
        mpv.SetProperty("lavfi-complex", graph);
        return mpv.WaitRestarts(restarts, TimeSpan.FromSeconds(30));
    }

    private readonly record struct Reading(int Samples, double Median, double Worst, double AvsyncMedian, double AvsyncWorst)
    {
        /// <summary>Milliseconds from the step's seek to playback restarting.</summary>
        public long RestartMs { get; init; }
    }

    /// <summary>mpv's clocks every 20 ms for a while, from 0.4 s after the step (the first moments after a restart may settle).</summary>
    private static Reading Read(MpvSession mpv, TimeSpan time)
    {
        Thread.Sleep(400);
        var (offsets, avsync) = (new List<double>(), new List<double>());
        var until = mpv.Now + (long)time.TotalMilliseconds;
        while (mpv.Now < until)
        {
            if (Number(mpv.Get("audio-pts")) is { } audio && Number(mpv.Get("time-pos")) is { } video)
                offsets.Add((audio - video) * 1000);
            if (Number(mpv.Get("avsync")) is { } sync)
                avsync.Add(sync * 1000);
            Thread.Sleep(20);
        }

        static double Median(List<double> values) => values.Count == 0 ? double.NaN : values.Order().ElementAt(values.Count / 2);
        static double Worst(List<double> values) => values.Count == 0 ? double.NaN : values.MaxBy(Math.Abs);
        return new Reading(offsets.Count, Median(offsets), Worst(offsets), Median(avsync), Worst(avsync));
    }

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
