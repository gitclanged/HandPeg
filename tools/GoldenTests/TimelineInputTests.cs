using System.Text;
using System.Windows;
using System.Windows.Input;
using HandPegApp;
using HandPegApp.Models.Timeline;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.2B on the real timeline control (on this STA thread, laid out at a size of its own, no window, no mouse): a press on empty space
/// seeks at once (the user's Q3), a pointer still held that moves less than the system's drag distance draws no band and its release leaves
/// the seek as it is, one that moves past it draws the rubber band, which selects the clips it touches as it is let go while the playhead
/// stays where the press put it; and where a clip's beats (its file's own seconds) fall on the Sequence, trimmed, slowed down, slipped.
/// Every line says what it proved.
/// </summary>
public static class TimelineInputTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("timeline-press-seeks", "# A press on empty timeline space seeks at once; the rubber band starts only past the drag distance, and leaves the playhead where the press put it.", PressSeeks);
        yield return Case("audio-beats-on-sequence", "# A clip's beats, found in its file's own seconds, where they fall on the Sequence: trimmed, at another speed, slipped, with a J cut.", BeatsOnSequence);
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

    private static void PressSeeks(Action<bool, string> expect)
    {
        // A control 1000 DIPs wide (its headers 168 of it), ten seconds across: a second is 83.2 DIPs. One row, two clips: 1 to 3 s and 6 to 8 s.
        var surface = new UnifiedTimelineControl();
        surface.Measure(new Size(1000, 300));
        surface.Arrange(new Rect(0, 0, 1000, 300));
        surface.SetView(0, 10, 10);
        var scale = surface.PixelsPerSecond;
        expect(Math.Abs(scale - (1000 - surface.HeaderWidth) / 10) < 1e-9, "laid out without a window: 10 s across the time area");

        var (first, second) = (new TimelineBlock { Left = 1 * scale, Width = 2 * scale, Name = "first", Clip = "first" }, new TimelineBlock { Left = 6 * scale, Width = 2 * scale, Name = "second", Clip = "second" });
        var row = new TimelineRow { Key = "row", Data = "row", Kind = TimelineRowKind.Video, Height = 56 };
        row.Blocks.Add(first);
        row.Blocks.Add(second);
        surface.SetRows([row]);

        var seeks = new List<double>();
        IReadOnlyList<TimelineBlock>? banded = null;
        surface.EmptyPressed += (_, seconds, _) => seeks.Add(seconds);
        surface.MarqueeSelected += (blocks, _) => banded = blocks;
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left);
        Point At(double seconds, double y) => new(surface.HeaderWidth + seconds * scale, y);

        // Pressed at 4.5 s, between the clips: the seek is told at once, before any move or release.
        surface.PressEmptyAt(row, At(4.5, 20), args, ModifierKeys.None, held: true);
        expect(seeks.Count == 1 && Math.Abs(seeks[0] - 4.5) < 1e-6, "a press on empty space seeks to its moment at once (4.5 s)");
        expect(!surface.IsBandDrawn, "and draws no band yet");

        // A hand that shakes as it clicks: under the drag distance, still no band; let go, nothing more than the seek.
        var small = new Point(At(4.5, 20).X + Math.Min(SystemParameters.MinimumHorizontalDragDistance, 2) - 0.5, 20 + Math.Min(SystemParameters.MinimumVerticalDragDistance, 2) - 0.5);
        expect(surface.MoveEmptyPress(small) && !surface.IsBandDrawn, "moved less than the system's drag distance, the press draws no band");
        expect(surface.ReleaseEmptyPress() && banded is null && seeks.Count == 1, "let go there, it selects nothing and seeks no more: the press's seek stands");
        expect(!surface.MoveEmptyPress(At(0.5, 40)), "and once let go, a move is no press's any more");

        // Pressed at 4.5 s again and dragged back to 0.5 s and down: past the drag distance, the band; let go, it selects the first clip only.
        surface.PressEmptyAt(row, At(4.5, 20), args, ModifierKeys.None, held: true);
        expect(seeks.Count == 2 && Math.Abs(seeks[1] - 4.5) < 1e-6, "the second press seeks at once too");
        expect(surface.MoveEmptyPress(At(0.5, 40)) && surface.IsBandDrawn, "dragged past the drag distance, the rubber band is drawn");
        expect(surface.ReleaseEmptyPress() && banded is { Count: 1 } && ReferenceEquals(banded[0], first), "let go, the band selects the clip it touches (1 to 3 s), not the other");
        expect(seeks.Count == 2 && Math.Abs(seeks[^1] - 4.5) < 1e-6, "and the playhead stays where the press put it (4.5 s): the band seeks nothing");
        expect(!surface.IsBandDrawn, "the band is gone once let go");
    }

    private static void BeatsOnSequence(Action<bool, string> expect)
    {
        static List<double> On(TimelineClip clip)
        {
            var times = new List<double>();
            MainViewModel.BeatsOnSequence(clip, times);
            return times;
        }

        static bool Same(IReadOnlyList<double> got, params double[] want) => got.Count == want.Length && got.Zip(want).All(p => Math.Abs(p.First - p.Second) < 1e-9);

        // A clip from 10 to 14 s on the Sequence, playing its file from 3 s at twice the speed (its file's 3 to 11 s).
        var beats = new double[] { 1, 3, 4, 7, 11, 12 };
        var clip = new TimelineClip { SourcePath = "D:/media/song.wav", Start = 10, End = 14, MediaOffset = 3, Speed = 2, SourceDuration = 60, BeatTimes = beats };
        expect(Same(On(clip), 10, 10.5, 12, 14), "trimmed to its file's 3 to 11 s at 2x: the beats at 3, 4, 7 and 11 s land at 10, 10.5, 12 and 14 s; those outside it are not on the Sequence");

        clip.AudioLinked = false;
        clip.AudioSlipSeconds = 0.5;
        expect(Same(On(clip), 10.5, 11, 12.5, 14.5), "its sound unlinked and slipped half a second later: every beat half a second later with it");

        (clip.AudioLinked, clip.AudioSlipSeconds, clip.AudioLeadInSeconds) = (true, 0, 1);
        expect(Same(On(clip), 9, 10, 10.5, 12, 14), "a J cut of a second: the beat its sound plays before the picture (its file's 1 s, at 9 s) is on the Sequence too");

        // Unlinked with the J cut still kept: the model ignores the cut while the clip is unlinked, so its sound plays 13 to 17 s (slipped 3 s).
        (clip.AudioLinked, clip.AudioSlipSeconds) = (false, 3);
        expect(Same(On(clip), 13, 13.5, 15, 17), "unlinked and slipped 3 s with its J cut kept: the cut is not heard, so only the beats its sound plays (13 to 17 s) are on the Sequence, not its file's 1 s at 12 s");

        expect(On(new TimelineClip { Start = 0, End = 4 }).Count == 0, "a clip with no beats has none on the Sequence");
    }
}
