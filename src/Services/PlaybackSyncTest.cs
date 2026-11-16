using System.Diagnostics;
using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- Playback & A/V Sync Test -----
//
// Optional (first-run second screen; Settings > Export & Hardware > GPU & NPU Roles, PlaybackSyncCard). Plays a synthetic sequence (a
// title sweeping over black, no media) with a dedicated GpuPreviewEngine (player: false) on a visible swap chain so DXGI frame
// statistics give true present times, following tools\AvSync's steps: play from pause, 2x, then 1x. Measures per-frame A/V against the
// audio clock (PlaybackSyncSampler) and frame gaps; stores AppSettings.LastPlaybackSyncTest. Under 20 s.

/// <summary>One test step: frame count, A/V (ms, positive = video ahead), drops, and pass state.</summary>
public sealed class PlaybackSyncStep
{
    public string Name { get; set; } = "";

    public int Frames { get; set; }

    public double MedianMs { get; set; }

    public double WorstMs { get; set; }

    public int Dropped { get; set; }

    /// <summary>Frames off by at least <see cref="PlaybackSyncTest.ToleranceMs"/> (e.g. one refresh late: a hitch).</summary>
    public int Off { get; set; }

    /// <summary>Median within <see cref="PlaybackSyncTest.ToleranceMs"/>, at most 1 in 20 frames off, with enough samples.</summary>
    public bool InSync { get; set; }

    /// <summary>No more than one frame in a hundred a hitch (dropped or off).</summary>
    public bool Smooth { get; set; }

    /// <summary>The frames dropped or off.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Hitches => Dropped + Off;
}

/// <summary>The test's result as it is kept (AppSettings.LastPlaybackSyncTest).</summary>
public sealed class PlaybackSyncResult
{
    /// <summary>When it ran (UTC, ISO 8601).</summary>
    public string MeasuredAt { get; set; } = "";

    /// <summary>The display's refresh rate as DWM gave it (0 not known).</summary>
    public double RefreshHz { get; set; }

    /// <summary>Measured pump-to-display latency (GpuViewport.DisplayLeadSeconds), ms; null if unmeasured.</summary>
    public double? DisplayLeadMs { get; set; }

    /// <summary>True when DXGI frame statistics provided present times (rather than assuming the DWM grid).</summary>
    public bool FrameStatistics { get; set; }

    /// <summary>The sound device the clock was read from ("" none: the processor's counter).</summary>
    public string AudioDevice { get; set; } = "";

    public List<PlaybackSyncStep> Steps { get; set; } = [];

    /// <summary>Why it could not finish ("" it did).</summary>
    public string Error { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Passed => Error.Length == 0 && Steps.Count > 0 && Steps.TrueForAll(s => s.InSync && s.Smooth);
}

/// <summary>Where the test is: 0 to 1 over the whole run, and the step.</summary>
public readonly record struct PlaybackSyncProgress(double Fraction, string Step);

public static class PlaybackSyncTest
{
    /// <summary>No frame may be this far from the sound, ms (tools\AvSync's --tolerance).</summary>
    public const double ToleranceMs = 15;

    /// <summary>Test sequence length, rate, start position and per-step measurement window (starting 0.4 s in).</summary>
    internal const double SequenceSeconds = 24, Fps = 30, StartAt = 1, MeasureSeconds = 4, Settle = 0.4;

    // The steps' names and rates, in order.
    internal static readonly (string Name, double Rate)[] StepPlan = [("Play", 1), ("2x", 2), ("1x", 1)];

    /// <summary>
    /// Test sequence: 1280x720 at 30 fps, black with a keyframed title sweeping once per second, no files or audio; every frame differs.
    /// </summary>
    public static SequenceExportState TestSequence()
    {
        const int width = 1280, height = 720;
        var document = new TimelineDocument { Width = width, Height = height, FrameRate = FrameRate.Fps30 };
        var words = new LayerState
        {
            Kind = LayerKind.Text, Name = "Sync", TextContent = "HandPeg", FontFamily = "Segoe UI", FontSize = 96, FontColor = "#FFFFFF",
            OutlineColor = "#000000", OutlineThickness = 0, PositionX = 0.05, PositionY = 0.38, SizeWidth = 0.3,
        };
        var layout = TextLayout.Measure(words.TextContent, words.FontFamily, words.FontSize, words.OutlineThickness);
        (words.ImageWidth, words.ImageHeight) = (layout.Width, layout.Height);
        words.KeysX = [.. Enumerable.Range(0, (int)SequenceSeconds + 1).Select(s => new Keyframe(s, s % 2 == 0 ? 0.05 : 0.65, EasingType.Linear))];
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        track.Clips.Add(new TimelineClip { ContentKind = LayerKind.Text, Start = 0, End = SequenceSeconds, State = words });
        document.Tracks.Add(track);
        return new SequenceExportState
        {
            Timeline = document,
            SourceFrameRate = Fps,
            RequestedSize = (width, height),
            OutputSize = (width, height),
            CaptionLayer = Layer.FromState(new LayerState
            {
                Kind = LayerKind.Captions, Name = "Subtitles", PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.9, SizeHeight = 0.2, LockAspectRatio = false,
                SourceWidth = 0.25, SourceHeight = 0.25,
            }, width, height, width, height),
        };
    }

    /// <summary>
    /// Runs on <paramref name="surface"/> (visible native window; UI thread, pumped while waiting). Returns the result
    /// (<see cref="PlaybackSyncResult.Error"/> if the player failed to start); throws OperationCanceledException on cancel.
    /// </summary>
    public static async Task<PlaybackSyncResult> RunAsync(nint surface, IProgress<PlaybackSyncProgress>? progress, CancellationToken token)
    {
        var total = 1.5 + StepPlan.Length * (MeasureSeconds + Settle);
        var clock = Stopwatch.StartNew();

        // The card redrawn at most four times a second (its own drawing is not what is measured).
        var (reported, said) = (-1.0, "");
        void Report(string step)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (step == said && now - reported < 0.25)
                return;
            (reported, said) = (now, step);
            progress?.Report(new(Math.Clamp(now / total, 0, 0.99), step));
        }

        Report("Starting the player");
        var engine = await GpuPreviewEngine.StartAsync(surface, player: false);
        if (engine is null)
            return new PlaybackSyncResult { MeasuredAt = Now(), Error = $"The player could not start: {GpuPreviewEngine.LastStartFailure}" };

        using (engine)
        {
            string? faulted = null;
            engine.Faulted += why => faulted = why;
            engine.Muted = true;
            engine.SetState(TestSequence());
            clock.Restart();

            // Waits a while as the UI thread does meanwhile (the plan's textures handed over), the progress moving.
            async Task Idle(double seconds, string step)
            {
                var until = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < until)
                {
                    token.ThrowIfCancellationRequested();
                    if (faulted is not null)
                        throw new InvalidOperationException(faulted);
                    engine.PrepareAround(engine.Position);
                    Report(step);
                    await Task.Delay(30, token);
                }
            }

            // The device opens on the engine's own thread: given a moment.
            var opening = Stopwatch.StartNew();
            while (engine.Audio.DeviceName == "Starting" && opening.Elapsed.TotalSeconds < 5)
                await Idle(0.05, "Starting the player");

            using var sampler = PlaybackSyncSampler.Start(engine);
            var windows = new List<(string Name, double Rate, long From, long To)>();
            try
            {
                engine.Seek(StartAt);
                await Idle(Settle, "Starting the player");
                foreach (var (name, rate) in StepPlan)
                {
                    engine.Rate = rate;
                    if (!engine.IsPlaying)
                        engine.Play();
                    var from = Stopwatch.GetTimestamp() + (long)(Settle * Stopwatch.Frequency);
                    await Idle(MeasureSeconds + Settle, name == "Play" ? "Playing from paused" : $"Playing at {name}");
                    windows.Add((name, rate, from, Stopwatch.GetTimestamp()));
                }
            }
            catch (InvalidOperationException ex)
            {
                AppLog.Write("Playback & A/V Sync Test: the player stopped", ex);
            return new PlaybackSyncResult { MeasuredAt = Now(), Error = $"The player stopped: {ex.Message}" };
            }
            finally
            {
                engine.Pause();
                sampler.Stop();
            }

            var refreshMs = sampler.RefreshMs;
            var result = new PlaybackSyncResult
            {
                MeasuredAt = Now(),
                RefreshHz = refreshMs > 0 ? Math.Round(1000 / refreshMs, 2) : 0,
                DisplayLeadMs = double.IsFinite(engine.Viewport.DisplayLeadSeconds) ? Math.Round(engine.Viewport.DisplayLeadSeconds * 1000, 3) : null,
                FrameStatistics = sampler.ReportedFrames >= 5,
                AudioDevice = engine.Audio.HasDevice ? engine.Audio.DeviceName : "",
            };
            foreach (var (name, rate, from, to) in windows)
            {
                var (offsets, moments) = sampler.FramesWithin(from, to);
                result.Steps.Add(Step(name, offsets, moments, AllowedStep(refreshMs, rate)));
            }

            progress?.Report(new(1, "Done"));
            AppLog.Write($"Playback & A/V Sync Test: {Summary(result)} ({Detail(result)})");
            return result;
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Max sequence frames one displayed frame may advance at a rate (pure): one, or more when the display cannot show every frame
    /// (2x of 30 fps at 50 Hz advances 40 ms per refresh).
    /// </summary>
    internal static int AllowedStep(double refreshMs, double rate) =>
        Math.Max(1, (int)Math.Ceiling(Math.Abs(rate) * Math.Max(refreshMs, 0) / 1000 * Fps - 1e-6));

    /// <summary>
    /// Builds a step from per-frame A/V (ms) and sequence times in display order (pure, golden-tested): median and worst |A/V|, frames off
    /// (&gt;= <see cref="ToleranceMs"/>), and drops (advances beyond <paramref name="allowed"/> frames; display lateness counts as off, not
    /// dropped). In sync: median within tolerance and at most 1 in 20 off. Smooth: at most 1 in 100 hitches (dropped or off).
    /// </summary>
    internal static PlaybackSyncStep Step(string name, IReadOnlyList<double> offsetsMs, IReadOnlyList<double> moments, int allowed)
    {
        var step = new PlaybackSyncStep { Name = name, Frames = moments.Count };
        if (offsetsMs.Count > 0)
        {
            var sorted = offsetsMs.Order().ToList();
            step.MedianMs = Math.Round(sorted[sorted.Count / 2], 2);
            step.WorstMs = Math.Round(offsetsMs.MaxBy(Math.Abs), 2);
            step.Off = offsetsMs.Count(o => Math.Abs(o) >= ToleranceMs);
        }

        for (var i = 1; i < moments.Count; i++)
        {
            var frames = (int)Math.Round((moments[i] - moments[i - 1]) * Fps);
            if (frames > allowed)
                step.Dropped += frames - allowed;
        }

        step.InSync = offsetsMs.Count >= 10 && Math.Abs(step.MedianMs) < ToleranceMs && step.Off <= offsetsMs.Count / 20;
        step.Smooth = step.Frames >= 10 && step.Hitches <= step.Frames / 100;
        return step;
    }

    /// <summary>
    /// Card summary line (pure): "In sync · worst 1.9 ms · 0 dropped · 60 Hz", "In sync · 2 hitches · 60 Hz", or the first failure.
    /// </summary>
    public static string Summary(PlaybackSyncResult result)
    {
        if (result.Error.Length > 0)
            return result.Error;
        if (result.Steps.Count == 0)
            return "Nothing was measured";
        var hz = result.RefreshHz > 0 ? string.Create(CultureInfo.InvariantCulture, $" · {result.RefreshHz:0} Hz") : "";
        if (result.Steps.Find(s => !s.InSync) is { } off)
        {
            return off.Frames < 10 ? $"Too few frames at {off.Name} ({off.Frames})"
                : Math.Abs(off.MedianMs) >= ToleranceMs ? string.Create(CultureInfo.InvariantCulture, $"Out of sync at {off.Name} · median {off.MedianMs:+0.0;-0.0} ms{hz}")
                : $"Out of sync at {off.Name} · {off.Off} frames off{hz}";
        }

        if (result.Steps.Find(s => !s.Smooth) is { } rough)
            return $"Stutters at {rough.Name} · {Hitches(rough.Hitches)}{hz}";
        var hitches = result.Steps.Sum(s => s.Hitches);
        if (result.Steps.Exists(s => s.Off > 0))
            return $"In sync · {Hitches(hitches)}{hz}";
        var worst = result.Steps.Max(s => Math.Abs(s.WorstMs));
        return string.Create(CultureInfo.InvariantCulture, $"In sync · worst {worst:0.0} ms · {hitches} dropped{hz}");
    }

    private static string Hitches(int count) => count == 1 ? "1 hitch" : $"{count} hitches";

    /// <summary>Every step's figures in one line (the log, the card's tooltip; pure).</summary>
    public static string Detail(PlaybackSyncResult result) => string.Join("; ", result.Steps.Select(s => string.Create(CultureInfo.InvariantCulture,
        $"{s.Name}: {s.Frames} frames, median {s.MedianMs:+0.00;-0.00;0.00} ms, worst {s.WorstMs:+0.00;-0.00;0.00} ms, {s.Off} off, {s.Dropped} dropped")))
        + (result.DisplayLeadMs is { } lead ? string.Create(CultureInfo.InvariantCulture, $"; display lead {lead:0.0} ms") : "")
        + (result.FrameStatistics ? "; DXGI frame statistics" : "; DWM's grid assumed");
}
