using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HandPegApp.Services;

namespace HandPegSeekBench;

/// <summary>
/// Instant Seek's bench (written in Pass 5.0-AudioAnd3D, run in Pass 5.0-Verify):
/// <c>HandPeg.SeekBench &lt;video&gt; [--ffprobe path] [--duration s] [--seeks n] [--cache-mb n] [--threads n] [--out file.json]</c>.
/// <para>A real compositor worker (this program again, as HandPeg starts HandPeg.exe) draws one media layer of the file;
/// each scenario publishes scenes as HandPeg would and times, from the counters in the control block, how soon a frame
/// is presented (any: the stand-in Instant Seek shows at once) and how soon the exact frame is.</para>
/// <list type="number">
/// <item>Cold seeks: the cache emptied before each, to a random moment (the chase from a key frame).</item>
/// <item>Warm seeks: random moments within the last 10 s seen (the cache and the pre-roll).</item>
/// <item>Frame steps back: 30 steps of one frame back from a random moment (no decode at all expected).</item>
/// <item>Scrub: a drag over 4 s of the file in 1 s at 250 positions a second, then let go.</item>
/// <item>Hover then click: the pointer's hint left 150 ms and 300 ms before the seek.</item>
/// <item>Playback: 10 s at 60 frames a second, playing; frames shown late are counted.</item>
/// </list>
/// Targets (ROADMAP Pass 5.0-Verify): first feedback under 8 ms, exact under 16 ms when cached or predicted, a cold chase
/// no slower than its frames over the decode rate, back steps at 0 decodes.
/// </summary>
public static class Program
{
    private const int Output = 1280, OutputHeight = 720;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == HandPegApp.Compositor.CompositorWorker.Argument)
            return HandPegApp.Compositor.CompositorWorker.Run(args);

        string? Option(string name) => Array.IndexOf(args, name) is var at && at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        double Number(string name, double fallback) => double.TryParse(Option(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        // A project played across its cuts (Pass 5.0-Release): its own mode, on the user's own data folder (the project's
        // seek indexes and proxies are what the app would use; the settings file is put back as it was).
        if (Option("--project") is { } project && File.Exists(project))
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            return ProjectPlayback.Run(args, Option, Number);
        }

        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.WriteLine("HandPeg.SeekBench <video> [--ffprobe path] [--duration s] [--seeks n] [--cache-mb n] [--threads n] [--out file.json]");
            Console.WriteLine("HandPeg.SeekBench --project <file.hproj> [--from s] [--seconds s] [--refresh hz] [--runs n] [--rest ms] [--hints horizon|legacy|none] [--cache-mb n] [--threads n]");
            return 2;
        }

        // Nothing of the user's is touched: HandPeg's data folder is one of the bench's own.
        Environment.SetEnvironmentVariable("HANDPEG_DATA_DIR", Path.Combine(Path.GetTempPath(), "HandPeg.SeekBench", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));

        var video = Path.GetFullPath(args[0]);

        var ffprobe = Option("--ffprobe") ?? FindOnPath("ffprobe.exe");
        var (duration, keys, rate) = ffprobe is not null ? Probe(ffprobe, video) : (0d, Array.Empty<double>(), 30.0);
        duration = Number("--duration", duration);
        if (duration <= 0)
        {
            Console.WriteLine("The file's length is not known: give --ffprobe or --duration.");
            return 2;
        }

        var seeks = (int)Number("--seeks", 40);
        var cacheMb = (long)Number("--cache-mb", 0);
        var threads = (int)Number("--threads", 0);
        Console.WriteLine(FormattableString.Invariant($"{Path.GetFileName(video)}: {duration:0.0} s, {rate:0.##} fps, {keys.Length} key frames (mean GOP {(keys.Length > 1 ? duration / keys.Length : double.NaN):0.00} s)"));

        using var client = new CompositorClient(-1, keepStandby: false, cacheMb switch { < 0 => -1, 0 => 0, _ => cacheMb << 20 }, threads);
        var clock = Stopwatch.StartNew();
        while (client.State == CompositorWorkerState.Starting && clock.ElapsedMilliseconds < 10_000)
            Thread.Sleep(5);
        if (client.State != CompositorWorkerState.Ready)
        {
            Console.WriteLine($"The worker did not start: {client.State} {client.Message}");
            return 1;
        }

        Console.WriteLine($"Worker: {client.Message}");
        client.SetSource(0, video, 0, keys.Length > 0 ? keys : null);
        var bench = new Bench(client, duration, 1 / rate);
        var results = new List<Result>
        {
            bench.ColdSeeks(seeks),
            bench.WarmSeeks(seeks),
            bench.StepsBack(30),
            bench.Scrub(),
            bench.HoverThenClick(seeks / 2, 150),
            bench.HoverThenClick(seeks / 2, 300),
            bench.Playback(10),
        };

        var report = new StringBuilder();
        foreach (var result in results)
            report.AppendLine(result.Describe());
        var stats = client.CacheStats;
        report.AppendLine(FormattableString.Invariant(
            $"Cache: {stats.Frames} frames, {stats.Bytes >> 20} of {stats.BudgetBytes >> 20} MB, {stats.Hits} hits, {stats.Misses} misses, {stats.PredictedHits} decoded ahead, {stats.Evictions} evicted; worst exact {stats.WorstExactLatencyMs:0.0} ms"));
        Console.Write(report.ToString());
        if (Option("--out") is { } outPath)
        {
            // A scenario with nothing to time (playback has no exact-frame latency) writes NaN as "NaN".
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            };
            File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(results.Select(r => r.ToJson()).ToList(), options));
            Console.WriteLine($"Written: {outPath}");
        }

        return 0;
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);

    /// <summary>The file's length, its key frames (seconds from its first video frame, as the worker counts) and its rate, by ffprobe.</summary>
    private static (double Duration, double[] Keys, double Rate) Probe(string ffprobe, string video)
    {
        static string Run(string exe, string arguments)
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return text;
        }

        var info = Run(ffprobe, $"-v error -select_streams v:0 -show_entries stream=avg_frame_rate:format=duration -of default=nw=1 \"{video}\"");
        double duration = 0, rate = 30;
        foreach (var line in info.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("duration=", StringComparison.Ordinal))
                _ = double.TryParse(line[9..], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            else if (line.StartsWith("avg_frame_rate=", StringComparison.Ordinal) && line[15..].Split('/') is [var n, var d]
                && double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0)
                rate = num / den;
        }

        var times = Run(ffprobe, $"-v error -select_streams v:0 -skip_frame nokey -show_entries frame=pts_time -of csv=p=0 \"{video}\"")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => double.TryParse(t.TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Where(double.IsFinite).Order().ToArray();
        if (times.Length > 0)
        {
            var first = times[0];
            for (var i = 0; i < times.Length; i++)
                times[i] -= first;
        }

        return (duration, times, rate);
    }

    private sealed record Result(string Name, List<double> FirstMs, List<double> ExactMs, int Decodes, int Approximate, int Total, string Note = "")
    {
        private static double Percentile(List<double> values, double p)
        {
            if (values.Count == 0)
                return double.NaN;
            var sorted = values.Order().ToList();
            return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        }

        public string Describe() => FormattableString.Invariant(
            $"{Name,-26} first p50 {Percentile(FirstMs, 0.5),6:0.0} p95 {Percentile(FirstMs, 0.95),6:0.0} | exact p50 {Percentile(ExactMs, 0.5),7:0.0} p95 {Percentile(ExactMs, 0.95),7:0.0} max {Percentile(ExactMs, 1),7:0.0} ms | {Decodes} decodes, {Approximate}/{Total} stand-ins {Note}");

        public object ToJson() => new
        {
            Name,
            FirstP50 = Percentile(FirstMs, 0.5), FirstP95 = Percentile(FirstMs, 0.95),
            ExactP50 = Percentile(ExactMs, 0.5), ExactP95 = Percentile(ExactMs, 0.95), ExactMax = Percentile(ExactMs, 1),
            Decodes, Approximate, Total, Note,
        };
    }

    private sealed class Bench(CompositorClient client, double duration, double frame)
    {
        private readonly Random _random = new(5005);

        private double RandomMoment() => _random.NextDouble() * Math.Max(duration - 1, 0.1);

        /// <summary>A scene of one media layer at a moment of the file, published; its sequence number.</summary>
        private long Show(double seconds, bool playing = false)
        {
            client.ClearScene();
            ref var scene = ref client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.OutputWidth, scene.OutputHeight) = (1920, 1080, Output, OutputHeight);
            (scene.PlayheadSeconds, scene.IsPlaying, scene.ActiveLayerCount) = (seconds, playing ? 1 : 0, 1);
            ref var layer = ref client.Layer(0);
            layer.SourceKind = (int)GpuSourceKind.Media;
            layer.SourceId = 0;
            layer.ClipKey = 1;
            layer.MediaSeconds = seconds;
            (layer.X, layer.Y, layer.Width, layer.Height) = (0, 0, 1920, 1080);
            (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
            (layer.CropRight, layer.CropBottom) = (1, 1);
            (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
            layer.Flags = GpuLayerDescriptor.FlagVisible;
            return client.Publish();
        }

        /// <summary>Times one moment: from the publish to the first frame presented for it, and to the exact one (at most 5 s).</summary>
        private (double First, double Exact, bool WasApproximate) Time(double seconds)
        {
            var started = Stopwatch.GetTimestamp();
            var sequence = Show(seconds);
            double first = double.NaN, exact = double.NaN;
            var approximate = false;
            ref var control = ref client.Memory.Control;
            var frames = Volatile.Read(ref control.FramesRendered);
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < 5)
            {
                var now = Volatile.Read(ref control.FramesRendered);
                if (now != frames && Volatile.Read(ref control.PresentedSequence) >= sequence)
                {
                    frames = now;
                    var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (double.IsNaN(first))
                        first = ms;
                    if (Volatile.Read(ref control.PresentedExact) != 0)
                    {
                        exact = ms;
                        break;
                    }

                    approximate = true;
                }

                Thread.SpinWait(200);
            }

            return (first, exact, approximate);
        }

        private int Decoded => Volatile.Read(ref client.Memory.Control.DecodedFrames);

        private Result Seeks(string name, IEnumerable<double> moments, Action? before = null)
        {
            var (firsts, exacts) = (new List<double>(), new List<double>());
            var (approximate, total, decoded) = (0, 0, Decoded);
            foreach (var moment in moments)
            {
                before?.Invoke();
                var (first, exact, wasApproximate) = Time(moment);
                if (double.IsFinite(first))
                    firsts.Add(first);
                if (double.IsFinite(exact))
                    exacts.Add(exact);
                approximate += wasApproximate ? 1 : 0;
                total++;
            }

            return new Result(name, firsts, exacts, Decoded - decoded, approximate, total);
        }

        public Result ColdSeeks(int count) =>
            Seeks("cold seeks", Enumerable.Range(0, count).Select(_ => RandomMoment()).ToList(), () =>
            {
                client.ClearFrameCache();
                Thread.Sleep(30);
            });

        public Result WarmSeeks(int count)
        {
            var around = RandomMoment();
            Time(around);
            Thread.Sleep(1500); // the pre-roll round it
            return Seeks("warm seeks (+-1.5 s)", Enumerable.Range(0, count).Select(_ => Math.Max(around + (_random.NextDouble() * 3 - 1.5), 0)).ToList());
        }

        public Result StepsBack(int steps)
        {
            var from = Math.Max(RandomMoment(), steps * frame + 1);
            Time(from);
            Thread.Sleep(800);
            return Seeks("frame steps back", Enumerable.Range(1, steps).Select(i => from - i * frame).ToList());
        }

        public Result Scrub()
        {
            var from = RandomMoment();
            var to = Math.Min(from + 4, duration - 0.1);
            var (firsts, exacts) = (new List<double>(), new List<double>());
            var decoded = Decoded;
            var approximate = 0;
            ref var control = ref client.Memory.Control;
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i <= 250; i++)
            {
                var at = from + (to - from) * i / 250.0;
                var publishedAt = Stopwatch.GetTimestamp();
                var sequence = Show(at);
                var frames = Volatile.Read(ref control.FramesRendered);
                while (Stopwatch.GetElapsedTime(publishedAt).TotalMilliseconds < 4)
                {
                    if (Volatile.Read(ref control.FramesRendered) != frames && Volatile.Read(ref control.PresentedSequence) >= sequence)
                    {
                        firsts.Add(Stopwatch.GetElapsedTime(publishedAt).TotalMilliseconds);
                        approximate += Volatile.Read(ref control.PresentedExact) == 0 ? 1 : 0;
                        break;
                    }

                    Thread.SpinWait(100);
                }
            }

            // Let go: the exact frame where it stopped.
            var (_, exact, _) = Time(to);
            if (double.IsFinite(exact))
                exacts.Add(exact);
            return new Result("scrub 4 s in 1 s", firsts, exacts, Decoded - decoded, approximate, 251,
                FormattableString.Invariant($"({Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms in all)"));
        }

        public Result HoverThenClick(int count, int restMs)
        {
            var (firsts, exacts) = (new List<double>(), new List<double>());
            var (approximate, decoded) = (0, Decoded);
            Span<GpuSeekHint> hint = stackalloc GpuSeekHint[1];
            for (var i = 0; i < count; i++)
            {
                client.ClearFrameCache();
                Thread.Sleep(30);
                var target = RandomMoment();
                hint[0] = new GpuSeekHint { SourceId = 0, Kind = (int)GpuSeekHintKind.Pointer, MediaSeconds = target };
                client.SetHints(hint);
                Thread.Sleep(restMs);
                var (first, exact, wasApproximate) = Time(target);
                if (double.IsFinite(first))
                    firsts.Add(first);
                if (double.IsFinite(exact))
                    exacts.Add(exact);
                approximate += wasApproximate ? 1 : 0;
            }

            client.SetHints([]);
            return new Result(FormattableString.Invariant($"hover {restMs} ms then click"), firsts, exacts, Decoded - decoded, approximate, count);
        }

        public Result Playback(double seconds)
        {
            var from = Math.Min(RandomMoment(), Math.Max(duration - seconds - 1, 0));
            var (late, total, decoded) = (0, 0, Decoded);
            var firsts = new List<double>();
            ref var control = ref client.Memory.Control;
            var started = Stopwatch.GetTimestamp();
            for (var n = 0; n < seconds * 60; n++)
            {
                var due = started + (long)(n / 60.0 * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < due)
                    Thread.SpinWait(100);
                var publishedAt = Stopwatch.GetTimestamp();
                var sequence = Show(from + n / 60.0, playing: true);
                var frames = Volatile.Read(ref control.FramesRendered);
                while (Stopwatch.GetElapsedTime(publishedAt).TotalMilliseconds < 16)
                {
                    if (Volatile.Read(ref control.FramesRendered) != frames && Volatile.Read(ref control.PresentedSequence) >= sequence)
                    {
                        firsts.Add(Stopwatch.GetElapsedTime(publishedAt).TotalMilliseconds);
                        late += Volatile.Read(ref control.PresentedExact) == 0 ? 1 : 0;
                        break;
                    }

                    Thread.SpinWait(100);
                }

                total++;
            }

            return new Result(FormattableString.Invariant($"playback {seconds:0} s at 60 fps"), firsts, [], Decoded - decoded, late, total, "(stand-ins = frames shown late)");
        }
    }
}
