using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4.2B: zero-lag scrubbing. scrub-zero-lag: the pure ScrubCoalescer with its own clock: a 500 Hz drag ticked by a 500 Hz frame loop
/// sets the playhead's value at most once per 16 ms, each time to the latest moment the pointer reached, and once more as it is let go with
/// the very last; a press let go at once sets it exactly once; nothing after. Then the wiring, read from the source: the ruler's drag and
/// the overview scrubber's thumb go through ScrubPlayheadTo (the lines and the engine on the raw move), the ruler tells the end of its seek
/// on release and on a capture taken away, the scrubber's binding waits to be told (Explicit), the player's clock leaves a held scrub
/// alone, and a scrub's value never seeks the engine a second time.
/// </summary>
public static class ScrubTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("scrub-zero-lag", "# A drag of the playhead moves its lines and the player on every pointer move; the playhead's value follows at most once per 16 ms and exactly as the drag is let go.", ZeroLag);
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

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static void ZeroLag(Action<bool, string> expect)
    {
        // A 500 Hz mouse dragging the playhead 10 ms of sequence per report for 200 ms, the frame loop ticking at 500 Hz too.
        var flushed = new List<(double At, double Ms)>();
        var now = 1000.0;
        var scrub = new ScrubCoalescer(ms => flushed.Add((now, ms)));
        expect(!scrub.IsHeld && !scrub.HasPending && !scrub.Tick(now), "nothing held or waiting before a drag; a frame then sets nothing");

        var moves = new List<(double At, double Ms)>();
        for (var i = 0; i <= 100; i++)
        {
            now = 1000 + i * 2.0;
            scrub.Move(i * 10.0);
            moves.Add((now, i * 10.0));
            scrub.Tick(now);
        }

        var gaps = flushed.Zip(flushed.Skip(1), (a, b) => b.At - a.At).ToList();
        expect(scrub.IsHeld && flushed.Count is >= 12 and <= 14 && gaps.All(g => g >= ScrubCoalescer.FlushIntervalMs - 1e-9),
            F($"101 moves over 200 ms set the playhead's value {flushed.Count} times, never closer than 16 ms (closest {gaps.DefaultIfEmpty(0).Min():0} ms)"));
        expect(flushed[0].Ms == 0 && flushed.All(f => moves.Last(m => m.At <= f.At).Ms == f.Ms),
            "the first move is set at its own frame, and each setting is the latest moment the pointer had reached");

        now += 2;
        scrub.Release(now);
        expect(!scrub.IsHeld && !scrub.HasPending && flushed[^1].Ms == 1000,
            "let go: the playhead's value is the very last moment (1000 ms) at once");
        var count = flushed.Count;
        now += 50;
        scrub.Release(now);
        expect(!scrub.Tick(now) && flushed.Count == count, "after it, a frame or a second release sets nothing");

        // A press let go before any frame (a click on the ruler): exactly one setting, at the release.
        now += 1000;
        scrub.Move(4321);
        scrub.Release(now);
        expect(flushed.Count == count + 1 && flushed[^1].Ms == 4321 && !scrub.Tick(now + 20), "a click let go at once sets the playhead once, where it was pressed");

        // The wiring, read from the source.
        var root = RepositoryRoot();
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([root, .. parts])).Replace("\r\n", "\n");
        var parts = Read("src", "Controls", "TimelineParts.cs");
        var view = Read("src", "MainWindow.TimelineView.cs");
        var player = Read("src", "MainWindow.Player.cs");
        var scrubFile = Read("src", "MainWindow.Scrub.cs");
        var xaml = Read("src", "MainWindow.xaml");

        expect(view.Contains("ruler.SeekRequested += seconds => ScrubPlayheadTo(seconds * 1000);") && view.Contains("ruler.SeekEnded += EndScrub;"),
            "a ruler's press and drag scrub (ScrubPlayheadTo), its end sets the playhead's value (EndScrub)");
        expect(parts.Contains("ReleaseMouseCapture();\n        SeekEnded?.Invoke();") && parts.Contains("if (wasSeeking)\n            SeekEnded?.Invoke();"),
            "the ruler tells the end of its seek when let go and when its capture is taken away");
        expect(xaml.Contains("Value=\"{Binding PositionMs, UpdateSourceTrigger=Explicit}\""),
            "the overview scrubber's value reaches the playhead only when told (its binding is Explicit)");
        expect(player.Contains("ScrubPlayheadTo(e.NewValue);") && player.Contains("_isScrubbing = false;\n        EndScrub();"),
            "the overview scrubber's drag scrubs the same way and ends with EndScrub");
        expect(player.Contains("if (IsScrubHeld)") && view.Contains("IsScrubHeld ||"),
            "the player's clock and Follow Playhead leave a held scrub alone (a ruler's too)");
        expect(player.Contains("if (_scrubFlushing)\n            return;") && scrubFile.Contains("if (!PlayerAt((long)positionMs))\n            PlayerSeek((long)positionMs);"),
            "the engine is sent on the raw move, once: a scrub's value never seeks it again");
        expect(scrubFile.Contains("UnifiedTimeline.Surface.PlayheadSeconds = seconds;") && scrubFile.Contains("PlaceRulerPlayhead(ruler, seconds);") && scrubFile.Contains("KeyLanes.MovePlayhead(seconds);"),
            "the Timeline's, the rulers' and the key lanes' playheads slide on the raw move");
        expect(scrubFile.Contains("CompositionTarget.Rendering += OnScrubFrame;") && scrubFile.Contains("CompositionTarget.Rendering -= OnScrubFrame;"),
            "the frame loop is hooked only while a scrub has something to set");
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HandPegApp.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("the repository (HandPegApp.csproj) above " + AppContext.BaseDirectory);
    }
}
