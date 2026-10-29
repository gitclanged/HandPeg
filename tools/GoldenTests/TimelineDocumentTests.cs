using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// The TimelineDocument of 4.3A on its own (nothing is compiled from it until 4.3B): frame rates and quantizing, the
/// transaction's edits on track 1 and the tracks above it, and the document as a project file writes JSON. Each case
/// writes down what it did, so its golden file shows the behaviour, and checks the numbers that matter itself.
/// </summary>
public static class TimelineDocumentTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("timeline-frame-rate",
            "# FrameRate: the NTSC fractions, frames and quantizing, and 10,000 splits without drift.", FrameRates);
        yield return Case("timeline-edits",
            "# ITimelineTransaction on track 1 and the tracks above it: split, trims, ripple, roll, slip, J/L, moves, crossfades, gaps, locks, nesting, rollback.", Edits);
        yield return Case("timeline-edge-cases",
            "# The edges of the edits: file ends at 60, 59.94 and 23.976 fps, free edits, J/L cuts, held-back ripples, commits, guards.", EdgeCases);
        yield return Case("timeline-json-round-trip",
            "# A TimelineDocument written as project files write JSON, read back and written again: the timeline of format 5.", JsonRoundTrip);
        yield return Case("timeline-folds",
            "# Folds (4.3D, Split & Mark): stretches taken out of every track and kept at their seams, a fold rolled back, the file round trip, then each put back.", Folds);
        yield return Case("timeline-split-keys",
            "# A clip split between two keyframes (4.3D): each half keeps its own keys, a key on the cut with the value there, and its part of the curve, for every easing.", SplitKeys);
    }

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        return new GraphResult(golden, text.ToString(), problems, 0);
    }

    private static void Expect(List<string> problems, bool held, string what)
    {
        if (!held)
            problems.Add("not so: " + what);
    }

    private static void Line(StringBuilder text, FormattableString line) =>
        text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static string N(double value) => GoldenText.Num(value);

    // ----- Frame rates -----

    private static void FrameRates(StringBuilder text, List<string> problems)
    {
        text.Append("\nFromFps:\n");
        (double Fps, FrameRate Expected)[] known =
        [
            (23.976, FrameRate.Ntsc23976), (23.98, FrameRate.Ntsc23976), (24, FrameRate.Fps24), (25, FrameRate.Fps25),
            (29.97, FrameRate.Ntsc2997), (30, FrameRate.Fps30), (50, FrameRate.Fps50), (59.94, FrameRate.Ntsc5994),
            (60, FrameRate.Fps60), (119.88, FrameRate.Ntsc11988), (120, FrameRate.Fps120),
            (48, new FrameRate(48, 1)), (12.5, FrameRate.Fps60), (0, FrameRate.Fps60), (-1, FrameRate.Fps60), (double.NaN, FrameRate.Fps60),
        ];
        foreach (var (fps, expected) in known)
        {
            var rate = FrameRate.FromFps(fps);
            Line(text, $"  {N(fps)} -> {rate}");
            Expect(problems, rate == expected, $"FromFps({N(fps)}) is {expected}, not {rate}");
        }

        text.Append("\nTryParse:\n");
        (string Text, bool Ok, FrameRate Rate)[] parsed =
        [
            ("30000/1001", true, FrameRate.Ntsc2997), ("29.97", true, FrameRate.Ntsc2997), ("60", true, FrameRate.Fps60),
            ("0/1", false, FrameRate.Fps60), ("24/0", false, FrameRate.Fps60), ("fast", false, FrameRate.Fps60),
        ];
        foreach (var (input, ok, expected) in parsed)
        {
            var read = FrameRate.TryParse(input, out var rate);
            Line(text, $"  \"{input}\" -> {(read ? rate.ToString() : "no")}");
            Expect(problems, read == ok && (!ok || rate == expected), $"TryParse(\"{input}\") gives {(ok ? expected.ToString() : "no")}");
        }
        Expect(problems, default(FrameRate).Fps == 60 && !default(FrameRate).IsValid, "a rate of 0/0 counts as 60/1");

        text.Append("\nQuantize (seconds -> the frame's exact time):\n");
        double[] moments = [0, 0.01, 0.5, 1, 1.0083, 1.0084, 10, 3600.5, -2];
        foreach (var rate in new[] { FrameRate.Ntsc5994, FrameRate.Ntsc2997, FrameRate.Fps60 })
        {
            var row = new StringBuilder();
            foreach (var moment in moments)
            {
                var quantized = rate.Quantize(moment);
                row.Append(CultureInfo.InvariantCulture, $"  {N(moment)}->{N(quantized)}");
                Expect(problems, rate.Quantize(quantized) == quantized && rate.IsOnFrame(quantized), $"{rate}: Quantize({N(moment)}) is its own quantized time");
                Expect(problems, rate.Quantize(moment, enabled: false) == moment, $"{rate}: Quantize({N(moment)}, enabled: false) leaves it");
            }
            Line(text, $"  {rate}:{row}");
        }
        Expect(problems, FrameRate.Fps60.Quantize(1.0083) == 1 && FrameRate.Fps60.Quantize(1.0084) == 1.016667, "60 fps: 1.0083 s is frame 60 (1 s), 1.0084 s frame 61 (1.016667 s)");
        Expect(problems, FrameRate.Ntsc2997.Quantize(1) == 1.001 && FrameRate.Ntsc5994.Quantize(1) == 1.001, "NTSC: 1 s lands on 1.001 s (frame 30 / 60)");
        Expect(problems, FrameRate.Ntsc5994.Quantize(0.01) == 0.016683, "59.94: 0.01 s lands on frame 1 (0.016683 s)");
        Expect(problems, FrameRate.Ntsc5994.FrameFromSeconds(-1) == 0 && FrameRate.Ntsc5994.FrameFromSeconds(double.NaN) == 0, "no frame before 0");

        text.Append("\nFrames and back:\n");
        foreach (var rate in new[] { FrameRate.Ntsc23976, FrameRate.Ntsc2997, FrameRate.Ntsc5994, FrameRate.Ntsc11988, FrameRate.Fps60 })
        {
            const long hour = 216000; // an hour at 60 fps
            var misses = 0;
            for (long frame = 0; frame <= hour; frame++)
            {
                var exact = rate.SecondsFromFrame(frame);
                var quantized = rate.Quantize(exact);
                if (rate.FrameFromSeconds(exact) != frame || rate.FrameFromSeconds(quantized) != frame || !rate.IsOnFrame(quantized))
                    misses++;
            }
            Line(text, $"  {rate}: frames 0 to {hour} come back exactly, {misses} miss");
            Expect(problems, misses == 0, $"{rate}: every frame's time comes back to its frame ({misses} did not)");
        }

        text.Append("\nSplits:\n");
        foreach (var rate in new[] { FrameRate.Ntsc5994, FrameRate.Ntsc2997, FrameRate.Fps60 })
            Splits(rate, 10_000, text, problems);
    }

    /// <summary>A fixed sequence of numbers from 0 to 1 (a linear congruential generator), so the case is the same every time.</summary>
    private static double Next(ref ulong state)
    {
        state = state * 6364136223846793005UL + 1442695040888963407UL;
        return (state >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>Cuts one clip at every third frame, in a shuffled order and a little off each frame, and checks every cut is one exact double on its frame.</summary>
    private static void Splits(FrameRate rate, int splits, StringBuilder text, List<string> problems)
    {
        var doc = new TimelineDocument { FrameRate = rate };
        TimelineTrackModel track;
        var frames = 3L * (splits + 1);
        using (var setup = doc.BeginTransaction("Set up"))
        {
            track = setup.InsertTrack(0, TimelineTrackKind.Video);
            setup.PlaceClip(track, new TimelineClip
            {
                SourcePath = "D:/media/long.mp4", AudioStreamIndex = 0, End = rate.SecondsFromFrame(frames), SourceDuration = rate.SecondsFromFrame(frames) + 1,
            });
            setup.Commit();
        }

        var end = track.Clips[0].End;
        var commits = new List<TimelineCommit>();
        doc.Committed += commits.Add;
        using (var cuts = doc.BeginTransaction("Cuts"))
        {
            for (var i = 0; i < splits; i++)
            {
                var frame = 3L * (1 + i * 7919L % splits);
                var at = rate.SecondsFromFrame(frame) + 0.3 * rate.FrameDuration;
                cuts.SplitClipAt(track.ClipsIntersecting(at, at + 1e-9)[0], at);
            }
            cuts.Commit();
        }

        var clips = track.Clips;
        var (gaps, offFrame, wrongFrame, wrongOffset) = (0, 0, 0, 0);
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            gaps += clip.Start == (i == 0 ? 0 : clips[i - 1].End) ? 0 : 1;
            offFrame += rate.IsOnFrame(clip.Start) && rate.IsOnFrame(clip.End) ? 0 : 1;
            wrongFrame += rate.FrameFromSeconds(clip.Start) == 3L * i ? 0 : 1;
            wrongOffset += Math.Abs(clip.MediaOffset - clip.Start) < 1e-9 ? 0 : 1;
        }

        Line(text, $"  {rate}: {splits} cuts in a shuffled order, {clips.Count} clips; {gaps} cut not one double, {offFrame} edge off its frame, {wrongFrame} clip on the wrong frame, {wrongOffset} file offset adrift; still ends at {N(clips[^1].End)} (frame {rate.FrameFromSeconds(clips[^1].End)})");
        Expect(problems, clips.Count == splits + 1, $"{rate}: {splits + 1} clips after {splits} cuts");
        Expect(problems, gaps == 0 && offFrame == 0 && wrongFrame == 0 && wrongOffset == 0, $"{rate}: every cut is one exact double on its frame, and every file offset follows");
        Expect(problems, clips[^1].End == end, $"{rate}: the last clip ends exactly where the whole one did");
        Expect(problems, doc.FindProblems(requireFrames: true).Count == 0, $"{rate}: no problems after the cuts ({string.Join("; ", doc.FindProblems(requireFrames: true).Take(3))})");
        Expect(problems, commits.Count == 1 && commits[0].TouchedClipIds.Count == splits + 1, $"{rate}: one commit, naming all {splits + 1} clips");
    }

    // ----- Edits -----

    private static void Edits(StringBuilder text, List<string> problems)
    {
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        var names = new Dictionary<Guid, string>();
        var commits = new List<TimelineCommit>();
        doc.Committed += commits.Add;

        TimelineTrackModel v1, v2, a1;
        TimelineClip a, b, c, d, m;
        using (var tx = doc.BeginTransaction("Set up"))
        {
            (v1, v2, a1) = (tx.InsertTrack(0, TimelineTrackKind.Video), tx.InsertTrack(1, TimelineTrackKind.Video), tx.InsertTrack(2, TimelineTrackKind.Audio));
            a = tx.PlaceClip(v1, new TimelineClip
            {
                SourcePath = "D:/media/a.mp4", AudioStreamIndex = 0, End = 10, MediaOffset = 5, SourceDuration = 60, FadeInSeconds = 1, FadeOutSeconds = 2,
                State = new LayerState
                {
                    Name = "A", KeysOpacity = [new(1, 100), new(6, 0)],
                    Effects = [new EffectState { Type = "builtin.color.eq", Id = Fixed(1), Range = [2, 8], Keys = new() { ["contrast"] = [new(3, 1), new(5, 1.5)] } }],
                },
            });
            c = tx.PlaceClip(v1, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/c.png", Start = 12, End = 15 });
            b = tx.PlaceClip(v2, new TimelineClip { SourcePath = "D:/media/b.mp4", AudioStreamIndex = 0, Start = 2, End = 8, SourceDuration = 20 });
            d = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Text, Start = 11, End = 14 });
            m = tx.PlaceClip(a1, new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/music.wav", AudioStreamIndex = 0, End = 30, SourceDuration = 120 });
            tx.Commit();
        }
        (names[a.Id], names[b.Id], names[c.Id], names[d.Id], names[m.Id]) = ("A", "B", "C", "D", "M");
        Show(text, doc, names, problems, "Set up at 25 fps (bottom first)");
        Expect(problems, ReferenceEquals(doc.VideoTracks[0], v1) && v1.TrackNumber == 1 && v2.TrackNumber == 2 && a1.TrackNumber == 1, "track 1 is VideoTracks[0]; the audio track is Audio 1");
        Expect(problems, doc.TotalDuration == 30 && commits.Count == 1, "the sequence lasts 30 s; one commit");

        TimelineClip a2;
        using (var tx = doc.BeginTransaction("Split"))
        {
            a2 = tx.SplitClipAt(a, 4.01);
            tx.Commit();
        }
        names[a2.Id] = "A2";
        Show(text, doc, names, problems, "Split A at 4.01 (lands on frame 100, 4 s)");
        Expect(problems, a.End == a2.Start && a2.Start == 4 && a2.MediaOffset == 9 && a.FadeInSeconds == 1 && a.FadeOutSeconds == 0 && a2.FadeInSeconds == 0 && a2.FadeOutSeconds == 2,
            "A is 0-4 with the fade in, A2 4-10 from 9 s of the file with the fade out, meeting on one double");
        Expect(problems, Times(a.State.KeysOpacity) == "1,4" && Times(a2.State.KeysOpacity) == "0,2",
            "4.3D: A keeps its keys before the cut and gets one on it; A2 one at its start and its keys after, moved 4 s back");
        Expect(problems, SameCurve([new(1, 100), new(6, 0)], a.State.KeysOpacity!, a2.State.KeysOpacity!, 4, 0, 10) < 1e-9,
            "the two halves' opacity is the whole clip's, moment for moment");
        Expect(problems, a2.State.Effects![0].Range is [0, 4] && Times(a2.State.Effects[0].Keys!["contrast"]) == "0,1" && a.State.Effects![0].Range is [2, 4]
                         && Times(a.State.Effects[0].Keys!["contrast"]) == "3,4",
            "the effect range across the cut ends there on A and starts there on A2; its keys split as the opacity's (copies, not shared)");

        using (var tx = doc.BeginTransaction("Trim start"))
        {
            var later = tx.TrimClipStart(a2, 5);
            var back = tx.TrimClipStart(a2, 3);
            Line(text, $"\n(trim A2's start to 5: moved {N(later)}; then to 3: moved {N(back)}, stopping at A's end)");
            Expect(problems, later == 1 && back == -1 && a2.Start == a.End && a2.MediaOffset == 9 && Times(a2.State.KeysOpacity) == "0,2",
                "trimming A2's start goes forward 1 s, then back only to A's end, its file offset and keyframes following");
            tx.Commit();
        }

        using (var tx = doc.BeginTransaction("Ripple trim start"))
        {
            var moved = tx.TrimClipStart(a2, 6, ripple: true, rippleAllUnlockedTracks: true);
            Expect(problems, moved == 2 && a2.Start == 4 && a2.End == 8 && a2.MediaOffset == 11, "a ripple trim of A2's head keeps its start, eats 2 s of its file");
            tx.Commit();
        }
        Show(text, doc, names, problems, "Ripple trim A2's head by 2 s on every track: C and D follow its old end (10 s); B and M start before it");
        Expect(problems, c.Start == 10 && d.Start == 9 && b.Start == 2 && m.Start == 0, "C to 10, D to 9; B and M stay");

        using (var tx = doc.BeginTransaction("Ripple trim end"))
        {
            Expect(problems, tx.TrimClipEnd(a2, 9, ripple: true) == 1, "a ripple trim of A2's end to 9 s moves it 1 s");
            tx.Commit();
        }
        Show(text, doc, names, problems, "Ripple trim A2's end to 9 s: what started from its old end follows");
        Expect(problems, c.Start == 11 && d.Start == 10, "C to 11, D to 10");

        using (var tx = doc.BeginTransaction("Roll"))
        {
            Expect(problems, tx.RollCut(a, a2, 3) == -1, "the roll moves the cut 1 s earlier");
            tx.Commit();
        }
        Show(text, doc, names, problems, "Roll the cut between A and A2 to 3 s");
        Expect(problems, a.End == 3 && a2.Start == 3 && a.End == a2.Start && a2.MediaOffset == 10 && Times(a2.State.KeysOpacity) == "-1,1", "A 0-3, A2 3-9 from 10 s, its keyframes on their frames");

        using (var tx = doc.BeginTransaction("Slip and J/L"))
        {
            var slipped = tx.SlipClip(a2, 2.5);
            var (lead, tail) = tx.TrimAudioSplit(a2, 0.6, 1);
            var atStart = tx.TrimAudioSplit(a, 2, 0);
            var back = tx.SlipClip(a2, -100);
            var sooner = tx.TrimClipStart(a2, 2, ripple: true, rippleAllUnlockedTracks: false);
            Line(text, $"\n(slip A2 by 2.5 s: {N(slipped)}; its sound from {N(lead)} s before to {N(tail)} s after its picture; A's sound cannot lead at the timeline's start: {N(atStart.LeadIn)}; slip A2 back 100 s: {N(back)}; A2's start to 2 s in a ripple trim: {N(sooner)})");
            Expect(problems, slipped == 2.5 && lead == 0.6 && tail == 1 && atStart == (0, 0), "slip 2.5 s; a J/L cut of 0.6 and 1 s; none at the timeline's start");
            Expect(problems, Math.Abs(back + 11.9) < 1e-9 && Math.Abs(a2.MediaOffset - 0.6) < 1e-9, "slipping back stops where the J-cut's sound reaches the file's start (0.6 s in)");
            Expect(problems, sooner == 0 && a2.Start == 3, "A2's start cannot move sooner, even in a ripple trim: its file is used up by its J-cut");
            tx.Commit();
        }
        Show(text, doc, names, problems, "Slip A2, give it a J/L cut, slip it back as far as its file allows");

        using (var tx = doc.BeginTransaction("Delete and move"))
        {
            tx.DeleteClip(a2);
            tx.MoveClip(b, v1, 3);
            tx.Commit();
        }
        Show(text, doc, names, problems, "Delete A2 from Video 1, then move B from Video 2 onto Video 1 where A2 was");
        Expect(problems, v1.Clips.Contains(b) && v2.Clips.Count == 1 && b.Start == a.End && b.TransitionIn == 0, "B on Video 1 against A; only D left on Video 2");

        var before = commits.Count;
        using (var tx = doc.BeginTransaction("Refused move"))
        {
            var refused = false;
            try
            {
                tx.MoveClip(d, v1, 8.5);
            }
            catch (TimelineEditException ex)
            {
                refused = true;
                Line(text, $"\n(D dropped at 8.5 s on Video 1, over B's end and, put against B, onto C: refused: {ex.Message})");
            }
            Expect(problems, refused && v2.Clips.Contains(d) && d.Start == 10 && !v1.Clips.Contains(d), "the refused move changed nothing (each edit is a step of its own)");
            tx.Commit();
        }
        Expect(problems, commits.Count == before, "a transaction that changed nothing raises no commit");

        using (var tx = doc.BeginTransaction("Drag-over crossfade"))
        {
            tx.MoveClip(c, v1, 8.4);
            tx.Commit();
        }
        Show(text, doc, names, problems, "C dropped at 8.4 s, 0.6 s over B's end: put against B, crossfading from it");
        Expect(problems, c.Start == b.End && c.Start == 9 && c.TransitionIn == 0.6, "C 9-12 with a 0.6 s crossfade from B");

        using (var tx = doc.BeginTransaction("Close gap"))
        {
            tx.TrimClipEnd(a, 2);
            Expect(problems, tx.CloseGap(v1, 2, 3, rippleAllUnlockedTracks: false) == 1, "closing the gap moves B and C back 1 s");
            tx.Commit();
        }
        Show(text, doc, names, problems, "Trim A's end to 2 s, then close the 1 s gap on Video 1 only");
        Expect(problems, b.Start == 2 && c.Start == 8 && c.TransitionIn == 0.6 && d.Start == 10, "B 2-8, C 8-11 still crossfading; D stays");

        using (var tx = doc.BeginTransaction("Ripple delete"))
        {
            Expect(problems, tx.RippleDelete(b, rippleAllUnlockedTracks: true) == 6, "ripple-deleting B closes its 6 s");
            tx.Commit();
        }
        Show(text, doc, names, problems, "Ripple-delete B on every track: C now meets A, and its crossfade is A's");
        Expect(problems, c.Start == a.End && c.Start == 2 && c.TransitionIn == 0.6 && d.Start == 4 && m.Start == 0, "C 2-5 from A, D 4-7, M stays");

        v2.IsLocked = true;
        using (var tx = doc.BeginTransaction("Locked"))
        {
            Expect(problems, tx.RippleDelete(a, rippleAllUnlockedTracks: true) == 2, "ripple-deleting A closes its 2 s");
            var refused = false;
            try
            {
                tx.MoveClip(d, v1, 5);
            }
            catch (TimelineEditException)
            {
                refused = true;
            }
            Expect(problems, refused, "a clip on a locked track cannot be moved");
            tx.Commit();
        }
        Show(text, doc, names, problems, "Lock Video 2, ripple-delete A: D stays; C goes to the start, and its crossfade with it");
        Expect(problems, c.Start == 0 && c.TransitionIn == 0 && d.Start == 4, "C 0-3 with no crossfade; D still 4-7");
        v2.IsLocked = false;

        TimelineTrackModel bottom;
        using (var tx = doc.BeginTransaction("New track"))
        {
            tx.MoveClip(d, v2, 0, insertTrackAtIndex: 0);
            tx.Commit();
            bottom = doc.Tracks[0];
        }
        Show(text, doc, names, problems, "Move D onto a new track inserted at the bottom: the video tracks are numbered again");
        Expect(problems, ReferenceEquals(doc.VideoTracks[0], bottom) && bottom.Clips.Contains(d) && bottom.DisplayName == "Video 1" && v1.TrackNumber == 2 && v2.TrackNumber == 3 && v2.Clips.Count == 0,
            "the new track is Video 1 (VideoTracks[0]) with D; the others are 2 and 3");

        TimelineClip m2, m3;
        using (var tx = doc.BeginTransaction("Ripple move"))
        {
            m2 = tx.SplitClipAt(m, 10);
            m3 = tx.SplitClipAt(m2, 20);
            (names[m2.Id], names[m3.Id]) = ("M2", "M3");
            tx.MoveClip(m3, a1, 0, ripple: true);
            var refused = false;
            try
            {
                tx.MoveClip(m, a1, 15, ripple: true);
            }
            catch (TimelineEditException)
            {
                refused = true;
            }
            Expect(problems, refused, "an inserting move cannot land inside a clip");
            tx.Commit();
        }
        Show(text, doc, names, problems, "Split M at 10 and 20 s, then a ripple move of M3 to 0 s: M and M2 make room (M to 15 s inside M2 refused)");
        Expect(problems, a1.Clips is [var first, var second, var third] && first == m3 && second == m && third == m2 && m.Start == 10 && m2.Start == 20 && m3.MediaOffset == 20,
            "Audio 1 is M3 (from 20 s of the file), M, M2");

        string lost;
        before = commits.Count;
        using (var outer = doc.BeginTransaction("Outer"))
        {
            var m4 = outer.SplitClipAt(m, 15);
            names[m4.Id] = "M4";
            using (var inner = doc.BeginTransaction("Inner, rolled back"))
            {
                lost = inner.SplitClipAt(m2, 25).Id.ToString();
                var outerRefused = false;
                try
                {
                    outer.SplitClipAt(m, 12);
                }
                catch (InvalidOperationException ex) when (ex is not TimelineEditException)
                {
                    outerRefused = true;
                }
                Expect(problems, outerRefused, "the outer transaction cannot edit while one inside it is open");
            }
            using (var inner = doc.BeginTransaction("Inner, committed"))
            {
                inner.TrimClipEnd(m4, 19);
                inner.Commit();
            }
            outer.Commit();
            Expect(problems, commits.Count == before + 1 && commits[^1].Label == "Outer" && commits[^1].TouchedClipIds.Contains(m4.Id)
                             && !commits[^1].TouchedClipIds.Any(id => id.ToString() == lost), "one commit, the outer's, naming M4 and not the rolled-back split");
        }
        Show(text, doc, names, problems, "Nested: split M at 15 s; inside, a split of M2 rolled back; inside, M4's end trimmed to 19 s and committed");
        Expect(problems, a1.Clips.Count == 4 && m2.Start == 20 && m2.End == 30, "M2 was not split");

        var saved = JsonSerializer.Serialize(doc);
        var tracks = doc.Tracks.ToList();
        using (var tx = doc.BeginTransaction("Rolled back"))
        {
            tx.TrimClipStart(c, 1);
            tx.MoveClip(d, v1, 4);
            tx.InsertTrack(0, TimelineTrackKind.Audio);
            tx.RippleDelete(m3, rippleAllUnlockedTracks: true);
            tx.SplitClipAt(m2, 15);
        }
        Expect(problems, JsonSerializer.Serialize(doc) == saved && doc.Tracks.SequenceEqual(tracks) && ReferenceEquals(bottom.Clips[0], d),
            "a transaction disposed without a commit puts back every clip, track and number, in the same objects");
        Line(text, $"\n(a transaction of a trim, a move, a new track, a ripple delete and a split, disposed uncommitted: the document is as it was)");

        using (var tx = doc.BeginTransaction("Bounds"))
        {
            var e = tx.PlaceClip(v2, new TimelineClip { SourcePath = "D:/media/e.mp4", Start = 2, End = 12, MediaOffset = 3, SourceDuration = 5, Speed = 2 });
            names[e.Id] = "E";
            var placed = e.Duration;
            var longer = tx.TrimClipEnd(e, 4);
            var sooner = tx.TrimClipStart(e, 1);
            Line(text, $"\n(E at twice its speed, 3 s into a file of 5 s, asked for 10 s: lasts {N(placed)} s; its end to 4 s: {N(longer)}; its start to 1 s: {N(sooner)}, now from {N(e.MediaOffset)} s of the file)");
            Expect(problems, placed == 1 && longer == 0 && sooner == -1 && e.MediaOffset == 1 && e.Duration == 2, "a clip and its trims stay inside its file, at its speed");
            tx.Commit();
        }
        Show(text, doc, names, problems, "E placed on Video 3");

        Viewport(text, problems);
    }

    /// <summary>The binary search of the visible clips against a plain scan, on a track of 2,000 clips.</summary>
    private static void Viewport(StringBuilder text, List<string> problems)
    {
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        TimelineTrackModel track;
        using (var tx = doc.BeginTransaction("Many"))
        {
            track = tx.InsertTrack(0, TimelineTrackKind.Video);
            for (var i = 0; i < 2000; i++)
                tx.PlaceClip(track, new TimelineClip { ContentKind = LayerKind.Image, Start = i, End = i + 0.8 }, allowCrossfadeOverlap: false);
            tx.Commit();
        }

        var (wrong, state) = (0, 99UL);
        for (var k = 0; k < 500; k++)
        {
            var from = Next(ref state) * 2010 - 5;
            var to = from + Next(ref state) * (k % 10 == 0 ? 0.05 : 40);
            var found = track.ClipsIntersecting(from, to).ToArray();
            var scanned = track.Clips.Where(clip => clip.End > from && clip.Start < to).ToArray();
            wrong += found.SequenceEqual(scanned) ? 0 : 1;
        }
        var misplaced = Enumerable.Range(0, track.Clips.Count).Count(i => track.IndexOf(track.Clips[i]) != i);
        Line(text, $"\nViewport: 2000 clips, 500 stretches found by binary search, {wrong} unlike a full scan; IndexOf wrong for {misplaced}");
        Expect(problems, wrong == 0 && misplaced == 0 && doc.FindProblems().Count == 0, "ClipsIntersecting and IndexOf agree with a full scan");
    }

    private static string Times(List<Keyframe>? keys) => keys is null ? "" : string.Join(",", keys.Select(k => N(k.Time)));

    /// <summary>
    /// How far two halves' keys (the second's counted from <paramref name="cut"/>) stray from the whole's, at 400 moments
    /// from <paramref name="from"/> to <paramref name="to"/> on the whole clip's clock.
    /// </summary>
    private static double SameCurve(List<Keyframe> whole, List<Keyframe> first, List<Keyframe> second, double cut, double from, double to)
    {
        var most = 0.0;
        for (var i = 0; i <= 400; i++)
        {
            var t = from + (to - from) * i / 400;
            var half = t < cut ? EasingCurves.ValueAt(first, t) : EasingCurves.ValueAt(second, t - cut);
            most = Math.Max(most, Math.Abs(half - EasingCurves.ValueAt(whole, t)));
        }

        return most;
    }

    // ----- Folds (4.3D) -----

    private static void Folds(StringBuilder text, List<string> problems)
    {
        var options = (JsonSerializerOptions)typeof(ProjectStore).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        var names = new Dictionary<Guid, string>();
        TimelineTrackModel v1, v2, a1;
        TimelineClip main, logo, music;
        using (var tx = doc.BeginTransaction("Set up"))
        {
            (v1, v2, a1) = (tx.InsertTrack(0, TimelineTrackKind.Video), tx.InsertTrack(1, TimelineTrackKind.Video), tx.InsertTrack(2, TimelineTrackKind.Audio));
            main = tx.PlaceClip(v1, new TimelineClip { ContentKind = LayerKind.MainVideo, SourcePath = "D:/media/talk.mp4", AudioStreamIndex = 0, End = 20, SourceDuration = 20, State = new LayerState { Name = "Talk" } });
            logo = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/logo.png", Start = 5, End = 15, State = new LayerState { Name = "Logo" } });
            music = tx.PlaceClip(a1, new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/music.wav", AudioStreamIndex = 0, End = 30, SourceDuration = 120, State = new LayerState { Name = "Music" } });
            tx.Commit();
        }
        (names[main.Id], names[logo.Id], names[music.Id]) = ("T", "L", "M");
        var before = Plays(doc);
        Show(text, doc, names, problems, "Set up at 25 fps: the talk, a logo over it from 5 s, music under both");

        FoldedSegment first, second;
        using (var tx = doc.BeginTransaction("Fold two silences"))
        {
            second = tx.FoldStretch(v1, 12.01, 13, rippleAllUnlockedTracks: true) ?? throw new InvalidOperationException("12-13 s was not folded");
            first = tx.FoldStretch(v1, 4, 6.4, rippleAllUnlockedTracks: true) ?? throw new InvalidOperationException("4-6.4 s was not folded");
            tx.Commit();
        }
        Name(doc, names);
        Show(text, doc, names, problems, "Fold 12-13 s (12.01 lands on frame 300) and then 4-6.4 s out of every track, the later first");
        Expect(problems, first is { Length: 2.4, AtEnd: false, AllTracks: true } && second is { Length: 1 } && doc.Folds?.Count == 2,
            "two folds, 2.4 s and 1 s, each at the start of the clip after it");
        Expect(problems, Math.Abs(doc.TotalDuration - 26.6) < 1e-9 && doc.FindProblems().Count == 0, "the timeline is 3.4 s shorter, and sound");
        Expect(problems, first.SeamIn(doc) == 4 && Math.Abs(second.SeamIn(doc)!.Value - 9.6) < 1e-9, "the seams: 4 s, and 12 s moved back by the first fold to 9.6 s");
        Expect(problems, first.Clips.Count == 3 && second.Clips.Count == 3 && first.Clips.All(c => c.Clip.Start >= 0 && c.Clip.End <= 2.4 + 1e-9),
            "each fold keeps what lay on all three tracks, counted from its seam");
        foreach (var fold in new[] { first, second })
        {
            var kept = string.Join(" ", fold.Clips.Select(c =>
                $"[{doc.Tracks.First(t => t.Id == c.TrackId).DisplayName}: {c.Clip.State.Name} {N(c.Clip.Start)}-{N(c.Clip.End)} @{N(c.Clip.MediaOffset)}]"));
            Line(text, $"fold at {N(fold.SeamIn(doc) ?? -1)} s ({N(fold.Length)} s): {kept}");
        }

        // Rolled back, a fold leaves nothing behind.
        var shown = Plays(doc);
        using (var tx = doc.BeginTransaction("A fold not kept"))
        {
            tx.FoldStretch(v1, 1, 2, rippleAllUnlockedTracks: false);
            Expect(problems, doc.Folds?.Count == 3, "inside the transaction, a third fold");
        }
        Expect(problems, doc.Folds?.Count == 2 && Plays(doc) == shown, "rolled back: the two folds and the timeline as they were");

        // Written as a project writes its timeline, and read back: the folds come back with their clips.
        var json = JsonSerializer.Serialize(doc, options);
        var back = JsonSerializer.Deserialize<TimelineDocument>(json, options)!;
        Expect(problems, JsonSerializer.Serialize(back, options) == json && back.Folds is { Count: 2 } && back.Folds[0].Clips.Count == 3
                         && back.Folds[0].SeamIn(back) == second.SeamIn(doc), "written and read back, the folds are the same");
        Expect(problems, JsonSerializer.Serialize(doc.Clone(), options) == json, "a copy of the document keeps its folds");

        // Put back, latest first: the timeline plays as it did before either was folded.
        using (var tx = doc.BeginTransaction("Put both back"))
        {
            tx.UnfoldSegment(second);
            tx.UnfoldSegment(first);
            tx.Commit();
        }
        Name(doc, names);
        Show(text, doc, names, problems, "Put both back, the later first");
        Expect(problems, doc.Folds is null && doc.TotalDuration == 30 && doc.FindProblems().Count == 0, "no folds left; 30 s again, and sound");
        Expect(problems, Plays(doc) == before, "every track plays what it played before the folds, at every moment");
    }

    /// <summary>What each track plays at 241 moments of 30 s: its clip's file (and the moment of it, for a video or a sound), or nothing.</summary>
    private static string Plays(TimelineDocument doc)
    {
        var played = new StringBuilder();
        for (var i = 0; i <= 240; i++)
        {
            var t = i / 8.0;
            foreach (var track in doc.Tracks)
            {
                var clip = track.Clips.FirstOrDefault(c => c.Start <= t + 1e-9 && t < c.End - 1e-9);
                played.Append(clip is null ? "-" : clip.PlaysFile ? $"{clip.SourcePath}@{N(Math.Round(clip.MediaOffset + (t - clip.Start) * clip.Speed, 6))}" : clip.SourcePath).Append(' ');
            }

            played.Append('\n');
        }

        return played.ToString();
    }

    /// <summary>Names the pieces of the set-up's clips that splits made after their first: T2, T3, ...</summary>
    private static void Name(TimelineDocument doc, Dictionary<Guid, string> names)
    {
        foreach (var track in doc.Tracks)
        {
            foreach (var clip in track.Clips.Where(c => !names.ContainsKey(c.Id)))
            {
                var stem = clip.State.Name[..1];
                var n = 2;
                while (names.ContainsValue($"{stem}{n}"))
                    n++;
                names[clip.Id] = $"{stem}{n}";
            }
        }
    }

    // ----- A clip split between two keyframes (4.3D) -----

    private static void SplitKeys(StringBuilder text, List<string> problems)
    {
        // One clip for each easing, its X moving from 0.1 to 0.7 between 1 s and 5 s and back to 0.4 by 9 s (a Bézier of its
        // own handles), split at 2.6 s, then its second half again at 1.4 s (4 s of the first clip): a part of a part.
        foreach (var easing in Enum.GetValues<EasingType>())
        {
            var first = new Keyframe(1, 0.1, easing);
            if (easing == EasingType.CubicBezier)
                first = first.WithEasing(easing, (0.2, -0.4, 0.7, 1.3));
            List<Keyframe> whole = [first, new(5, 0.7, easing), new(9, 0.4)];

            var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
            TimelineClip clip;
            using (var tx = doc.BeginTransaction("Set up"))
            {
                var track = tx.InsertTrack(0, TimelineTrackKind.Video);
                clip = tx.PlaceClip(track, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/p.png", End = 10, State = new LayerState { KeysX = whole } });
                tx.Commit();
            }

            TimelineClip second, third;
            using (var tx = doc.BeginTransaction("Split"))
            {
                second = tx.SplitClipAt(clip, 2.6);
                third = tx.SplitClipAt(second, 4);
                tx.Commit();
            }

            var (a, b, c) = (clip.State.KeysX!, second.State.KeysX!, third.State.KeysX!);
            var most = 0.0;
            for (var i = 0; i <= 1000; i++)
            {
                var t = 10.0 * i / 1000;
                var half = t < 2.6 ? EasingCurves.ValueAt(a, t) : t < 4 ? EasingCurves.ValueAt(b, t - 2.6) : EasingCurves.ValueAt(c, t - 4);
                most = Math.Max(most, Math.Abs(half - EasingCurves.ValueAt(whole, t)));
            }

            string Keys(List<Keyframe> keys) => string.Join(" ", keys.Select(k => k.IsPart
                ? $"{N(k.Time)}:{N(Math.Round(k.Value, 6))} {k.Easing} part {N(Math.Round(k.Part.Start, 6))}-{N(Math.Round(k.Part.End, 6))}"
                : $"{N(k.Time)}:{N(Math.Round(k.Value, 6))} {k.Easing}"));
            Line(text, $"\n== {easing}");
            Line(text, $"  0-2.6:  {Keys(a)}");
            Line(text, $"  2.6-4:  {Keys(b)}");
            Line(text, $"  4-10:   {Keys(c)}");
            var read = EasingCurves.IsPieced(easing) ? $"{EasingCurves.PiecesOf(b[0]).Length - 1} straight pieces" : EasingCurves.Expression("p", b[0]);
            Line(text, $"  as FFmpeg reads the middle half's first stretch: {read}");
            Line(text, $"  most it strays from the whole clip's motion in 1001 moments: {(most < 1e-9 ? "none" : N(most))}");
            Expect(problems, most < 1e-9, $"{easing}: the three pieces move as the whole clip did");
            Expect(problems, a[^1].Time == 2.6 && b[0].Time == 0 && b[^1].Time == 1.4 && c[0].Time == 0 && Math.Abs(a[^1].Value - b[0].Value) < 1e-12,
                $"{easing}: a key on each cut, the same value on both sides");
        }
    }

    private static Guid Fixed(int n) => new(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{n:D12}"));

    private static void Show(StringBuilder text, TimelineDocument doc, Dictionary<Guid, string> names, List<string> problems, string step)
    {
        text.Append("\n== ").Append(step).Append('\n');
        foreach (var track in doc.Tracks)
        {
            var line = new StringBuilder("  ").Append(track.DisplayName).Append(track.IsLocked ? " (locked)" : "").Append(':');
            foreach (var clip in track.Clips)
                line.Append(' ').Append(Describe(clip, names));
            text.Append(line).Append('\n');
        }

        foreach (var problem in doc.FindProblems(requireFrames: true))
            problems.Add($"after \"{step}\": {problem}");
    }

    private static string Describe(TimelineClip clip, Dictionary<Guid, string> names)
    {
        var text = new StringBuilder("[").Append(names.GetValueOrDefault(clip.Id, "?")).Append(' ').Append(N(clip.Start)).Append('-').Append(N(clip.End));
        if (clip.PlaysFile)
            text.Append(" @").Append(N(clip.MediaOffset));
        if (clip.Speed != 1)
            text.Append(" x").Append(N(clip.Speed));
        if (clip.TransitionIn > 0)
            text.Append(" crossfade ").Append(N(clip.TransitionIn));
        if (clip.FadeInSeconds > 0)
            text.Append(" in ").Append(N(clip.FadeInSeconds));
        if (clip.FadeOutSeconds > 0)
            text.Append(" out ").Append(N(clip.FadeOutSeconds));
        if (clip.AudioLeadInSeconds != 0 || clip.AudioTailOutSeconds != 0)
            text.Append(" sound -").Append(N(clip.AudioLeadInSeconds)).Append(" +").Append(N(clip.AudioTailOutSeconds));
        if (clip.State.KeysOpacity is { Count: > 0 } keys)
            text.Append(" keys ").Append(Times(keys));
        return text.Append(']').ToString();
    }

    // ----- Edge cases -----

    private static void EdgeCases(StringBuilder text, List<string> problems)
    {
        FileEdges(text, problems);
        FreeEdits(text, problems);
        SoundCuts(text, problems);
        HeldBack(text, problems);
        Commits(text, problems);
        Guards(text, problems);
    }

    /// <summary>A document with one video track holding these clips, placed as they are.</summary>
    private static (TimelineDocument Doc, TimelineTrackModel Track) Placed(FrameRate rate, params TimelineClip[] clips)
    {
        var doc = new TimelineDocument { FrameRate = rate };
        using var tx = doc.BeginTransaction("Set up");
        var track = tx.InsertTrack(0, TimelineTrackKind.Video);
        foreach (var clip in clips)
            tx.PlaceClip(track, clip, allowCrossfadeOverlap: false);
        tx.Commit();
        return (doc, track);
    }

    private static string Problems(TimelineDocument doc, bool requireFrames = true) => string.Join("; ", doc.FindProblems(requireFrames).Take(3));

    /// <summary>Edges on a frame stay on it (the rates whose frames are not whole microseconds), and nothing at a file's end moves the wrong way.</summary>
    private static void FileEdges(StringBuilder text, List<string> problems)
    {
        text.Append("\nAt a file's ends:\n");
        foreach (var rate in new[] { FrameRate.Fps60, FrameRate.Ntsc5994, FrameRate.Ntsc23976 })
        {
            double At(long frame) => rate.Quantize(rate.SecondsFromFrame(frame));
            var least = (long)Math.Ceiling(TimelineDocument.MinimumClipSeconds * rate.Fps - 1e-3);
            var whole = new TimelineClip { SourcePath = "D:/media/w.mp4", End = At(62), SourceDuration = At(62) };
            var after = new TimelineClip { SourcePath = "D:/media/x.mp4", Start = At(62), End = At(124), SourceDuration = 60 };
            var (doc, track) = Placed(rate, whole, after);
            using var tx = doc.BeginTransaction("Edges");
            var longer = tx.TrimClipEnd(whole, 5);
            var sooner = tx.TrimClipStart(after, 0, ripple: true);
            var rolled = tx.RollCut(whole, after, At(62) + 1);
            var exact = tx.PlaceClip(track, new TimelineClip { SourcePath = "D:/media/y.mp4", Start = At(200), End = At(301), SourceDuration = At(101) });
            var lead = tx.TrimAudioSplit(tx.PlaceClip(track, new TimelineClip { SourcePath = "D:/media/z.mp4", AudioStreamIndex = 0, Start = At(400), End = At(500), MediaOffset = 30, SourceDuration = 60 }), 100, 0).LeadIn;
            var shortest = tx.SplitClipAt(exact, At(200 + least));
            var kept = tx.TrimClipEnd(exact, 0);
            Line(text, $"  {rate}: the whole file's end to 5 s: {N(longer)}; the next clip's head (its file from 0) sooner: {N(sooner)}; the cut 1 s later: {N(rolled)}; a clip asked for its file's 101 frames lasts {rate.FrameFromSeconds(shortest.End) - rate.FrameFromSeconds(exact.Start)}; a J-cut asked for 100 s leads {rate.FrameFromSeconds(lead)} frames (its start); a {rate.FrameFromSeconds(exact.Duration)}-frame clip trimmed shorter: {N(kept)}");
            Expect(problems, longer == 0 && sooner == 0 && rolled == 0 && whole.End == At(62) && after.Start == At(62), $"{rate}: no edge at a file's end moves, nor the wrong way");
            Expect(problems, shortest.End == At(301) && lead == At(400) && kept == 0 && exact.End == At(200 + least), $"{rate}: a file's last frame, the timeline's start and the shortest clip are reached exactly");
            Expect(problems, doc.FindProblems(requireFrames: true).Count == 0, $"{rate}: no problems at the file's ends ({Problems(doc)})");
            tx.Commit();
        }
    }

    /// <summary>Precision edits off the frames, in a quantized document, and quantized edits after them.</summary>
    private static void FreeEdits(StringBuilder text, List<string> problems)
    {
        var a = new TimelineClip { SourcePath = "D:/media/a.mp4", End = 4, SourceDuration = 60 };
        var b = new TimelineClip { SourcePath = "D:/media/b.mp4", Start = 4, End = 6, SourceDuration = 60 };
        var (doc, track) = Placed(FrameRate.Fps25, a, b);
        using var tx = doc.BeginTransaction("Free");
        var later = tx.TrimClipEnd(a, 4.013, ripple: true, freeUnquantized: true);
        var (met, aEnd) = (a.End == b.Start, a.End);

        var other = tx.InsertTrack(1, TimelineTrackKind.Video);
        var c = tx.PlaceClip(other, new TimelineClip { SourcePath = "D:/media/c.mp4", End = 4, MediaOffset = 6, SourceDuration = 10 });
        var eaten = tx.TrimClipStart(c, 0.013, ripple: true, rippleAllUnlockedTracks: false, freeUnquantized: true);

        tx.TrimClipEnd(a, 3.48);
        var closed = tx.CloseGap(track, a.End, b.Start, rippleAllUnlockedTracks: false);
        Line(text, $"\nFree edits at 25 fps: A's end to 4.013 s, rippling: {N(later)}, B {(met ? "follows on the same double" : "does not meet it")}; C's head 0.013 s in, rippling: its file now ends at {N(c.MediaEnd)} of 10 s; A's end back on a frame (3.48 s), and the gap to B (from 4.013 s, off the frames) closed: {N(closed)}");
        Expect(problems, Math.Abs(later - 0.013) < 1e-9 && met && aEnd == 4.013, "a free ripple trim moves its follower by exactly as much, onto the same double");
        Expect(problems, Math.Abs(eaten - 0.013) < 1e-9 && c.MediaEnd <= 10 + 1e-9, "a free ripple trim of a head keeps the clip inside its file");
        Expect(problems, Math.Abs(closed - 0.533) < 1e-9 && b.Start == a.End, "a gap ending off the frames closes to the clip before it");
        Expect(problems, doc.FindProblems().Count == 0 && doc.FindProblems(requireFrames: true).Count > 0, $"free edges are allowed, and found only when frames are required ({Problems(doc, false)})");
        tx.Commit();
    }

    /// <summary>J and L cuts: letting a sound out again, the shortest sound against the trims, a lead-in against moves and ripples.</summary>
    private static void SoundCuts(StringBuilder text, List<string> problems)
    {
        var clip = new TimelineClip { SourcePath = "D:/media/s.mp4", AudioStreamIndex = 0, Start = 3, End = 9, MediaOffset = 10, SourceDuration = 60 };
        var (doc, track) = Placed(FrameRate.Fps25, clip);
        using (var tx = doc.BeginTransaction("Let out"))
        {
            var far = tx.TrimAudioSplit(clip, -100, 1);
            var kept = tx.TrimAudioSplit(clip, double.NaN, 1.6);
            Line(text, $"\nJ/L at 25 fps: a 6 s clip's sound asked to start 100 s inside it: -{N(-far.LeadIn)} / +{N(far.TailOut)} (2 frames of sound left); its tail to 1.6 s, its lead not a number: -{N(-kept.LeadIn)} / +{N(kept.TailOut)}");
            Expect(problems, far == (-6.92, 1) && kept == (-6.92, 1.6), "a sound pulled inside its picture is let out on the side that moved, to the shortest sound; a lead that is not a number is left as it is");
            tx.Commit();
        }

        var before = new TimelineClip { SourcePath = "D:/media/p.mp4", End = 10, SourceDuration = 100 };
        var middle = new TimelineClip { SourcePath = "D:/media/m.mp4", AudioStreamIndex = 0, Start = 10, End = 10.2, MediaOffset = 5, SourceDuration = 100 };
        var next = new TimelineClip { SourcePath = "D:/media/n.mp4", Start = 10.2, End = 14, SourceDuration = 100 };
        var (cuts, _) = Placed(FrameRate.Fps25, before, middle, next);
        using (var tx = cuts.BeginTransaction("Shortest sound"))
        {
            var set = tx.TrimAudioSplit(middle, -1, 1);
            var rolled = tx.RollCut(before, middle, 10.04);
            Line(text, $"  a 0.2 s clip with its sound from 1 s inside to 1 s after: -{N(-set.LeadIn)} / +{N(set.TailOut)}; the cut before it a frame later: {N(rolled)}, leaving {N(middle.Duration)} s");
            Expect(problems, set == (-1, 1) && Math.Abs(rolled - 0.04) < 1e-9 && Math.Abs(middle.Duration - 0.16) < 1e-9, "a J/L clip whose sound is as long as its picture trims like any other");
            tx.Commit();
        }

        var y = new TimelineClip { SourcePath = "D:/media/y.mp4", AudioStreamIndex = 0, Start = 2, End = 4, MediaOffset = 5, SourceDuration = 60 };
        var (leads, lane) = Placed(FrameRate.Fps25, y);
        using (var tx = leads.BeginTransaction("Lead-in"))
        {
            tx.TrimAudioSplit(y, 1, 0);
            tx.MoveClip(y, lane, 0.2);
            var landed = y.Start;
            var below = tx.InsertTrack(0, TimelineTrackKind.Video);
            var z = tx.PlaceClip(below, new TimelineClip { ContentKind = LayerKind.Image, End = 0.08 });
            var back = tx.RippleDelete(z, rippleAllUnlockedTracks: true);
            Line(text, $"  a clip whose sound leads by 1 s, moved to 0.2 s: lands at {N(landed)}; a ripple delete before it: {N(back)}, it stays at {N(y.Start)}");
            Expect(problems, landed == 1 && back == 0 && y.Start == 1 && leads.FindProblems(requireFrames: true).Count == 0, "a J-cut's sound never starts before the timeline, and a ripple held back by it never turns into a push");
            tx.Commit();
        }
    }

    /// <summary>Ripples held back by another track; moves that change nothing; a drag-over that makes the moved clip the earlier; keys at 59.94; speed.</summary>
    private static void HeldBack(StringBuilder text, List<string> problems)
    {
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        var commits = new List<TimelineCommit>();
        TimelineTrackModel v1, v2, subtitles;
        TimelineClip a, b, c, d, s;
        using (var tx = doc.BeginTransaction("Set up"))
        {
            (v1, v2, subtitles) = (tx.InsertTrack(0, TimelineTrackKind.Video), tx.InsertTrack(1, TimelineTrackKind.Video), tx.InsertTrack(2, TimelineTrackKind.Subtitle));
            a = tx.PlaceClip(v1, new TimelineClip { ContentKind = LayerKind.Image, End = 4 });
            b = tx.PlaceClip(v1, new TimelineClip { ContentKind = LayerKind.Image, Start = 4, End = 6, TransitionIn = 0.4 });
            c = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Image, Start = 2, End = 5 });
            d = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Image, Start = 6, End = 8 });
            s = tx.PlaceClip(subtitles, new TimelineClip { ContentKind = LayerKind.Captions, End = 8 });
            tx.Commit();
        }
        doc.Committed += commits.Add;

        using (var tx = doc.BeginTransaction("Held back"))
        {
            var trimmed = tx.TrimClipEnd(a, 2, ripple: true);
            Line(text, $"\nHeld back: A's end 2 s sooner, rippling every track, while C on Video 2 lies 1 s before D: moved {N(trimmed)}; B {N(b.Start)}-{N(b.End)} crossfade {N(b.TransitionIn)}; D {N(d.Start)}-{N(d.End)}; the captions stay {N(s.Start)}-{N(s.End)}");
            Expect(problems, trimmed == -1 && a.End == 3 && b.Start == 3 && b.TransitionIn == 0.4 && d.Start == 5 && c.Start == 2 && s.End == 8,
                "a ripple trim is held back with what cannot follow, the follower meets it still (crossfade kept), and the captions never ripple");
            tx.Commit();
        }

        var count = commits.Count;
        using (var tx = doc.BeginTransaction("Nowhere"))
        {
            tx.MoveClip(b, v1, b.Start);
            tx.Commit();
        }
        Expect(problems, b.TransitionIn == 0.4 && commits.Count == count, "a move to where the clip is changes nothing: its crossfade stays, and no commit");

        using (var tx = doc.BeginTransaction("Away and back"))
        {
            tx.MoveClip(b, v1, 3.4);
            tx.MoveClip(b, v1, 3);
            Line(text, $"  B moved 0.4 s later, then back against A: crossfade {N(b.TransitionIn)} (it belonged to the cut it left)");
            Expect(problems, b.Start == 3 && b.TransitionIn == 0, "a crossfade belongs to its cut: moved away and back, it is gone");
            tx.Commit();
        }

        using (var tx = doc.BeginTransaction("Earlier"))
        {
            var e = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Text, Start = 9, End = 11 });
            tx.MoveClip(d, v2, 7.6);
            Line(text, $"  D (2 s) dropped at 7.6 s, 0.6 s over the start of E (9-11 s): D {N(d.Start)}-{N(d.End)}, E crossfades {N(e.TransitionIn)}");
            Expect(problems, d.Start == 7 && d.End == e.Start && e.TransitionIn == 0.6, "dropped over the start of a clip, the moved clip goes before it and the other crossfades from it");
            tx.Commit();
        }

        var ntsc = new TimelineClip { SourcePath = "D:/media/k.mp4", End = 30, SourceDuration = 60, State = new LayerState { KeysOpacity = [new(1, 100)] } };
        var (keyed, _) = Placed(FrameRate.Ntsc5994, ntsc);
        var drift = 0.0;
        using (var tx = keyed.BeginTransaction("Keys"))
        {
            for (var i = 0; i < 60; i++)
                tx.TrimClipStart(ntsc, ntsc.Start + FrameRate.Ntsc5994.FrameDuration);
            drift = Math.Max(drift, Math.Abs(ntsc.Start + ntsc.State.KeysOpacity![0].Time - 1));
            for (var i = 0; i < 60; i++)
                tx.TrimClipStart(ntsc, ntsc.Start - FrameRate.Ntsc5994.FrameDuration);
            drift = Math.Max(drift, Math.Abs(ntsc.Start + ntsc.State.KeysOpacity![0].Time - 1));
            tx.Commit();
        }
        Line(text, $"  59.94 fps: a keyframe at 1 s through 60 one-frame trims of its clip's start and 60 back: back at its start {N(ntsc.Start)}, the key at {N(ntsc.State.KeysOpacity![0].Time)}, never more than a microsecond-scale drift");
        Expect(problems, ntsc.Start == 0 && drift < 1e-4, $"keyframes stay on their content at NTSC rates (drift {drift})");

        var fast = new TimelineClip { SourcePath = "D:/media/f.mp4", End = 4, MediaOffset = 2, Speed = 2, SourceDuration = 60 };
        var (speedy, _) = Placed(FrameRate.Fps25, fast);
        using (var tx = speedy.BeginTransaction("Speed"))
        {
            var second = tx.SplitClipAt(fast, 1);
            Expect(problems, second.MediaOffset == 4 && second.MediaEnd == 10, "the second half of a clip at twice its speed starts 2 s further into its file");
            tx.Commit();
        }
    }

    /// <summary>Changes of tracks alone are commits too; an outer transaction rolled back with one still open inside it.</summary>
    private static void Commits(StringBuilder text, List<string> problems)
    {
        var doc = new TimelineDocument();
        var commits = new List<TimelineCommit>();
        doc.Committed += commits.Add;
        TimelineTrackModel added;
        using (var tx = doc.BeginTransaction("Add track"))
        {
            added = tx.InsertTrack(0, TimelineTrackKind.Audio, "Voice");
            tx.Commit();
        }
        Expect(problems, commits is [{ TouchedClipIds.Count: 0 } only] && only.TouchedTrackIds.Contains(added.Id), "adding a track is a commit of its own, naming the track");

        var outer = doc.BeginTransaction("Outer");
        outer.InsertTrack(0, TimelineTrackKind.Video);
        var inner = doc.BeginTransaction("Inner");
        inner.InsertTrack(0, TimelineTrackKind.Video);
        outer.Dispose();
        var reopened = doc.BeginTransaction("After");
        reopened.Commit();
        Line(text, $"\nCommits: adding a track raised {commits.Count} commit, naming {commits[0].TouchedTrackIds.Count} track; an outer transaction disposed with one open inside it leaves {doc.Tracks.Count} track");
        Expect(problems, doc.Tracks is [var left] && left == added && commits.Count == 1, "disposing an outer transaction rolls back the one still open inside it too, and the document can be edited again");
    }

    /// <summary>What a track takes; a refused placement; huge times; the views of the clips; a rollback of every property of a clip.</summary>
    private static void Guards(StringBuilder text, List<string> problems)
    {
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        using (var tx = doc.BeginTransaction("Guards"))
        {
            var lane = tx.InsertTrack(0, TimelineTrackKind.Switch);
            tx.PlaceClip(lane, new TimelineClip { SourcePath = "D:/media/v.mp4", End = 1 });
            var sound = new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/a.wav", AudioStreamIndex = 0, End = 1 };
            Expect(problems, Refused(() => tx.PlaceClip(lane, sound)), "a switch track takes a video clip and refuses a sound");

            var given = new TimelineClip { ContentKind = LayerKind.Image, Start = 0.5, End = 2, Speed = 50, FadeInSeconds = 100, TransitionIn = 20 };
            var json = JsonSerializer.Serialize(given);
            Expect(problems, Refused(() => tx.PlaceClip(lane, given, allowCrossfadeOverlap: false)) && JsonSerializer.Serialize(given) == json, "a refused placement leaves the clip as it was given");

            var still = tx.PlaceClip(lane, new TimelineClip { ContentKind = LayerKind.Image, Start = 2, End = 3 });
            tx.TrimClipEnd(still, double.MaxValue);
            var moved = !Refused(() => tx.MoveClip(still, lane, 1e18));
            Line(text, $"\nGuards: the end of an image trimmed to the largest double: ends at {N(still.End)} s; then moved to 10^18 s: {(moved ? "moved" : "refused")}");
            Expect(problems, still.End == 1e9 && !moved && doc.FindProblems(requireFrames: true).Count == 0, $"huge times are held to a billion seconds, on frames ({Problems(doc)})");
            tx.Commit();
        }

        var (views, row) = Placed(FrameRate.Fps25, new TimelineClip { ContentKind = LayerKind.Image, End = 1 }, new TimelineClip { ContentKind = LayerKind.Image, Start = 1, End = 2 });
        var spans = new[] { (1.0, 1.0), (1.0, 1.5), (0.5, 1.0), (-5.0, 0.0), (2.0, 9.0), (-1.0, 3.0) }
            .Select(span => row.ClipsIntersecting(span.Item1, span.Item2).ToArray().Select(clip => row.Clips.IndexOf(clip) + 1)).Select(found => $"[{string.Join(",", found)}]");
        Line(text, $"  two touching clips 0-1 and 1-2 seen from 1-1, 1-1.5, 0.5-1, -5-0, 2-9, -1-3: {string.Join(" ", spans)}");
        Expect(problems, string.Join(" ", spans) == "[] [2] [1] [] [] [1,2]", "a view shows the clips that lie in it, not those that only touch its edges");

        var probe = new TimelineClip { SourcePath = "D:/media/p.mp4", AudioStreamIndex = 0, End = 4, SourceDuration = 60 };
        var (probing, _) = Placed(FrameRate.Fps25, probe);
        var before = JsonSerializer.Serialize(probe);
        string changed;
        using (var tx = probing.BeginTransaction("Every field"))
        {
            tx.SlipClip(probe, 1);
            foreach (var property in typeof(TimelineClip).GetProperties().Where(p => p.CanWrite))
                property.SetValue(probe, Other(property.GetValue(probe), property.PropertyType));
            changed = JsonSerializer.Serialize(probe);
        }
        Expect(problems, changed != before && JsonSerializer.Serialize(probe) == before, "a rollback puts back every property of a clip (TimelineClip.Restore misses none)");
    }

    private static bool Refused(Action edit)
    {
        try
        {
            edit();
            return false;
        }
        catch (TimelineEditException)
        {
            return true;
        }
    }

    /// <summary>Another value of a property's type, for the rollback guard.</summary>
    private static object? Other(object? value, Type type) => value switch
    {
        double number => number + 1.25,
        bool flag => !flag,
        int whole => whole + 7,
        string words => words + "-other",
        Guid => Fixed(4242),
        LayerKind => LayerKind.Text,
        TransitionStyle => TransitionStyle.Zoom,
        ProxyChoice => ProxyChoice.Original,
        LayerState => new LayerState { Name = "other" },
        null when type == typeof(string) => "other",
        _ => value,
    };

    // ----- JSON -----

    private static void JsonRoundTrip(StringBuilder text, List<string> problems)
    {
        // The options project files are written with (camelCase, enums by name, nothing written that is at its default).
        var options = typeof(ProjectStore).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as JsonSerializerOptions;
        Expect(problems, options is not null, "ProjectStore's JSON options are found (ProjectStore.JsonOptions)");
        options ??= new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
        var pretty = new JsonSerializerOptions(options) { WriteIndented = true };

        var doc = Sample();
        Expect(problems, doc.FindProblems().Count == 0, $"the sample has no problems ({string.Join("; ", doc.FindProblems())})");

        var first = JsonSerializer.Serialize(doc, pretty);
        var back = JsonSerializer.Deserialize<TimelineDocument>(first, options)!;
        var second = JsonSerializer.Serialize(back, pretty);
        Expect(problems, first == second, "written, read and written again, the JSON is the same");
        problems.AddRange(ProjectTests.Compare(doc, back, "timeline").Select(d => "read back differently: " + d));
        Expect(problems, back.FindProblems().Count == 0, "read back, it has no problems");
        Expect(problems, back.FrameRate == FrameRate.Ntsc2997 && back.Tracks[0].Clips[1].State.Effects![0].Keys!["angle"][1].Easing == EasingType.Hold,
            "the rate and an effect key's easing come back");

        using (var tx = back.BeginTransaction("Split after reading"))
        {
            tx.SplitClipAt(back.Tracks[0].Clips[0], 5);
            tx.Commit();
        }
        Expect(problems, back.Tracks[0].Clips.Count == 3 && back.FindProblems().Count == 0, "a document read back can be edited");

        text.Append('\n').Append(first.ReplaceLineEndings("\n")).Append('\n');
    }

    /// <summary>A document with a little of everything, its ids fixed, its times on frames of 29.97.</summary>
    private static TimelineDocument Sample()
    {
        var rate = FrameRate.Ntsc2997;
        double At(long frame) => rate.Quantize(rate.SecondsFromFrame(frame));

        return new TimelineDocument
        {
            Width = 1080, Height = 1920, UseVerticalResolution = true, FrameRate = rate,
            SequenceEffects = [new EffectState { Type = "builtin.color.eq", Id = Fixed(90), Values = new() { ["contrast"] = JsonSerializer.SerializeToElement(1.2) } }],
            Markers = [new TimelineMarker(At(45), "Intro")],
            Tracks =
            [
                new TimelineTrackModel
                {
                    Id = Fixed(1), TrackNumber = 1, Kind = TimelineTrackKind.Video,
                    TrackLook = new LayerState { Name = "Main video", SizeWidth = 1, SizeHeight = 1, CornerRadius = 8 },
                    Clips =
                    [
                        new TimelineClip
                        {
                            Id = Fixed(11), SourcePath = "D:/media/main.mp4", AudioStreamIndex = 0, Start = 0, End = At(300),
                            MediaOffset = 2.5, SourceDuration = 600, FadeInSeconds = 0.5, FadeInStyle = TransitionStyle.WipeLeft,
                            State = new LayerState { Name = "Opening", KeysScale = [new(0, 1), new(2, 1.2, EasingType.Linear)] },
                        },
                        new TimelineClip
                        {
                            Id = Fixed(12), SourcePath = "D:/media/main.mp4", AudioStreamIndex = 0, Start = At(300), End = At(450),
                            MediaOffset = 20, Speed = 1.5, SourceDuration = 600, TransitionIn = 0.5, FadeOutSeconds = 1, FadeOutStyle = TransitionStyle.SlideUp,
                            AudioLeadInSeconds = At(15), ProxyChoice = ProxyChoice.Make,
                            State = new LayerState
                            {
                                Name = "Second", EffectsBypassed = true,
                                Effects = [new EffectState { Type = "builtin.look.vignette", Id = Fixed(91), On = false, Range = [1, 3], Keys = new() { ["angle"] = [new(0, 30), new(2, 60, EasingType.Hold)] } }],
                            },
                        },
                    ],
                },
                new TimelineTrackModel
                {
                    Id = Fixed(2), TrackNumber = 1, Kind = TimelineTrackKind.Audio, Name = "Music", GainDb = -3, DuckAgainstVoice = true, DuckAmountDb = -12,
                    AudioFilters = new TrackAudioFilters { Compressor = true, HighPass = true },
                    Clips = [new TimelineClip { Id = Fixed(21), ContentKind = LayerKind.Audio, SourcePath = "D:/media/music.wav", AudioStreamIndex = 0, End = At(900), SourceDuration = 240, GainDb = -6, FadeOutSeconds = 3, State = new LayerState { Name = "Music" } }],
                },
                new TimelineTrackModel
                {
                    Id = Fixed(3), TrackNumber = 1, Kind = TimelineTrackKind.Adjustment,
                    Clips = [new TimelineClip { Id = Fixed(31), ContentKind = LayerKind.Adjustment, Start = At(60), End = At(240), IsHidden = true, State = new LayerState { Name = "Grade", Kind = LayerKind.Adjustment } }],
                },
                new TimelineTrackModel
                {
                    Id = Fixed(4), TrackNumber = 1, Kind = TimelineTrackKind.Subtitle,
                    Clips = [new TimelineClip { Id = Fixed(41), ContentKind = LayerKind.Captions, End = At(900), State = new LayerState { Name = "Captions", Kind = LayerKind.Captions } }],
                },
            ],
        };
    }
}
