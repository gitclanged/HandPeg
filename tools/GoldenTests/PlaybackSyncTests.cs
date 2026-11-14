using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.5.2-Release's Playback &amp; A/V Sync Test (the user's Q2; the live run itself is AvSync calibrate on the test PCs):
/// <para>playback-sync-analysis: a step's figures from its frames (median, worst, frames skipped against what the display allows), the
/// result's few words, the test sequence (no file, under a minute in all), the result kept in the settings file, and the test's engine
/// beside the player's (never the player's Current).</para>
/// <para>playback-sync-card: the card on the first-run window's second screen with a stand-in for the run (no engine, no worker): idle, running
/// (its on-screen picture, progress, Cancel Test), a result shown and kept by the window, Cancel, a run that could not start, held while the
/// hardware check measures, stopped by a change of screen, the last result shown when opened again, and Settings' keeping of a result.</para>
/// Every line says what it proved.
/// </summary>
public static class PlaybackSyncTests
{
    /// <summary>The cases that need no WPF Application.</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("playback-sync-analysis", "# The Playback & A/V Sync Test's figures, words, sequence and kept result (5.5.2).", Analysis);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("playback-sync-card", "# The Playback & A/V Sync Test's card on the first-run window, run by a stand-in (5.5.2).", Card);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            run(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    // ----- playback-sync-analysis -----

    private static List<double> Moments(int count, params int[] skipsAfter)
    {
        var (list, frame) = (new List<double>(), 0);
        for (var i = 0; i < count; i++)
        {
            list.Add(1 + frame / 30.0);
            frame += 1 + skipsAfter.Count(s => s == i);
        }

        return list;
    }

    private static List<double> Offsets(int count, double worst) => [.. Enumerable.Range(0, count).Select(i => i == count / 3 ? worst : (i % 5 - 2) * 0.1)];

    private static void Analysis(Action<bool, string> expect)
    {
        // A step's figures.
        var clean = PlaybackSyncTest.Step("1x", Offsets(120, 1.1), Moments(120), 1);
        expect(clean is { Frames: 120, MedianMs: 0, WorstMs: 1.1, Dropped: 0, InSync: true, Smooth: true },
            "120 frames a frame apart, A/V within 1.1 ms: median 0, worst +1.1, 0 dropped, in sync and smooth");
        var one = PlaybackSyncTest.Step("1x", Offsets(120, 0.5), Moments(120, 40), 1);
        expect(one is { Dropped: 1, Smooth: true }, "one frame skipped in 120: 1 dropped, still smooth (one in a hundred)");
        var three = PlaybackSyncTest.Step("1x", Offsets(120, 0.5), Moments(120, 40, 80, 80), 1);
        expect(three is { Dropped: 3, Smooth: false, InSync: true }, "three skipped (one, then two at once): 3 dropped, not smooth, still in sync");
        expect(PlaybackSyncTest.Step("2x", Offsets(240, 0.5), Moments(240, Enumerable.Range(0, 240).Where(i => i % 5 == 0).ToArray()), 2) is { Dropped: 0, Smooth: true },
            "2x on a 50 Hz display moving two frames at times: allowed (2), nothing dropped");
        expect(PlaybackSyncTest.Step("1x", Offsets(120, 16.2), Moments(120), 1) is { InSync: true, Smooth: true, WorstMs: 16.2, Off: 1, Hitches: 1 },
            "one frame 16.2 ms ahead of the sound (15 ms): 1 off, a hitch; still in sync and smooth (one in a hundred)");
        expect(PlaybackSyncTest.Step("1x", Offsets(120, -33.1), Moments(120, 50), 1) is { InSync: true, Smooth: false, WorstMs: -33.1, Off: 1, Dropped: 1, Hitches: 2 },
            "a frame two refreshes late and one dropped: in sync, 2 hitches in 120, not smooth; the worst kept with its sign");
        var sevenOff = Offsets(120, 0.5);
        for (var i = 0; i < 7; i++)
            sevenOff[i * 10] = 18;
        expect(PlaybackSyncTest.Step("1x", sevenOff, Moments(120), 1) is { InSync: false, Off: 7 }, "7 frames of 120 off (more than one in twenty): out of sync");
        expect(PlaybackSyncTest.Step("1x", [.. Enumerable.Repeat(20.0, 120)], Moments(120), 1) is { InSync: false, MedianMs: 20 },
            "every frame 20 ms ahead: out of sync by its median");
        expect(PlaybackSyncTest.Step("1x", Offsets(9, 0.2), Moments(9), 1) is { InSync: false, Smooth: false },
            "nine frames: too few to say either way");

        // The frames one new frame may move on by: one, more only where the display cannot show every frame.
        var steps = new[] { (16.667, 1.0), (1000 / 60.0, 2.0), (20.0, 2.0), (2.0, 2.0), (1000 / 144.0, 2.0), (0.0, 2.0), (40.0, 1.0) }
            .Select(c => PlaybackSyncTest.AllowedStep(c.Item1, c.Item2)).ToList();
        expect(steps.SequenceEqual([1, 1, 2, 1, 1, 1, 2]),
            $"allowed steps: 60 Hz 1x 1, 60 Hz 2x 1 (exactly a frame a refresh), 50 Hz 2x 2, 500 Hz 2x 1, 144 Hz 2x 1, unknown 1, 25 Hz 1x 2 ({string.Join(",", steps)})");

        // The result's few words.
        var passed = new PlaybackSyncResult
        {
            RefreshHz = 60, DisplayLeadMs = 33.3, FrameStatistics = true, AudioDevice = "Speakers",
            Steps = [clean, PlaybackSyncTest.Step("2x", Offsets(240, -0.6), Moments(240), 1), PlaybackSyncTest.Step("1x", Offsets(120, 0.4), Moments(120), 1)],
        };
        expect(passed.Passed && PlaybackSyncTest.Summary(passed) == "In sync · worst 1.1 ms · 0 dropped · 60 Hz",
            $"a clean run: \"{PlaybackSyncTest.Summary(passed)}\"");
        var off = new PlaybackSyncResult { RefreshHz = 500, Steps = [clean, PlaybackSyncTest.Step("2x", [.. Enumerable.Repeat(24.0, 240)], Moments(240), 1)] };
        expect(!off.Passed && PlaybackSyncTest.Summary(off) == "Out of sync at 2x · median +24.0 ms · 500 Hz", $"out of sync: \"{PlaybackSyncTest.Summary(off)}\"");
        var scattered = new PlaybackSyncResult { Steps = [PlaybackSyncTest.Step("1x", sevenOff, Moments(120), 1)] };
        expect(!scattered.Passed && PlaybackSyncTest.Summary(scattered) == "Out of sync at 1x · 7 frames off", $"out of sync, frames off: \"{PlaybackSyncTest.Summary(scattered)}\"");
        var rough = new PlaybackSyncResult { Steps = [three] };
        expect(!rough.Passed && PlaybackSyncTest.Summary(rough) == "Stutters at 1x · 3 hitches", $"stutters, refresh unknown: \"{PlaybackSyncTest.Summary(rough)}\"");
        var hitch = new PlaybackSyncResult
        {
            RefreshHz = 60,
            Steps = [clean, PlaybackSyncTest.Step("2x", Offsets(240, -33.1), Moments(240, 9), 1), PlaybackSyncTest.Step("1x", Offsets(120, 0.4), Moments(120), 1)],
        };
        expect(hitch.Passed && PlaybackSyncTest.Summary(hitch) == "In sync · 2 hitches · 60 Hz", $"a frame late and one dropped at 2x, passing: \"{PlaybackSyncTest.Summary(hitch)}\"");
        var failed = new PlaybackSyncResult { Error = "The player could not start: no card" };
        expect(!failed.Passed && PlaybackSyncTest.Summary(failed) == failed.Error, "a run that could not start says why, and has not passed");
        expect(PlaybackSyncTest.Detail(passed).StartsWith("1x: 120 frames, median 0.00 ms, worst +1.10 ms, 0 off, 0 dropped; 2x: 240 frames", StringComparison.Ordinal)
               && PlaybackSyncTest.Detail(passed).EndsWith("; display lead 33.3 ms; DXGI frame statistics", StringComparison.Ordinal),
            "the tooltip's detail: every step's figures, the display lead, how frames were known shown");

        // The sequence played: no file, a title keyed across, long enough for every step, the whole run under a minute.
        var state = PlaybackSyncTest.TestSequence();
        var clips = state.Timeline.Tracks.SelectMany(t => t.Clips).ToList();
        expect(state.Timeline is { Width: 1280, Height: 720 } && state.SourceFrameRate == 30 && clips.Count == 1
               && clips[0] is { ContentKind: LayerKind.Text, Start: 0, End: 24 } && string.IsNullOrEmpty(clips[0].SourcePath),
            "the sequence: 1280x720 at 30 fps, one 24 s title clip, no file");
        var keys = clips[0].State?.KeysX ?? [];
        expect(keys.Count == 25 && keys.Zip(keys.Skip(1)).All(p => p.First.Value != p.Second.Value),
            "the title moves every second (25 keys, each to the other side): every frame presented is a new frame");
        var lowered = TimelineLowering.Lowering(state);
        expect(lowered.Stack.Count(l => l.IsVideoFile) == 0 && lowered.Stack.All(l => string.IsNullOrEmpty(l.ImagePath)),
            "lowered, nothing reads a file");
        var played = PlaybackSyncTest.StartAt + PlaybackSyncTest.StepPlan.Sum(s => s.Rate * (PlaybackSyncTest.MeasureSeconds + PlaybackSyncTest.Settle));
        var total = 1.5 + PlaybackSyncTest.StepPlan.Length * (PlaybackSyncTest.MeasureSeconds + PlaybackSyncTest.Settle);
        expect(played < PlaybackSyncTest.SequenceSeconds - 2 && total < 60 && string.Join(",", PlaybackSyncTest.StepPlan.Select(s => s.Name)) == "Play,2x,1x",
            string.Create(CultureInfo.InvariantCulture, $"steps Play, 2x, 1x end {played:0.#} s into the 24 s sequence; the run is {total:0.#} s plus the player's start (under a minute)"));

        // Kept in the settings file, read back.
        var settings = new AppSettings { LastPlaybackSyncTest = passed };
        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var read = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        expect(read.LastPlaybackSyncTest is { RefreshHz: 60, DisplayLeadMs: 33.3, FrameStatistics: true, Passed: true, Steps.Count: 3 }
               && read.LastPlaybackSyncTest.Steps[1] is { Name: "2x", Frames: 240, WorstMs: -0.6 },
            "the result written to the settings file and read back whole (every step)");
        expect(!json.Contains("\"Passed\"", StringComparison.Ordinal) && new AppSettings().LastPlaybackSyncTest is null,
            "Passed is worked out, never written; settings without a test hold none");

        // A stalled playing turn measures no display lead (the Desktop's on-screen session: a cold play's 20-60 ms turns drew frames ~60 ms ahead).
        var origins = new[]
        {
            GpuViewportControl.LeadOrigin(1000, 0.002, 0.002, 0.002), GpuViewportControl.LeadOrigin(1000, 0.0029, 0.002, 0.002),
            GpuViewportControl.LeadOrigin(1000, 0.0031, 0.002, 0.002), GpuViewportControl.LeadOrigin(1000, 0.032, 0.002, 0.002),
            GpuViewportControl.LeadOrigin(1000, 0.0167, 1 / 60.0, 1 / 60.0), GpuViewportControl.LeadOrigin(1000, 0.034, 1 / 60.0, 1 / 60.0),
            GpuViewportControl.LeadOrigin(1000, 0.020, double.NaN, 1 / 60.0), GpuViewportControl.LeadOrigin(1000, 0.030, double.NaN, 1 / 60.0),
            GpuViewportControl.LeadOrigin(1000, 5.0, 0.002, 0.002),
        };
        expect(origins.SequenceEqual([1000L, 1000, 0, 0, 1000, 0, 1000, 0, 0]),
            $"lead origins: 500 Hz turns of 2 and 2.9 ms kept, 3.1 and 32 ms stalls; 60 Hz 16.7 kept, 34 ms a stall; monitor unknown: by the estimate; a play's first turn none ({string.Join(",", origins)})");

        // The test's engine beside the player's.
        var start = typeof(GpuPreviewEngine).GetMethod(nameof(GpuPreviewEngine.StartAsync))!.GetParameters();
        expect(start.Length == 3 && start[2] is { Name: "player", HasDefaultValue: true, DefaultValue: true },
            "GpuPreviewEngine.StartAsync(surface, adapter, player = true): the player's callers unchanged, the test's engine asks player: false");
    }

    // ----- playback-sync-card -----

    private static void Card(Action<bool, string> expect)
    {
        var settings = AppSettings.Current;
        var keptResult = settings.LastPlaybackSyncTest;
        settings.LastPlaybackSyncTest = null;
        var window = new FirstRunWindow(live: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            window.GoToScreen(2);
            Until(() => window.SyncTestCard.IsVisible, 2000);
            var card = window.SyncTestCard;

            // Idle.
            expect(card.IsVisible && card.TestButton.IsEnabled && card.TestButtonText.Text == "Test Playback & A/V Sync"
                   && card.DescriptionText.Text == "Testing your hardware to ensure playback is smooth and synced — this takes under a minute.",
                "Screen 2 shows Test Playback & A/V Sync and the user's line about it");
            expect(card.SurfaceFrame.Visibility == Visibility.Collapsed && card.RunProgress.Visibility == Visibility.Collapsed
                   && card.StatusText.Visibility == Visibility.Collapsed && card.Result is null,
                "before a run: no picture, no progress, no result line");

            // Running, with a stand-in that reports halfway and waits.
            var (calls, changes, completed) = (0, 0, 0);
            nint given = 0;
            TaskCompletionSource<PlaybackSyncResult> gate = new();
            card.RunningChanged += () => changes++;
            card.Completed += _ => completed++;
            card.Runner = async (surface, progress, token) =>
            {
                calls++;
                given = surface;
                progress.Report(new PlaybackSyncProgress(0.5, "Playing at 2x"));
                using (token.Register(() => gate.TrySetCanceled(token)))
                    return await gate.Task;
            };
            Click(card);
            Until(() => calls == 1 && card.StatusText.Text == "Playing at 2x...", 3000);
            expect(card.IsRunning && card.SurfaceFrame.IsVisible && given != 0 && given == card.Surface.SurfaceHandle,
                "pressed: the small picture shows, and the run is given its native window (the swap chain's)");
            expect(card.TestButtonText.Text == "Cancel Test" && card.TestButton.IsEnabled && card.RunProgress.IsVisible
                   && Math.Abs(card.RunProgress.Value - 0.5) < 1e-9 && card.StatusText.Text == "Playing at 2x...",
                "while it runs: Cancel Test, the progress at half, the step said");

            // A result: shown, raised once, kept by the window for Start Editing.
            var result = new PlaybackSyncResult
            {
                RefreshHz = 60, FrameStatistics = true,
                Steps = [PlaybackSyncTest.Step("Play", Offsets(120, 0.4), Moments(120), 1), PlaybackSyncTest.Step("2x", Offsets(240, 1.1), Moments(240), 1),
                    PlaybackSyncTest.Step("1x", Offsets(120, 0.3), Moments(120), 1)],
            };
            gate.SetResult(result);
            Until(() => !card.IsRunning, 3000);
            expect(card.StatusText.Text == "In sync · worst 1.1 ms · 0 dropped · 60 Hz" && card.StatusText.ToolTip is string tip && tip.StartsWith("Play: 120 frames", StringComparison.Ordinal),
                $"done: the result in a few words (\"{card.StatusText.Text}\"), every step's figures in its tooltip");
            expect(card.SurfaceFrame.Visibility == Visibility.Collapsed && card.RunProgress.Visibility == Visibility.Collapsed
                   && card.TestButtonText.Text == "Test Playback & A/V Sync" && changes == 2 && completed == 1 && ReferenceEquals(card.Result, result),
                "the picture and the progress go, the button is itself again; RunningChanged twice, Completed once");
            expect(ReferenceEquals(SyncResultOf(window), result), "the first-run window holds the result for Start Editing (Finish)");

            // Cancel: pressed again while it runs.
            gate = new TaskCompletionSource<PlaybackSyncResult>();
            Click(card);
            Until(() => calls == 2 && card.IsRunning, 3000);
            Click(card);
            Until(() => !card.IsRunning, 3000);
            expect(card.StatusText.Text == "Test cancelled" && completed == 1 && ReferenceEquals(SyncResultOf(window), result)
                   && card.SurfaceFrame.Visibility == Visibility.Collapsed,
                "Cancel Test stops it: \"Test cancelled\", nothing raised, the window keeps the last result");

            // A run that could not start: said, never kept.
            card.Runner = (_, _, _) => Task.FromResult(new PlaybackSyncResult { Error = "The player could not start: no card" });
            Click(card);
            Until(() => !card.IsRunning && card.StatusText.Text.Length > 0 && card.StatusText.Text != "Starting...", 3000);
            expect(card.StatusText.Text == "The player could not start: no card" && completed == 1 && ReferenceEquals(SyncResultOf(window), result),
                "a run that could not start says why and is not kept");

            // Held while the quick hardware check measures.
            var measuring = typeof(FirstRunWindow).GetMethod("SetMeasuring", BindingFlags.NonPublic | BindingFlags.Instance)!;
            measuring.Invoke(window, [true]);
            var before = calls;
            card.Runner = (_, _, _) =>
            {
                calls++;
                return Task.FromResult(result);
            };
            Click(card);
            Idle();
            expect(!card.TestButton.IsEnabled && (card.TestButton.ToolTip as string) == "Waits for the hardware check" && calls == before && !card.IsRunning,
                "while the quick hardware check measures, the button waits (\"Waits for the hardware check\") and nothing runs");
            measuring.Invoke(window, [false]);
            expect(card.TestButton.IsEnabled, "the check done, it can run again");

            // A change of screen stops it.
            gate = new TaskCompletionSource<PlaybackSyncResult>();
            card.Runner = async (_, _, token) =>
            {
                using (token.Register(() => gate.TrySetCanceled(token)))
                    return await gate.Task;
            };
            Click(card);
            Until(() => card.IsRunning, 3000);
            window.GoToScreen(1);
            Until(() => !card.IsRunning, 3000);
            expect(!card.IsRunning && card.StatusText.Text == "Test cancelled", "Back to Screen 1 while it runs: the test stops");
        }
        finally
        {
            window.Close();
        }

        // Opened again (from the settings): the last result kept is shown.
        settings.LastPlaybackSyncTest = new PlaybackSyncResult { RefreshHz = 144, Steps = [PlaybackSyncTest.Step("1x", Offsets(120, 2.0), Moments(120), 1)] };
        var again = new FirstRunWindow(live: false);
        try
        {
            expect(again.SyncTestCard.StatusText.Text == "Last test: In sync · worst 2.0 ms · 0 dropped · 144 Hz" && SyncResultOf(again) is null,
                $"opened again, the last result kept: \"{again.SyncTestCard.StatusText.Text}\" (not written again unless a new one is made)");
        }
        finally
        {
            again.Close();
        }

        // Settings keeps a result at once: its copy and the settings in effect, saved.
        var copy = settings.Clone();
        var fresh = new PlaybackSyncResult { RefreshHz = 75, Steps = [PlaybackSyncTest.Step("1x", Offsets(120, 0.7), Moments(120), 1)] };
        try
        {
            SettingsWindow.KeepSyncResult(copy, fresh);
            var file = File.ReadAllText(Path.Combine(AppPaths.Settings, "appsettings.json"));
            expect(ReferenceEquals(copy.LastPlaybackSyncTest, fresh) && ReferenceEquals(AppSettings.Current.LastPlaybackSyncTest, fresh)
                   && file.Contains("\"LastPlaybackSyncTest\"", StringComparison.Ordinal) && file.Contains("\"RefreshHz\": 75", StringComparison.Ordinal),
                "Settings keeps a result in its own copy and in the settings in effect, saved at once (whether Settings is then saved or cancelled)");
        }
        finally
        {
            AppSettings.Current.LastPlaybackSyncTest = keptResult;
            AppSettings.Current.SaveAsCurrent();
        }

        var finish = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Dialogs", "FirstRunWindow.xaml.cs"));
        expect(finish.Contains("SyncTestCard.Cancel();", StringComparison.Ordinal) && finish.Contains("settings.LastPlaybackSyncTest = sync;", StringComparison.Ordinal),
            "Start Editing (Finish) stops a test running and writes the window's result with the other choices");
    }

    private static PlaybackSyncResult? SyncResultOf(FirstRunWindow window) =>
        (PlaybackSyncResult?)typeof(FirstRunWindow).GetField("_syncResult", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);

    // The button pressed as the dispatcher would (its context current, so the card's progress and continuations come back to it).
    private static void Click(PlaybackSyncCard card)
    {
        Dispatcher.CurrentDispatcher.BeginInvoke(() => card.TestButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        Idle();
    }

    private static void Idle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    // The window's thread turned until a condition holds (or the time is up).
    private static void Until(Func<bool> condition, int milliseconds)
    {
        var started = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - started < milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }
}
