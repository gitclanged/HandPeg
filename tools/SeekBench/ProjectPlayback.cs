using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;
using HandPegApp.Tools.AvSync;

namespace HandPegSeekBench;

/// <summary>
/// Playback across a project's cuts (Pass 5.0-Release, ROADMAP Task 1.1): <c>HandPeg.SeekBench --project file.hproj
/// [--from s] [--seconds s] [--refresh hz] [--runs n] [--rest ms] [--hints horizon|legacy|none] [--cache-mb n] [--threads n]</c>.
/// <para>The project is opened in the app's own view model (the A/V harness's <c>ProjectSession</c>: the settings file put
/// back byte for byte, the project only read), and the state the player is given (MainViewModel.LivePreviewState) is
/// planned as the GPU engine plans it. A real compositor worker then plays the plan in real time as the engine's frame
/// pump does: a turn every refresh, the scene evaluated one refresh ahead, published only when the timeline's frame
/// changed (CanvasFrameOf), the worker's hints left as the engine leaves them (<see cref="GpuHintWriter"/>, or the
/// engine's hints before Pass 5.0-Release: the next cut while paused, nothing while playing). Each run starts cold (the
/// cache emptied), rests paused at the start for a while, then plays.</para>
/// <para>A frame is <b>late</b> when the first picture the worker drew for it lacked an exact frame (a stand-in: the
/// player shows the frame before); its <b>wait</b> is how long until the exact picture came (or the next frame was due).
/// Late frames are counted over the whole run and in the half second after each cut.</para>
/// </summary>
internal static class ProjectPlayback
{
    private const double AfterCut = 0.5;

    public static int Run(string[] args, Func<string, string?> option, Func<string, double, double> number)
    {
        var project = Path.GetFullPath(option("--project")!);
        var mode = option("--hints") ?? "horizon";
        var refresh = number("--refresh", 60);
        var runs = (int)number("--runs", 3);
        var restMs = (int)number("--rest", 1500);
        var cacheMb = (long)number("--cache-mb", 0);
        var threads = (int)number("--threads", 0);

        using var session = ProjectSession.Open(project, Console.Out);
        var missing = session.WaitForFacts(TimeSpan.FromSeconds(60));
        if (missing.Count > 0)
            Console.WriteLine("Not known yet: " + string.Join(", ", missing));
        var state = session.State();
        if (Array.IndexOf(args, "--strip") >= 0)
            return Strip(state, runs);

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
        using var broker = new GpuResourceBroker(client, bakeOnCallingThread: true);
        var plan = GpuScenePlan.Create(state, broker);
        var fps = state.Timeline.FrameRate.IsValid ? state.Timeline.FrameRate.Fps : 30;
        var from = Math.Clamp(number("--from", 0), 0, Math.Max(plan.SequenceSeconds - 0.1, 0));
        var seconds = Math.Min(number("--seconds", plan.SequenceSeconds - from), plan.SequenceSeconds - from);
        var cuts = Cuts(plan, from, from + seconds);
        Console.WriteLine(FormattableString.Invariant(
            $"{Path.GetFileName(project)}: {plan.SequenceSeconds:0.000} s at {fps:0.##} fps, {plan.CanvasWidth} x {plan.CanvasHeight}; {cuts.Count} cuts in {from:0.000}..{from + seconds:0.000} s; hints: {mode}; refresh {refresh:0} Hz"));
        Console.WriteLine("Cuts: " + string.Join(", ", cuts.Select(c => c.ToString("0.000", CultureInfo.InvariantCulture))));

        var player = new Player(client, plan, fps, refresh, mode) { Trace = Array.IndexOf(args, "--trace") >= 0 };
        var report = new StringBuilder();
        var totals = (Frames: 0, Late: 0, LateAtCuts: 0);
        for (var run = 1; run <= runs; run++)
        {
            client.ClearFrameCache();
            Thread.Sleep(50);
            var result = player.Play(from, seconds, restMs, cuts);
            totals = (totals.Frames + result.Frames, totals.Late + result.Late, totals.LateAtCuts + result.LateAtCuts);
            report.AppendLine(FormattableString.Invariant($"run {run}: {result.Describe()}"));
            foreach (var crossing in result.Crossings)
                report.AppendLine(FormattableString.Invariant($"    cut {crossing.Cut,8:0.000} s: {crossing.Late,2} late, worst wait {crossing.WorstWaitMs,6:0.0} ms"));
        }

        var counters = client.DecodeCounters;
        var stats = client.CacheStats;
        report.AppendLine(FormattableString.Invariant(
            $"All runs: {totals.Late} of {totals.Frames} frames late, {totals.LateAtCuts} within {AfterCut:0.0} s after a cut; decoders opened {counters.Opens}, seeks {counters.Seeks}, frames decoded {counters.Decoded}"));
        report.AppendLine(FormattableString.Invariant(
            $"Cache: {stats.Frames} frames, {stats.Bytes >> 20} of {stats.BudgetBytes >> 20} MB, {stats.PredictedHits} decoded ahead, {stats.Evictions} evicted"));
        Console.Write(report.ToString());
        return 0;
    }

    /// <summary>
    /// The Overview Bar's sheet of the project (--strip): drawn by a GpuProgramStrip of its own each run (its worker started
    /// for it, nothing kept from the run before), cold and then after its file was opened ahead (Prewarm, as a hover or a
    /// drag on a fast drive does): how long until the full sheet.
    /// </summary>
    private static int Strip(SequenceExportState state, int runs)
    {
        var settings = AppSettings.Current;
        foreach (var ahead in new[] { false, true })
        {
            for (var run = 1; run <= runs; run++)
            {
                using var strip = new GpuProgramStrip();
                if (ahead)
                {
                    strip.Prewarm(state.LocalMediaPath);
                    ProjectSession.Pump(TimeSpan.FromMilliseconds(1500));
                }

                var clock = Stopwatch.StartNew();
                var drawing = strip.DrawNowAsync(state, settings.ThumbnailIntervalSeconds, settings.ShowTimelineThumbnails == true, CancellationToken.None);
                ProjectSession.WaitUntil(() => drawing.IsCompleted, TimeSpan.FromSeconds(60));
                var sheet = drawing.IsCompletedSuccessfully ? drawing.Result : null;
                Console.WriteLine(FormattableString.Invariant(
                    $"strip {(ahead ? "opened ahead" : "cold")} run {run}: {(sheet is null ? "no sheet" : $"{sheet.Count} pictures ({strip.LastDrawn} drawn)")} in {clock.ElapsedMilliseconds} ms"));
            }
        }

        return 0;
    }

    /// <summary>Every cut in a stretch of the sequence (where a file-reading layer comes on or the main video goes on to another clip).</summary>
    private static List<double> Cuts(GpuScenePlan plan, double from, double to)
    {
        var cuts = new List<double>();
        for (var t = plan.NextCutAfter(from); double.IsFinite(t) && t <= to; t = plan.NextCutAfter(t))
            cuts.Add(t);
        return cuts;
    }

    private sealed record Crossing(double Cut, int Late, double WorstWaitMs);

    private sealed record Outcome(int Frames, int Late, int LateAtCuts, int LongestRun, double WorstWaitMs, int Decoded, List<Crossing> Crossings)
    {
        public string Describe() => FormattableString.Invariant(
            $"{Late} of {Frames} frames late ({LateAtCuts} within {AfterCut:0.0} s after a cut), longest run of late frames {LongestRun}, worst wait {WorstWaitMs:0.0} ms, {Decoded} frames decoded");
    }

    /// <summary>The engine's frame pump, without a window: the worker draws off screen, its presents counted from the control block.</summary>
    private sealed class Player(CompositorClient client, GpuScenePlan plan, double fps, double refresh, string mode)
    {
        private readonly GpuSeekHint[] _hints = new GpuSeekHint[CompositorProtocol.MaxHints];
        private readonly GpuMediaHint[] _media = new GpuMediaHint[CompositorProtocol.MaxHints];
        private readonly GpuHintWriter _writer = new();

        /// <summary>Every late frame and every hint written, on the console (--trace).</summary>
        public bool Trace { get; init; }

        private long Publish(double at, bool playing)
        {
            client.ClearScene();
            ref var header = ref client.Scene;
            var layers = MemoryMarshal.CreateSpan(ref client.Layer(0), CompositorProtocol.MaxLayers);
            plan.Evaluate(at, ref header, layers);
            header.PlayheadSeconds = at;
            header.IsPlaying = playing ? 1 : 0;
            // The player's own size: a pane of about 720 pixels on the canvas's long side.
            var scale = 720.0 / Math.Max(plan.CanvasWidth, plan.CanvasHeight);
            (header.OutputWidth, header.OutputHeight) = (Math.Max((int)(plan.CanvasWidth * scale), 16), Math.Max((int)(plan.CanvasHeight * scale), 16));
            header.Flags |= FrameSceneHeader.FlagLetterbox;
            return client.Publish();
        }

        /// <summary>The worker's hints at a moment, as the engine leaves them.</summary>
        private void Hint(double at, bool playing)
        {
            switch (mode)
            {
                case "none":
                    client.SetHints([]);
                    break;
                case "legacy":
                    // Before Pass 5.0-Release: the first frame after the next cut, while paused only.
                    if (playing)
                        return;
                    var cut = plan.NextCutAfter(at);
                    var count = 0;
                    if (double.IsFinite(cut))
                    {
                        var found = plan.MediaAt(cut + 1e-3, _media);
                        for (var i = 0; i < found; i++)
                            _hints[count++] = new GpuSeekHint { SourceId = _media[i].SourceId, Kind = (int)GpuSeekHintKind.CutEntry, MediaSeconds = _media[i].MediaSeconds };
                    }

                    client.SetHints(_hints.AsSpan(0, count));
                    break;
                default:
                    if (_writer.Write(plan, at, 1, _hints, out var written))
                    {
                        client.SetHints(_hints.AsSpan(0, written));
                        if (Trace)
                        {
                            Console.WriteLine(FormattableString.Invariant($"  {at,8:0.000} hints: ") + string.Join(" ", _hints.Take(written).Select(h =>
                                FormattableString.Invariant($"[{(GpuSeekHintKind)h.Kind} {h.MediaSeconds:0.000}+{h.Span:0.00} at {h.At:0.000}]"))));
                        }
                    }
                    break;
            }
        }

        public Outcome Play(double from, double seconds, int restMs, List<double> cuts)
        {
            ref var control = ref client.Memory.Control;
            _writer.Reset();
            plan.PrepareAround(from);

            // Paused at the start: the first frame, then the rest (the pre-roll and the hints of a paused player).
            Hint(from, playing: false);
            var first = Publish(from, playing: false);
            var waited = Stopwatch.StartNew();
            while (Volatile.Read(ref control.PresentedSequence) < first && waited.ElapsedMilliseconds < 10_000)
                Thread.Sleep(1);
            Thread.Sleep(restMs);
            if (Trace)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"  rested {restMs} ms: decoded {Volatile.Read(ref control.DecodedFrames)}, seeks {Volatile.Read(ref control.DecoderSeeks)}, opens {Volatile.Read(ref control.DecoderOpens)}, cached {Volatile.Read(ref control.CachedFrames)}, busy lanes {Volatile.Read(ref control.BusyDecodeThreads)}"));
            }

            var decodedBefore = Volatile.Read(ref control.DecodedFrames);
            var crossings = cuts.Select(c => (Cut: c, Late: 0, Worst: 0.0)).ToArray();
            var (frames, late, lateAtCuts, run, longest, worst) = (0, 0, 0, 0, 0, 0.0);
            var turn = 1 / refresh;
            var started = Stopwatch.GetTimestamp();
            var lastFrame = long.MinValue;
            for (var n = 0; ; n++)
            {
                var due = started + (long)(n * turn * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < due)
                    Thread.SpinWait(50);

                // The moment on screen one refresh from now (the engine: the audio clock plus a refresh).
                var at = from + n * turn + turn;
                if (at >= from + seconds)
                    break;
                var frame = (long)Math.Floor(at * fps + 1e-6);
                Hint(at, playing: true);
                if (frame == lastFrame)
                    continue;
                lastFrame = frame;

                var publishedAt = Stopwatch.GetTimestamp();
                var sequence = Publish(at, playing: true);
                var rendered = Volatile.Read(ref control.FramesRendered);
                var (presented, exact) = (false, false);
                var wait = 0.0;

                // Until the next frame is due: the first picture of this one, and (late) how soon the exact one comes.
                var until = started + (long)((n + Math.Max(Math.Round(refresh / fps), 1)) * turn * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < until)
                {
                    var now = Volatile.Read(ref control.FramesRendered);
                    if (now != rendered && Volatile.Read(ref control.PresentedSequence) >= sequence)
                    {
                        rendered = now;
                        var isExact = Volatile.Read(ref control.PresentedExact) != 0;
                        if (!presented)
                        {
                            presented = true;
                            exact = isExact && Stopwatch.GetElapsedTime(publishedAt).TotalSeconds < turn;
                        }

                        if (isExact)
                        {
                            wait = Stopwatch.GetElapsedTime(publishedAt).TotalMilliseconds;
                            break;
                        }
                    }

                    Thread.SpinWait(50);
                }

                if (!presented || !exact && wait == 0)
                    wait = Stopwatch.GetElapsedTime(publishedAt).TotalMilliseconds;
                frames++;
                if (exact)
                {
                    run = 0;
                    continue;
                }

                late++;
                if (Trace)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"  {at,8:0.000} late (wait {wait:0.0} ms): busy lanes {Volatile.Read(ref control.BusyDecodeThreads)}, decoded {Volatile.Read(ref control.DecodedFrames) - decodedBefore}, seeks {Volatile.Read(ref control.DecoderSeeks)}, opens {Volatile.Read(ref control.DecoderOpens)}, cached {Volatile.Read(ref control.CachedFrames)}, render {control.LastFrameCpuMs:0.0} ms"));
                }
                longest = Math.Max(longest, ++run);
                worst = Math.Max(worst, wait);
                for (var k = 0; k < crossings.Length; k++)
                {
                    if (at >= crossings[k].Cut - 1e-6 && at < crossings[k].Cut + AfterCut)
                    {
                        crossings[k].Late++;
                        crossings[k].Worst = Math.Max(crossings[k].Worst, wait);
                        lateAtCuts++;
                        break;
                    }
                }
            }

            return new Outcome(frames, late, lateAtCuts, longest, worst, Volatile.Read(ref control.DecodedFrames) - decodedBefore,
                crossings.Select(c => new Crossing(c.Cut, c.Late, c.Worst)).ToList());
        }
    }
}
