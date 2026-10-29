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
            "# Folds (4.3D, Split & Mark): stretches taken out of every track and kept at their seams, a fold rolled back, the file round trip, one put back and folded again as the same fold (4.4-BugHunt), then each put back.", Folds);
        yield return Case("timeline-fold-seam-trims",
            "# Fold-aware edge trims (4.4-BugHunt, Q11): a clip's edge at a fold's seam shows part of the fold (joined into the clip) or folds more of the clip, children in step, the fold's id kept; past the whole fold it is put back; refusals change nothing.", FoldSeamTrims);
        yield return Case("timeline-trim-unlinks-children",
            "# Children left wholly outside a parent's trim are unlinked and kept (4.4-BugHunt, Q3), on a track a ripple closes up they still go; a roll carries the children on its cut.", TrimUnlinksChildren);
        yield return Case("timeline-fuzz",
            "# A model fuzz (4.4-BugHunt): 10,000 seeded random edits, undos and redos on seven tracks with children, captions and folds: no throw but a refusal, a refusal changes nothing, no problem after an edit, Undo's copies untouched.", Fuzz);
        yield return Case("timeline-split-keys",
            "# A clip split between two keyframes (4.3D): each half keeps its own keys, a key on the cut with the value there, and its part of the curve, for every easing.", SplitKeys);
        yield return Case("timeline-children",
            "# Children (4.4): a talk's HUD cut-out on the track under it and a key on another track go with its splits, trims, moves, ripples, deletes and folds; refusals and rollbacks change nothing.", Children);
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

        // Put back, a stretch stays marked as the fold it was (4.4-BugHunt), and folded again it is that fold once more: the
        // document is as it was before it was put back.
        string Saved() => JsonSerializer.Serialize(doc, options);
        static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;
        string Stretch((double From, double To)? span) => span is { } s ? $"{N(s.From)}-{N(s.To)} s" : "none";
        var bothFolded = Saved();
        using (var tx = doc.BeginTransaction("Put the first back"))
        {
            tx.UnfoldSegment(first);
            tx.Commit();
        }
        Name(doc, names);
        Show(text, doc, names, problems, "Put the earlier fold back (4.4-BugHunt): the stretch stays marked, as the fold it was");
        var marked = doc.RestoredFolds is [var mark] ? mark : throw new InvalidOperationException("the stretch put back is not marked");
        var markedTrack = doc.Tracks.FirstOrDefault(t => t.Id == marked.TrackId)?.DisplayName ?? "?";
        var sameId = marked.Id == first.Id ? "kept" : "lost";
        Line(text, $"marked: {Stretch(marked.SpanIn(doc))} on {markedTrack}, {N(marked.Length)} s, every track {marked.AllTracks}, the fold's id {sameId}");
        Expect(problems, doc.Folds is [var left] && left.Id == second.Id && marked.Id == first.Id && marked.TrackId == v1.Id && marked.AllTracks
                         && marked.SpanIn(doc) is { } markedSpan && Same(markedSpan.From, 4) && Same(markedSpan.To, 6.4),
            "put back, the fold goes and its stretch, 4-6.4 s on the talk's track, stays marked with the fold's id");

        FoldedSegment again;
        using (var tx = doc.BeginTransaction("Hide it again"))
        {
            again = tx.FoldRestored(marked);
            tx.Commit();
        }
        var sameAgain = Saved() == bothFolded;
        Line(text, $"\nFolded again: the same fold {again.Id == first.Id}, the document as it was before it was put back {sameAgain}");
        Expect(problems, again.Id == first.Id && doc.RestoredFolds is null && sameAgain,
            "folded again, the stretch is the same fold, and the document is as it was before it was put back");

        // Put back again: written and read back, and copied, the mark is the same.
        using (var tx = doc.BeginTransaction("Put the first back again"))
        {
            tx.UnfoldSegment(first);
            tx.Commit();
        }
        var unfolded = Saved();
        var undoCopy = doc.Clone();
        var read = JsonSerializer.Deserialize<TimelineDocument>(unfolded, options)!;
        var readMark = read.RestoredFolds is [var readRecord] && readRecord == doc.RestoredFolds![0] && readRecord.SpanIn(read) is { } readSpan && Same(readSpan.From, 4) && Same(readSpan.To, 6.4);
        var (markWritten, writtenAgain) = (unfolded.Contains("\"restoredFolds\""), JsonSerializer.Serialize(read, options) == unfolded);
        Line(text, $"Written as a project writes it: marks written {markWritten}; read back the same {readMark}, and written again the same {writtenAgain}");
        Expect(problems, markWritten && readMark && writtenAgain && JsonSerializer.Serialize(undoCopy, options) == unfolded,
            "the mark goes into the file and comes back the same, and a copy of the document keeps it");

        // A split inside the stretch leaves it marked; its first part deleted, it is let go; the copy Undo took before both
        // still has it, and folds it again.
        var restoredTalk = doc.Tracks[0].Clips.First(c => Same(c.Start, 4));
        using (var tx = doc.BeginTransaction("Split the stretch put back"))
        {
            tx.SplitClipAt(restoredTalk, 5);
            tx.Commit();
        }
        var afterSplit = doc.RestoredFolds is [var stillMarked] ? stillMarked.SpanIn(doc) : null;
        using (var tx = doc.BeginTransaction("Delete its first part"))
        {
            tx.DeleteClip(restoredTalk);
            tx.Commit();
        }
        var letGo = doc.RestoredFolds is null;
        var inCopy = undoCopy.RestoredFolds is [var copyRecord] ? copyRecord.SpanIn(undoCopy) : null;
        var copyKept = JsonSerializer.Serialize(undoCopy, options) == unfolded;
        Line(text, $"Split at 5 s, the stretch still lies {Stretch(afterSplit)}; its first part deleted, it is let go {letGo}; the copy Undo took before them still marks {Stretch(inCopy)}, unchanged {copyKept}");
        Expect(problems, afterSplit is { } split && Same(split.From, 4) && Same(split.To, 6.4) && letGo && inCopy is { } copied && Same(copied.From, 4) && Same(copied.To, 6.4) && copyKept,
            "a split inside the stretch keeps it marked, deleting where it starts lets it go, and Undo's copy from before keeps it");

        doc = undoCopy;
        using (var tx = doc.BeginTransaction("Hide it once more, after Undo"))
        {
            tx.FoldRestored(doc.RestoredFolds![0]);
            tx.Commit();
        }
        Expect(problems, Saved() == bothFolded, "brought back by Undo, the stretch folds again into the document as it was before it was put back");

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

    // ----- Children (4.4) -----

    private static void Children(StringBuilder text, List<string> problems)
    {
        var options = (JsonSerializerOptions)typeof(ProjectStore).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        var names = new Dictionary<Guid, string>();
        void Step(string step)
        {
            Name(doc, names);
            Show(text, doc, names, problems, step);
            Links(text, doc, names);
        }
        string Saved() => JsonSerializer.Serialize(doc, options);
        static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;

        TimelineTrackModel v1, v2, v3, a1;
        TimelineClip talk, hud, key, music;
        using (var tx = doc.BeginTransaction("Set up"))
        {
            (v1, v2, v3, a1) = (tx.InsertTrack(0, TimelineTrackKind.Video), tx.InsertTrack(1, TimelineTrackKind.Video, "HUD"), tx.InsertTrack(2, TimelineTrackKind.Video), tx.InsertTrack(3, TimelineTrackKind.Audio));
            talk = tx.PlaceClip(v1, new TimelineClip { ContentKind = LayerKind.MainVideo, SourcePath = "D:/media/talk.mp4", AudioStreamIndex = 0, End = 20, SourceDuration = 30, State = new LayerState { Name = "Talk" } });
            hud = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Video, SourcePath = "D:/media/hud", End = 20, State = new LayerState { Name = "HUD" } });
            key = tx.PlaceClip(v3, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/key.png", Start = 6, End = 9, State = new LayerState { Name = "Key" } });
            music = tx.PlaceClip(a1, new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/music.wav", AudioStreamIndex = 0, End = 30, SourceDuration = 120, State = new LayerState { Name = "Music" } });
            tx.SetTrackParent(v2, v1.Id);
            tx.SetClipParent(hud, talk.Id);
            tx.SetClipParent(key, talk.Id);
            tx.Commit();
        }
        (names[talk.Id], names[hud.Id], names[key.Id], names[music.Id]) = ("T", "H", "K", "M");
        Step("Set up at 25 fps: the talk, its HUD cut-out on the HUD track under it, a key over it from 6 s on Video 3 (both its children), music under all");
        Expect(problems, v2.ParentTrackId == v1.Id && doc.ChildTracksOf(v1).SequenceEqual(new[] { v2 }) && doc.ChildrenOf(talk).Select(c => c.Clip).SequenceEqual(new[] { hud, key }),
            "the HUD track lies under the talk's, and the HUD and the key are the talk's children");

        TimelineClip talk2;
        using (var tx = doc.BeginTransaction("Split"))
        {
            talk2 = tx.SplitClipAt(talk, 8);
            tx.Commit();
        }
        var (hud2, key2) = (v2.Clips[1], v3.Clips[1]);
        Step("Split the talk at 8 s: its HUD and its key are cut there too, their second pieces the second talk's");
        Expect(problems, Same(hud.End, 8) && Same(hud2.Start, 8) && Same(hud2.End, 20) && Same(key.End, 8) && Same(key2.End, 9) && hud.ParentClipId == talk.Id
                         && key.ParentClipId == talk.Id && hud2.ParentClipId == talk2.Id && key2.ParentClipId == talk2.Id,
            "a split cuts the children across it, and their pieces after the cut go with its second half");

        using (var tx = doc.BeginTransaction("Trim the end"))
        {
            tx.TrimClipEnd(talk2, 18);
            tx.Commit();
        }
        Step("Trim the second talk's end to 18 s: its HUD's end, which was on it, follows; its key, inside it, stays");
        Expect(problems, Same(talk2.End, 18) && hud2.End == talk2.End && Same(key2.End, 9), "a trim takes the children on its edge along");

        using (var tx = doc.BeginTransaction("Trim the start"))
        {
            tx.TrimClipStart(talk2, 9.2);
            tx.Commit();
        }
        Step("Trim the second talk's start to 9.2 s: its HUD's start follows; its key, left wholly before it, stays where it is, unlinked (Q3)");
        Expect(problems, Same(talk2.Start, 9.2) && Same(talk2.MediaOffset, 9.2) && hud2.Start == talk2.Start && doc.TrackOf(key2) == v3 && key2.ParentClipId is null
                         && Same(key2.Start, 8) && Same(key2.End, 9) && v3.Clips.Count == 2,
            "a trim takes the children on its edge along, and leaves those it leaves wholly outside where they are, unlinked");

        using (var tx = doc.BeginTransaction("Move"))
        {
            tx.MoveClip(talk2, v1, 11.2);
            tx.Commit();
        }
        Step("Move the second talk to 11.2 s: its HUD moves with it, on its own track");
        Expect(problems, Same(talk2.Start, 11.2) && Same(hud2.Start, talk2.Start) && Same(hud2.End, talk2.End), "a move moves the children by as much");

        TimelineClip badge;
        using (var tx = doc.BeginTransaction("Badge"))
        {
            badge = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/badge.png", Start = 21, End = 22, State = new LayerState { Name = "Badge" } });
            tx.Commit();
        }
        names[badge.Id] = "B";
        var saved = Saved();
        bool refused;
        using (var tx = doc.BeginTransaction("Move into the badge"))
        {
            refused = Refused(() => tx.MoveClip(talk2, v1, 13));
            tx.Commit();
        }
        var outcome = refused ? "refused" : "moved";
        Line(text, $"\nA badge on the HUD track from 21 s; the second talk moved to 13 s, its HUD would run into it: {outcome}");
        Expect(problems, refused && Saved() == saved, "a move whose child has no room is refused whole, and changes nothing");

        using (var tx = doc.BeginTransaction("Ripple trim"))
        {
            tx.TrimClipEnd(talk, 6, ripple: true, rippleAllUnlockedTracks: false);
            tx.Commit();
        }
        Step("Ripple-trim the first talk's end to 6 s on its own track: the HUD track under it ripples too, the HUD's end rides with the talk's, and the key, left with nothing before 6 s on a track that does not ripple, stays, unlinked (Q3)");
        Expect(problems, Same(talk.End, 6) && Same(hud.End, 6) && doc.TrackOf(key) == v3 && key.ParentClipId is null && Same(key.Start, 6) && Same(key.End, 8)
                         && Same(talk2.Start, 9.2) && Same(hud2.Start, 9.2) && Same(badge.Start, 19) && Same(music.End, 30),
            "a ripple trim carries the children on its edge, leaves one it leaves with nothing on a track that does not ripple where it is, unlinked, and ripples the tracks under its own and no other");

        saved = Saved();
        using (var tx = doc.BeginTransaction("Ripple delete, not kept"))
        {
            var back = tx.RippleDelete(talk2, rippleAllUnlockedTracks: false);
            Line(text, $"\nRipple-deleting the second talk (not kept) takes its HUD too, and closes up its track and the HUD track by {N(back)} s: the badge would start at {N(badge.Start)} s");
            Expect(problems, doc.TrackOf(hud2) is null && Same(back, 8.8) && Same(badge.Start, 10.2), "a ripple delete takes the children, and closes up the tracks under its own");
        }
        Expect(problems, Saved() == saved, "rolled back, the ripple delete leaves everything as it was");

        using (var tx = doc.BeginTransaction("Delete alone, not kept"))
        {
            tx.DeleteClip(talk2, withChildren: false);
            Expect(problems, doc.TrackOf(talk2) is null && doc.TrackOf(hud2) is not null && hud2.ParentClipId == talk2.Id, "deleted alone, a clip leaves its children where they are");
        }
        using (var tx = doc.BeginTransaction("Delete"))
        {
            tx.DeleteClip(talk2);
            tx.Commit();
        }
        Step("Delete the second talk: its HUD goes with it");
        Expect(problems, doc.TrackOf(talk2) is null && doc.TrackOf(hud2) is null && v2.Clips.Count == 2 && Saved() != saved, "a delete takes the children");

        var before = Plays(doc);
        FoldedSegment fold;
        using (var tx = doc.BeginTransaction("Fold"))
        {
            fold = tx.FoldStretch(v1, 2, 3, rippleAllUnlockedTracks: false) ?? throw new InvalidOperationException("2-3 s was not folded");
            tx.Commit();
        }
        Step("Fold 2-3 s out of the talk's track alone: the HUD track under it folds with it");
        var keptTalk = fold.Clips.First(c => c.TrackId == v1.Id).Clip;
        var keptHud = fold.Clips.First(c => c.TrackId == v2.Id).Clip;
        var kept = string.Join(" ", fold.Clips.Select(c => $"[{doc.Tracks.First(t => t.Id == c.TrackId).DisplayName}: {c.Clip.State.Name} {N(c.Clip.Start)}-{N(c.Clip.End)}]"));
        var follows = keptHud.ParentClipId == keptTalk.Id ? "yes" : "no";
        Line(text, $"fold at {N(fold.SeamIn(doc) ?? -1)} s ({N(fold.Length)} s): {kept}; the HUD's piece goes with the talk's: {follows}");
        Expect(problems, fold.Clips.Count == 2 && keptHud.ParentClipId == keptTalk.Id && Same(talk.End, 2) && Same(hud.End, 2) && v2.Clips[1].ParentClipId == v1.Clips[1].Id
                         && Same(badge.Start, 18) && Same(music.End, 30), "a fold takes the children's pieces out with their parents', and closes up the tracks under its own");

        // The talk's piece in the fold has its id taken by a clip placed meanwhile: put back, it gets a new one, and the HUD's
        // piece names that.
        TimelineClip twin;
        using (var tx = doc.BeginTransaction("Twin"))
        {
            twin = tx.PlaceClip(v3, new TimelineClip { Id = keptTalk.Id, ContentKind = LayerKind.Image, SourcePath = "D:/media/twin.png", Start = 25, End = 26, State = new LayerState { Name = "Xtwin" } });
            tx.Commit();
        }
        names[twin.Id] = "X";
        using (var tx = doc.BeginTransaction("Unfold"))
        {
            tx.UnfoldSegment(fold);
            tx.Commit();
        }
        Step("Put the fold back after a clip on Video 3 took the talk piece's id: the piece comes back with a new id, and the HUD's piece goes with it");
        var restored = v1.Clips[1];
        Expect(problems, doc.Folds is null && restored.Id != keptTalk.Id && Same(restored.Start, 2) && v2.Clips[1].Id == keptHud.Id && v2.Clips[1].ParentClipId == restored.Id,
            "put back, a fold's children name their parents' new ids");
        using (var tx = doc.BeginTransaction("Take the twin off"))
        {
            tx.DeleteClip(twin);
            tx.Commit();
        }
        Expect(problems, Plays(doc) == before, "with the fold put back, every track plays what it played before it");

        saved = Saved();
        using (var tx = doc.BeginTransaction("Remove the talk's track, not kept"))
        {
            tx.RemoveTrack(v1);
            Expect(problems, v2.ParentTrackId is null && doc.Tracks.Contains(v2) && v2.Clips.Count == 4, "a track taken off leaves the tracks under it, under none");
        }
        Expect(problems, v2.ParentTrackId == v1.Id && Saved() == saved, "rolled back, the HUD track lies under the talk's again");

        var (third, thirdHud) = (v1.Clips[2], v2.Clips[2]);
        using (var tx = doc.BeginTransaction("Ripple the head, not kept"))
        {
            tx.TrimClipStart(third, 4, ripple: true, rippleAllUnlockedTracks: false);
            Line(text, $"\nRipple-trimming the head of {names[third.Id]} by 1 s (not kept): it plays from {N(third.MediaOffset)} s, {N(third.Start)}-{N(third.End)} s; its HUD lies {N(thirdHud.Start)}-{N(thirdHud.End)} s; the badge would start at {N(badge.Start)} s");
            Expect(problems, Same(third.Start, 3) && Same(third.End, 5) && Same(third.MediaOffset, 4) && Same(thirdHud.Start, 3) && Same(thirdHud.End, 5) && Same(badge.Start, 18),
                "a ripple trim of a head trims the child on it the same, its end coming back with the clip's, and ripples the tracks under its own");
        }
        Expect(problems, Saved() == saved, "rolled back, the ripple trim leaves everything as it was");

        using (var tx = doc.BeginTransaction("Split with the HUD locked, not kept"))
        {
            tx.SetTrack(v2, locked: true);
            tx.SplitClipAt(talk, 1);
            Expect(problems, Same(talk.End, 1) && Same(hud.End, 2) && hud.ParentClipId == talk.Id, "a child on a locked track is not cut, and stays with the first half");
        }
        Expect(problems, Saved() == saved, "rolled back, the split leaves everything as it was");

        using (var tx = doc.BeginTransaction("Links, not kept"))
        {
            bool[] guards =
            [
                Refused(() => tx.SetClipParent(talk, talk.Id)),
                Refused(() => tx.SetClipParent(talk, hud.Id)),
                Refused(() => tx.SetClipParent(hud, Guid.NewGuid())),
                Refused(() => tx.SetTrackParent(v1, v2.Id)),
                Refused(() => tx.SetTrackParent(v2, v2.Id)),
            ];
            tx.SetClipParent(hud, null);
            tx.SetTrackParent(v2, null);
            var said = string.Join(" ", guards.Select(g => g ? "refused" : "made"));
            Line(text, $"\nLinks asked for: a clip its own parent, the talk under its own HUD, a parent not on the timeline, the talk's track under the HUD track, a track under itself: {said}");
            Expect(problems, guards.All(g => g) && hud.ParentClipId is null && v2.ParentTrackId is null, "links that would loop are refused; unlinked, a clip and a track go with nothing");
        }
        Expect(problems, Saved() == saved && hud.ParentClipId == talk.Id && v2.ParentTrackId == v1.Id, "rolled back, the links are back");

        // Loops made by hand (a project file written by hand) are what FindProblems finds.
        var loops = doc.Clone();
        loops.Tracks[0].Clips[0].ParentClipId = loops.Tracks[1].Clips[0].Id;
        loops.Tracks[0].Clips[1].ParentClipId = loops.Tracks[0].Clips[1].Id;
        loops.Tracks[2].ParentTrackId = loops.Tracks[2].Id;
        var found = loops.FindProblems();
        var (own, back2, under) = (found.Count(p => p.Contains("its own parent")), found.Count(p => p.Contains("lead back")), found.Count(p => p.Contains("under itself")));
        Line(text, $"\nLinks made by hand: {own} clip its own parent, {back2} clips whose parents lead back to them, {under} track under itself");
        Expect(problems, found.Count == 4 && own == 1 && back2 == 2 && under == 1 && doc.FindProblems().Count == 0, "FindProblems finds a clip its own parent, a loop of two and a track under itself, and nothing in the document they were copied from");

        // Written as a project writes its timeline and read back, the links come back; without links, nothing of them is written.
        var json = Saved();
        var read = JsonSerializer.Deserialize<TimelineDocument>(json, options)!;
        var plain = doc.Clone();
        foreach (var track in plain.Tracks)
        {
            track.ParentTrackId = null;
            foreach (var clip in track.Clips)
                clip.ParentClipId = null;
        }
        var plainJson = JsonSerializer.Serialize(plain, options);
        var (written, again, none) = (json.Contains("\"parentClipId\"") && json.Contains("\"parentTrackId\""), JsonSerializer.Serialize(read, options) == json,
                                      !plainJson.Contains("\"parentClipId\"") && !plainJson.Contains("\"parentTrackId\""));
        Line(text, $"\nWritten as a project writes it: links written {written}; read back and written again, the same {again}; a copy without links writes none of them {none}");
        Expect(problems, written && again && none && read.FindClip(hud.Id)?.ParentClipId == talk.Id && read.Tracks[1].ParentTrackId == v1.Id, "the links go into the file and come back, and are left out while there are none");

        // A rollback puts back every property of a track, as the guard of the edge cases does for a clip.
        var trackBefore = JsonSerializer.Serialize(v3);
        string trackChanged;
        using (var tx = doc.BeginTransaction("Every track field"))
        {
            tx.SetTrack(v3, name: "Probe");
            foreach (var property in typeof(TimelineTrackModel).GetProperties().Where(p => p.CanWrite && p.Name != nameof(TimelineTrackModel.Clips)))
                property.SetValue(v3, Other(property.GetValue(v3), property.PropertyType));
            trackChanged = JsonSerializer.Serialize(v3);
        }
        Expect(problems, trackChanged != trackBefore && JsonSerializer.Serialize(v3) == trackBefore, "a rollback puts back every property of a track (TimelineTrackModel.RestoreFields misses none)");
    }

    /// <summary>Which clip goes with which (child&lt;parent) and which track lies under which, as one line.</summary>
    private static void Links(StringBuilder text, TimelineDocument doc, Dictionary<Guid, string> names)
    {
        var clips = doc.Tracks.SelectMany(t => t.Clips).Where(c => c.ParentClipId is not null)
            .Select(c => names.GetValueOrDefault(c.Id, "?") + "<" + names.GetValueOrDefault(c.ParentClipId!.Value, "?"));
        var tracks = doc.Tracks.Where(t => t.ParentTrackId is not null)
            .Select(t => t.DisplayName + " under " + (doc.Tracks.FirstOrDefault(p => p.Id == t.ParentTrackId)?.DisplayName ?? "?"));
        text.Append("  links: ").Append(string.Join(" ", clips)).Append("; ").Append(string.Join(", ", tracks)).Append('\n');
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

    /// <summary>The options project files are written with (camelCase, enums by name, nothing written that is at its default).</summary>
    private static JsonSerializerOptions ProjectOptions() =>
        (JsonSerializerOptions)typeof(ProjectStore).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    // ----- Fold-aware edge trims (4.4-BugHunt, Q11) -----

    private static void FoldSeamTrims(StringBuilder text, List<string> problems)
    {
        var options = ProjectOptions();
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        var names = new Dictionary<Guid, string>();
        string Saved() => JsonSerializer.Serialize(doc, options);
        static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;
        void Step(string step)
        {
            Name(doc, names);
            Show(text, doc, names, problems, step);
            Links(text, doc, names);
        }
        string Kept(FoldedSegment fold) => string.Join(" ", fold.Clips.Select(c =>
            $"[{doc.Tracks.FirstOrDefault(t => t.Id == c.TrackId)?.DisplayName ?? "?"}: {N(c.Clip.Start)}-{N(c.Clip.End)}{(c.Clip.PlaysFile ? " @" + N(c.Clip.MediaOffset) : "")}]"));

        TimelineTrackModel v1, v2, a1;
        TimelineClip talk, hud, music;
        using (var tx = doc.BeginTransaction("Set up"))
        {
            (v1, v2, a1) = (tx.InsertTrack(0, TimelineTrackKind.Video), tx.InsertTrack(1, TimelineTrackKind.Video, "HUD"), tx.InsertTrack(2, TimelineTrackKind.Audio));
            talk = tx.PlaceClip(v1, new TimelineClip { ContentKind = LayerKind.MainVideo, SourcePath = "D:/media/talk.mp4", AudioStreamIndex = 0, End = 20, SourceDuration = 30, State = new LayerState { Name = "Talk" } });
            hud = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Video, SourcePath = "D:/media/hud", End = 20, State = new LayerState { Name = "HUD" } });
            music = tx.PlaceClip(a1, new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/music.wav", AudioStreamIndex = 0, End = 30, SourceDuration = 120, State = new LayerState { Name = "Music" } });
            tx.SetTrackParent(v2, v1.Id);
            tx.SetClipParent(hud, talk.Id);
            tx.Commit();
        }
        (names[talk.Id], names[hud.Id], names[music.Id]) = ("T", "H", "M");
        var before = Plays(doc);

        FoldedSegment fold;
        using (var tx = doc.BeginTransaction("Fold"))
        {
            fold = tx.FoldStretch(v1, 6, 10, rippleAllUnlockedTracks: true) ?? throw new InvalidOperationException("6-10 s was not folded");
            tx.Commit();
        }
        Step("Set up at 25 fps (a talk, its HUD cut-out on the track under it, music under both) and fold 6-10 s out of every track");
        var (foldId, afterId, hudAfterId) = (fold.Id, fold.SeamClipId, v2.Clips[1].Id);
        Line(text, $"fold at {N(fold.SeamIn(doc) ?? -1)} s ({N(fold.Length)} s): {Kept(fold)}");

        FoldedSegment? Resize(string step, FoldSide side, double seconds)
        {
            FoldedSegment? resized;
            using (var tx = doc.BeginTransaction(step))
            {
                resized = tx.ResizeFold(fold, side, seconds);
                tx.Commit();
            }
            Step(step);
            if (resized is not null)
            {
                var id = resized.Id == foldId ? "kept" : "new";
                Line(text, $"fold at {N(resized.SeamIn(doc) ?? -1)} s ({N(resized.Length)} s), its id {id}: {Kept(resized)}");
                fold = resized;
            }
            return resized;
        }
        TimelineClip Clip(Guid id) => doc.FindClip(id) ?? throw new InvalidOperationException($"the clip {id} is gone");

        var shown = Resize("Drag the start of the talk after the seam 1 s left: the fold's last second shows, joined into it with its HUD's", FoldSide.Tail, 1);
        var (talkAfter, hudAfter) = (Clip(afterId), Clip(hudAfterId));
        Expect(problems, shown is { Length: 3 } && shown.Id == foldId && shown.SeamClipId == afterId && Same(talkAfter.Start, 6) && Same(talkAfter.End, 17)
                         && Same(talkAfter.MediaOffset, 9) && Same(hudAfter.Start, 6) && Same(hudAfter.End, 17) && hudAfter.ParentClipId == afterId
                         && v1.Clips.Count == 2 && v2.Clips.Count == 2 && a1.Clips.Count == 2 && Same(doc.TotalDuration, 27),
            "a tail shown joins the clip after the seam (its file running on), the HUD's piece joins its HUD, still its child; the fold keeps its id");

        shown = Resize("Drag the end of the talk before the seam 1 s right: the fold's first second shows, joined into it", FoldSide.Head, 1);
        (talkAfter, hudAfter) = (Clip(afterId), Clip(hudAfterId));
        Expect(problems, shown is { Length: 2 } && shown.Id == foldId && shown.SeamClipId == afterId && Same(talk.End, 7) && Same(hud.End, 7) && hud.ParentClipId == talk.Id
                         && Same(talkAfter.Start, 7) && Same(talkAfter.End, 18) && v1.Clips.Count == 2 && Same(doc.TotalDuration, 28),
            "a head shown joins the clip before the seam, and its HUD's piece its HUD");

        var objectBefore = Clip(afterId);
        var folded = Resize("Drag the start of the talk after the seam 0.6 s right: as much more of it folds, and it keeps its id, its HUD too", FoldSide.Tail, -0.6);
        (talkAfter, hudAfter) = (Clip(afterId), Clip(hudAfterId));
        Expect(problems, folded is { Length: 2.6 } && folded.Id == foldId && folded.SeamClipId == afterId && Same(talkAfter.Start, 7) && Same(talkAfter.End, 17.4)
                         && Same(talkAfter.MediaOffset, 9.6) && Same(hudAfter.Start, 7) && Same(hudAfter.End, 17.4) && hudAfter.ParentClipId == afterId && Same(doc.TotalDuration, 27.4),
            "folding more at the tail takes the head of the clip after the seam and its HUD's, which keep their ids");
        var another = !ReferenceEquals(objectBefore, talkAfter);
        Line(text, $"the clip after the seam is another object now, its id the same: {another}");

        folded = Resize("Drag the end of the talk before the seam 1 s left: its last second folds too", FoldSide.Head, -1);
        talkAfter = Clip(afterId);
        Expect(problems, folded is { Length: 3.6 } && folded.Id == foldId && Same(talk.End, 6) && Same(hud.End, 6) && Same(talkAfter.Start, 6) && Same(talkAfter.MediaOffset, 9.6)
                         && Same(doc.TotalDuration, 26.4),
            "folding more at the head takes the tail of the clip before the seam, which keeps its id");
        var foldedState = Saved();

        var whole = Resize("Drag the start of the talk after the seam 10 s left, past the whole fold: it is put back, and stays marked", FoldSide.Tail, 10);
        var mark = doc.RestoredFolds is [var only] ? only : null;
        Expect(problems, whole is null && doc.Folds is null && mark is not null && mark.Id == foldId && mark.SpanIn(doc) is { } span && Same(span.From, 6) && Same(span.To, 9.6)
                         && Plays(doc) == before && Same(doc.TotalDuration, 30),
            "shown past its length, the fold is put back whole (every track plays what it did before the fold) and its stretch stays marked");

        using (var tx = doc.BeginTransaction("Hide it again"))
        {
            fold = tx.FoldRestored(mark ?? throw new InvalidOperationException("the stretch put back is not marked"));
            tx.Commit();
        }
        Step("Hide the stretch again: the same fold, as it was before it was put back");
        Expect(problems, fold.Id == foldId && Saved() == foldedState, "the stretch put back folds again into the document as it was");

        var saved = Saved();
        using (var tx = doc.BeginTransaction("Locked HUD, not kept"))
        {
            tx.SetTrack(v2, locked: true);
            var inside = Saved();
            var refused = Refused(() => tx.ResizeFold(fold, FoldSide.Tail, 1));
            var outcome = refused ? "refused" : "made";
            Line(text, $"\nWith the HUD track locked, showing a second of the fold: {outcome}");
            Expect(problems, refused && Saved() == inside, "with a track the fold covers locked, opening it at an edge is refused and changes nothing");
        }
        using (var tx = doc.BeginTransaction("As much as there is, not kept"))
        {
            var most = tx.ResizeFold(fold, FoldSide.Head, -100);
            Line(text, $"Folding 100 s more of the talk before the seam: it keeps {N(talk.Duration)} s, the fold is {N(most?.Length ?? -1)} s");
            Expect(problems, most is not null && Same(talk.Duration, 0.08) && Same(most.Length, 9.52) && doc.FindProblems().Count == 0,
                "folding more than the clip has folds all but the shortest clip of it");
        }
        Expect(problems, Saved() == saved, "rolled back, both leave everything as it was");
    }

    // ----- Children left outside a trim are unlinked and kept (4.4-BugHunt, Q3) -----

    private static void TrimUnlinksChildren(StringBuilder text, List<string> problems)
    {
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        var names = new Dictionary<Guid, string>();
        static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;
        void Step(string step)
        {
            Name(doc, names);
            Show(text, doc, names, problems, step);
            Links(text, doc, names);
        }

        TimelineTrackModel v1, v2, v3, v4;
        TimelineClip talk, hud, key, badge;
        using (var tx = doc.BeginTransaction("Set up"))
        {
            (v1, v2, v3, v4) = (tx.InsertTrack(0, TimelineTrackKind.Video), tx.InsertTrack(1, TimelineTrackKind.Video, "HUD"), tx.InsertTrack(2, TimelineTrackKind.Video),
                                tx.InsertTrack(3, TimelineTrackKind.Video, "Flash"));
            talk = tx.PlaceClip(v1, new TimelineClip { ContentKind = LayerKind.MainVideo, SourcePath = "D:/media/talk.mp4", AudioStreamIndex = 0, End = 20, SourceDuration = 30, State = new LayerState { Name = "Talk" } });
            hud = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Video, SourcePath = "D:/media/hud", End = 20, State = new LayerState { Name = "HUD" } });
            badge = tx.PlaceClip(v3, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/badge.png", Start = 2, End = 3, State = new LayerState { Name = "Badge" } });
            key = tx.PlaceClip(v3, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/key.png", Start = 12, End = 14, State = new LayerState { Name = "Key" } });
            tx.SetTrackParent(v2, v1.Id);
            tx.SetTrackParent(v4, v1.Id);
            foreach (var child in new[] { hud, badge, key })
                tx.SetClipParent(child, talk.Id);
            tx.Commit();
        }
        (names[talk.Id], names[hud.Id], names[badge.Id], names[key.Id]) = ("T", "H", "B", "K");
        Step("Set up at 25 fps: a talk, its HUD on the track under it, a badge at 2-3 s and a key at 12-14 s on Video 3, all three its children");

        using (var tx = doc.BeginTransaction("Trim the end"))
        {
            tx.TrimClipEnd(talk, 10);
            tx.Commit();
        }
        Step("Trim the talk's end to 10 s: its HUD follows; the key, left wholly after it, stays where it is, unlinked");
        Expect(problems, Same(talk.End, 10) && Same(hud.End, 10) && hud.ParentClipId == talk.Id && doc.TrackOf(key) == v3 && key.ParentClipId is null
                         && Same(key.Start, 12) && Same(key.End, 14) && badge.ParentClipId == talk.Id,
            "a child left wholly after a trimmed end is kept where it was, a clip of its own");

        using (var tx = doc.BeginTransaction("Trim the start"))
        {
            tx.TrimClipStart(talk, 4);
            tx.Commit();
        }
        Step("Trim the talk's start to 4 s: its HUD follows; the badge, left wholly before it, stays where it is, unlinked");
        Expect(problems, Same(talk.Start, 4) && Same(talk.MediaOffset, 4) && Same(hud.Start, 4) && doc.TrackOf(badge) == v3 && badge.ParentClipId is null
                         && Same(badge.Start, 2) && Same(badge.End, 3) && v3.Clips.Count == 2,
            "a child left wholly before a trimmed start is kept where it was, a clip of its own");

        TimelineClip key2, flash;
        using (var tx = doc.BeginTransaction("Two more children"))
        {
            key2 = tx.PlaceClip(v3, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/key.png", Start = 8, End = 10, State = new LayerState { Name = "Key" } });
            flash = tx.PlaceClip(v4, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/flash.png", Start = 8.4, End = 10, State = new LayerState { Name = "Flash" } });
            tx.SetClipParent(key2, talk.Id);
            tx.SetClipParent(flash, talk.Id);
            tx.Commit();
        }
        Name(doc, names);
        using (var tx = doc.BeginTransaction("Ripple trim"))
        {
            tx.TrimClipEnd(talk, 7, ripple: true, rippleAllUnlockedTracks: false);
            tx.Commit();
        }
        Step("A second key (8-10 s, Video 3) and a flash (8.4-10 s, on the Flash track under the talk's) as its children; ripple-trim the talk's end to 7 s on its own track: the key, left with nothing on a track that does not ripple, stays, unlinked; the flash, on a track that closes up, goes");
        Expect(problems, Same(talk.End, 7) && Same(hud.End, 7) && doc.TrackOf(key2) == v3 && key2.ParentClipId is null && Same(key2.Start, 8) && Same(key2.End, 10)
                         && doc.TrackOf(flash) is null && doc.TrackOf(key) == v3,
            "a ripple trim keeps a child it leaves with nothing on a track it does not ripple, unlinked, and takes one on a track it closes up");

        TimelineClip talk2, roller;
        using (var tx = doc.BeginTransaction("Split, and a key on the cut"))
        {
            talk2 = tx.SplitClipAt(talk, 5.6);
            roller = tx.PlaceClip(v3, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/roll.png", Start = 5, End = 5.6, State = new LayerState { Name = "Roll key" } });
            tx.SetClipParent(roller, talk.Id);
            tx.Commit();
        }
        var hud2 = v2.Clips[1];
        Step("Split the talk at 5.6 s (its HUD with it), and a key of the first half from 5 s to the cut");

        double rolled;
        using (var tx = doc.BeginTransaction("Roll later"))
        {
            rolled = tx.RollCut(talk, talk2, 6.4);
            tx.Commit();
        }
        Step("Roll the cut to 6.4 s: the HUD's cut rolls with it, and the key on the cut follows the first half's end");
        Expect(problems, Same(rolled, 0.8) && Same(talk.End, 6.4) && Same(talk2.Start, 6.4) && Same(talk2.MediaOffset, 6.4) && Same(hud.End, 6.4) && Same(hud2.Start, 6.4)
                         && hud2.ParentClipId == talk2.Id && Same(roller.End, 6.4) && roller.ParentClipId == talk.Id,
            "a roll later carries the children on its cut: the HUD's halves meet on the new cut");

        using (var tx = doc.BeginTransaction("Roll earlier"))
        {
            rolled = tx.RollCut(talk, talk2, 4.6);
            tx.Commit();
        }
        Step("Roll the cut back to 4.6 s: the HUD's cut rolls back with it; the key, left wholly after the first half, stays, unlinked");
        Expect(problems, Same(rolled, -1.8) && Same(talk.End, 4.6) && Same(talk2.Start, 4.6) && Same(talk2.MediaOffset, 4.6) && Same(hud.End, 4.6) && Same(hud2.Start, 4.6)
                         && hud2.ParentClipId == talk2.Id && roller.ParentClipId is null && Same(roller.Start, 5) && Same(roller.End, 6.4),
            "a roll earlier carries the children on its cut, and leaves one it leaves wholly outside where it is, unlinked");
    }

    // ----- A model fuzz (4.4-BugHunt) -----

    private const int FuzzSeeds = 4, FuzzSteps = 2500;

    /// <summary>
    /// Ten thousand random edits, undos and redos (a fixed sequence of them, from four seeds) on a document of seven tracks
    /// with children, a captions' lane under the main track, keyframes and folds, each edit in a transaction of its own: one
    /// refused must leave the document as it was (to the byte of its JSON), one made must leave no problem (FindProblems),
    /// no fold id twice, and the copy Undo took before it as it was; every undo and redo must give back the document it
    /// kept. The golden text is how many of each verb were made and refused, and the counts of what went wrong (all 0).
    /// </summary>
    private static void Fuzz(StringBuilder text, List<string> problems)
    {
        var options = ProjectOptions();
        string[] verbs = ["split", "move", "move to a track", "trim start", "trim end", "ripple trim", "roll", "slip", "ripple delete", "delete",
                          "delete alone", "fold", "unfold", "fold again", "resize fold", "set parent", "place", "lock", "undo", "redo"];
        var (made, refused) = (new int[verbs.Length], new int[verbs.Length]);
        var (thrown, broken, changedByRefusal, changedCopies, operations) = (0, 0, 0, 0, 0);
        void Report(string what)
        {
            if (problems.Count < 40)
                problems.Add(what);
        }

        for (var seed = 1; seed <= FuzzSeeds; seed++)
        {
            var state = 0x9E3779B97F4A7C15UL * (ulong)seed;
            double U() => Next(ref state);
            var doc = FuzzDocument();
            var (undo, redo) = (new List<(TimelineDocument Doc, string Json)>(), new List<(TimelineDocument Doc, string Json)>());
            for (var step = 0; step < FuzzSteps; step++, operations++)
            {
                var json = JsonSerializer.Serialize(doc, options);
                var clips = doc.Tracks.SelectMany(t => t.Clips.Select(c => (Track: t, Clip: c))).ToList();
                var pairs = new List<(TimelineClip Earlier, TimelineClip Later)>();
                foreach (var track in doc.Tracks)
                {
                    for (var i = 1; i < track.Clips.Count; i++)
                    {
                        if (Math.Abs(track.Clips[i - 1].End - track.Clips[i].Start) <= 1e-6)
                            pairs.Add((track.Clips[i - 1], track.Clips[i]));
                    }
                }

                var verb = Choose(FuzzWeights(verbs, clips.Count, pairs.Count, doc, undo.Count, redo.Count), U());
                var where = string.Create(CultureInfo.InvariantCulture, $"fuzz seed {seed}, step {step} ({verbs[verb]})");
                if (verbs[verb] is "undo" or "redo")
                {
                    // As the view model's Undo does it: the copy kept is put in, and a copy of what was there kept the other way.
                    var (from, to) = verbs[verb] == "undo" ? (undo, redo) : (redo, undo);
                    var (kept, keptJson) = from[^1];
                    from.RemoveAt(from.Count - 1);
                    if (JsonSerializer.Serialize(kept, options) != keptJson)
                    {
                        changedCopies++;
                        Report($"{where}: the copy kept to go back to was changed by the edits after it");
                    }

                    to.Add((doc.Clone(), json));
                    doc = kept;
                    made[verb]++;
                    if (doc.FindProblems() is { Count: > 0 } wrong)
                    {
                        broken++;
                        Report($"{where}: {wrong[0]}");
                    }
                    continue;
                }

                var edit = FuzzEdit(verbs[verb], doc, clips, pairs, U, what => Report($"{where}: {what}"));
                var before = doc.Clone();
                bool touched;
                try
                {
                    using var tx = doc.BeginTransaction(verbs[verb]);
                    edit(tx);
                    tx.Commit();
                    touched = tx.TouchedClipIds.Count > 0 || tx.TouchedTrackIds.Count > 0;
                    made[verb]++;
                }
                catch (TimelineEditException)
                {
                    refused[verb]++;
                    if (JsonSerializer.Serialize(doc, options) != json)
                    {
                        changedByRefusal++;
                        Report($"{where}: refused, but the document changed");
                    }
                    continue;
                }
                catch (Exception ex)
                {
                    thrown++;
                    Report($"{where}: threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    continue;
                }

                if (doc.FindProblems() is { Count: > 0 } found)
                {
                    broken++;
                    Report($"{where}: {found[0]}");
                }
                var foldIds = (doc.Folds ?? []).Select(f => f.Id).Concat((doc.RestoredFolds ?? []).Select(r => r.Id)).ToList();
                if (foldIds.Count != foldIds.Distinct().Count())
                {
                    broken++;
                    Report($"{where}: a fold's id is there twice (among the folds and the stretches put back)");
                }
                if (!touched)
                    continue;

                // The copy Undo took before the edit is packed after it (as the view model does): the edit must not have changed it.
                if (JsonSerializer.Serialize(before, options) != json)
                {
                    changedCopies++;
                    Report($"{where}: the copy Undo took before the edit was changed by it");
                }
                undo.Add((before, json));
                if (undo.Count > 20)
                    undo.RemoveAt(0);
                redo.Clear();
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"\n{FuzzSeeds} seeds of {FuzzSteps} steps: {operations} operations\n");
        for (var v = 0; v < verbs.Length; v++)
            Line(text, $"  {verbs[v]}: {made[v]} made, {refused[v]} refused");
        Line(text, $"  threw something other than a refusal: {thrown}; problems after an edit, an undo or a redo: {broken}; refusals that changed the document: {changedByRefusal}; copies kept for Undo or Redo that changed: {changedCopies}");
        Expect(problems, thrown == 0 && broken == 0 && changedByRefusal == 0 && changedCopies == 0,
            "the fuzz: nothing thrown but refusals, no problem after any edit, no refusal that changed anything, no copy for Undo changed");
    }

    /// <summary>The fuzz's document: a talk with keyframes, its HUD on the track under it, keys over it, B-roll, music, a voice in two, the captions' lane.</summary>
    private static TimelineDocument FuzzDocument()
    {
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        using var tx = doc.BeginTransaction("Set up");
        var (v1, v2, v3, v4) = (tx.InsertTrack(0, TimelineTrackKind.Video), tx.InsertTrack(1, TimelineTrackKind.Video, "HUD"), tx.InsertTrack(2, TimelineTrackKind.Video), tx.InsertTrack(3, TimelineTrackKind.Video));
        var (a1, a2, s1) = (tx.InsertTrack(4, TimelineTrackKind.Audio), tx.InsertTrack(5, TimelineTrackKind.Audio), tx.InsertTrack(6, TimelineTrackKind.Subtitle));
        var talk = tx.PlaceClip(v1, new TimelineClip
        {
            ContentKind = LayerKind.MainVideo, SourcePath = "D:/media/talk.mp4", AudioStreamIndex = 0, End = 40, SourceDuration = 90,
            State = new LayerState { Name = "Talk", KeysOpacity = [new(2, 100), new(20, 40), new(35, 80, EasingType.Linear)] },
        });
        var hud = tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Video, SourcePath = "D:/media/hud", End = 40, State = new LayerState { Name = "HUD" } });
        TimelineClip[] keys =
        [
            tx.PlaceClip(v3, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/key.png", Start = 5, End = 8, State = new LayerState { Name = "Key", KeysX = [new(0, 0.2), new(2, 0.6)] } }),
            tx.PlaceClip(v3, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/key.png", Start = 15, End = 18, State = new LayerState { Name = "Key" } }),
        ];
        tx.PlaceClip(v3, new TimelineClip { ContentKind = LayerKind.Text, Start = 25, End = 30, State = new LayerState { Name = "Title" } });
        tx.PlaceClip(v4, new TimelineClip { SourcePath = "D:/media/b-roll.mp4", AudioStreamIndex = 0, Start = 10, End = 20, MediaOffset = 5, SourceDuration = 60, State = new LayerState { Name = "B-roll" } });
        tx.PlaceClip(v4, new TimelineClip { SourcePath = "D:/media/b-roll.mp4", AudioStreamIndex = 0, Start = 22, End = 26, Speed = 1.5, SourceDuration = 60, FadeInSeconds = 0.5, State = new LayerState { Name = "Fast B-roll" } });
        tx.PlaceClip(a1, new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/music.wav", AudioStreamIndex = 0, End = 60, SourceDuration = 200, State = new LayerState { Name = "Music" } });
        tx.PlaceClip(a2, new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/voice.wav", AudioStreamIndex = 0, Start = 3, End = 12, SourceDuration = 30, State = new LayerState { Name = "Voice" } });
        tx.PlaceClip(a2, new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/voice.wav", AudioStreamIndex = 0, Start = 12, End = 22, MediaOffset = 9, SourceDuration = 30, TransitionIn = 0.4, State = new LayerState { Name = "Voice" } });
        tx.PlaceClip(s1, new TimelineClip { ContentKind = LayerKind.Captions, End = 40, State = new LayerState { Name = "Captions", Kind = LayerKind.Captions } });
        tx.SetTrackParent(v2, v1.Id);
        tx.SetTrackParent(s1, v1.Id);
        tx.SetClipParent(hud, talk.Id);
        foreach (var key in keys)
            tx.SetClipParent(key, talk.Id);
        tx.Commit();
        return doc;
    }

    /// <summary>How likely each verb of the fuzz is now: none that has nothing to work on, splits and placings while the clips are few, deletes when they are many.</summary>
    private static double[] FuzzWeights(string[] verbs, int clips, int pairs, TimelineDocument doc, int undos, int redos) =>
        [.. verbs.Select(verb => (double)(verb switch
        {
            "split" => clips is > 0 and < 50 ? 10 : 0,
            "move" => clips > 0 ? 8 : 0,
            "move to a track" => clips > 0 ? 4 : 0,
            "trim start" or "trim end" => clips > 0 ? 6 : 0,
            "ripple trim" => clips > 0 ? 5 : 0,
            "roll" => pairs > 0 ? 5 : 0,
            "slip" => clips > 0 ? 3 : 0,
            "ripple delete" or "delete" => clips > 0 ? clips > 35 ? 6 : 2 : 0,
            "delete alone" => clips > 0 ? 2 : 0,
            "fold" => 6,
            "unfold" => doc.Folds is { Count: > 0 } ? 6 : 0,
            "fold again" => doc.RestoredFolds is { Count: > 0 } ? 6 : 0,
            "resize fold" => doc.Folds is { Count: > 0 } ? 10 : 0,
            "set parent" => clips > 1 ? 3 : 0,
            "place" => clips < 50 ? clips < 20 ? 12 : 4 : 0,
            "lock" => 1,
            "undo" => undos > 0 ? 5 : 0,
            _ => redos > 0 ? 3 : 0,
        }))];

    /// <summary>The verb a number from 0 to 1 lands on, each taking its weight's share.</summary>
    private static int Choose(double[] weights, double u)
    {
        var at = u * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            if (at < weights[i])
                return i;
            at -= weights[i];
        }
        return Array.FindLastIndex(weights, w => w > 0);
    }

    /// <summary>
    /// One random edit of the fuzz, its clip, times and switches drawn now (from <paramref name="u"/>, so that the run is the
    /// same every time), made when it is given the transaction. <paramref name="fault"/> hears of a result that is wrong
    /// though not refused.
    /// </summary>
    private static Action<ITimelineTransaction> FuzzEdit(string verb, TimelineDocument doc, List<(TimelineTrackModel Track, TimelineClip Clip)> clips,
                                                         List<(TimelineClip Earlier, TimelineClip Later)> pairs, Func<double> u, Action<string> fault)
    {
        int Pick(int count) => Math.Min((int)(u() * count), count - 1);
        (TimelineTrackModel Track, TimelineClip Clip) Any() => clips[Pick(clips.Count)];
        var (a, b, c) = (u(), u(), u());
        switch (verb)
        {
            case "split":
            {
                var (_, clip) = Any();
                return tx => tx.SplitClipAt(clip, clip.Start + a * clip.Duration);
            }
            case "move":
            {
                var (track, clip) = Any();
                return tx => tx.MoveClip(clip, track, clip.Start + (b - 0.5) * 8, ripple: c < 0.2);
            }
            case "move to a track":
            {
                var (_, clip) = Any();
                var targets = doc.Tracks.Where(t => t.Accepts(clip)).ToList();
                var target = targets[Pick(targets.Count)];
                var insert = c < 0.1 && doc.Tracks.Count < 12 ? Pick(doc.Tracks.Count + 1) : -1;
                return tx => tx.MoveClip(clip, target, clip.Start + (b - 0.5) * 4, ripple: a < 0.15, insertTrackAtIndex: insert);
            }
            case "trim start":
            {
                var (_, clip) = Any();
                return tx => tx.TrimClipStart(clip, clip.Start + (a - 0.5) * 4);
            }
            case "trim end":
            {
                var (_, clip) = Any();
                return tx => tx.TrimClipEnd(clip, clip.End + (a - 0.5) * 4);
            }
            case "ripple trim":
            {
                var (_, clip) = Any();
                if (b < 0.5)
                    return tx => tx.TrimClipStart(clip, clip.Start + (a - 0.5) * 4, ripple: true, rippleAllUnlockedTracks: c < 0.5);
                return tx => tx.TrimClipEnd(clip, clip.End + (a - 0.5) * 4, ripple: true, rippleAllUnlockedTracks: c < 0.5);
            }
            case "roll":
            {
                var (earlier, later) = pairs[Pick(pairs.Count)];
                return tx => tx.RollCut(earlier, later, later.Start + (a - 0.5) * 3);
            }
            case "slip":
            {
                var (_, clip) = Any();
                return tx => tx.SlipClip(clip, (a - 0.5) * 4);
            }
            case "ripple delete":
            {
                var (_, clip) = Any();
                return tx => tx.RippleDelete(clip, rippleAllUnlockedTracks: a < 0.5);
            }
            case "delete":
            case "delete alone":
            {
                var (_, clip) = Any();
                var withChildren = verb == "delete";
                return tx => tx.DeleteClip(clip, withChildren);
            }
            case "fold":
            {
                var track = doc.Tracks[Pick(doc.Tracks.Count)];
                var from = a * Math.Max(doc.TotalDuration, 1);
                return tx => tx.FoldStretch(track, from, from + 0.1 + b * 3, rippleAllUnlockedTracks: c < 0.5);
            }
            case "unfold":
            {
                var fold = doc.Folds![Pick(doc.Folds.Count)];
                return tx => tx.UnfoldSegment(fold);
            }
            case "fold again":
            {
                var restored = doc.RestoredFolds![Pick(doc.RestoredFolds.Count)];
                return tx =>
                {
                    if (tx.FoldRestored(restored).Id != restored.Id)
                        fault($"the stretch {restored.Id} was folded again as another fold");
                };
            }
            case "resize fold":
            {
                var fold = doc.Folds![Pick(doc.Folds.Count)];
                var side = a < 0.5 ? FoldSide.Head : FoldSide.Tail;
                var seconds = (b - 0.45) * 2 * (fold.Length + 0.5);
                return tx =>
                {
                    if (tx.ResizeFold(fold, side, seconds) is { } resized && resized.Id != fold.Id)
                        fault($"the fold {fold.Id} came back from a resize as {resized.Id}");
                };
            }
            case "set parent":
            {
                var (_, clip) = Any();
                Guid? parent = a < 0.3 ? null : Any().Clip.Id;
                return tx => tx.SetClipParent(clip, parent);
            }
            case "place":
            {
                var track = doc.Tracks[Pick(doc.Tracks.Count)];
                var start = a * (doc.TotalDuration + 4);
                var placed = track.Kind switch
                {
                    TimelineTrackKind.Audio => new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = "D:/media/sfx.wav", AudioStreamIndex = 0, MediaOffset = b * 5, SourceDuration = 20 },
                    TimelineTrackKind.Subtitle => new TimelineClip { ContentKind = LayerKind.Captions },
                    TimelineTrackKind.Adjustment => new TimelineClip { ContentKind = LayerKind.Adjustment },
                    _ when b < 0.5 => new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/still.png" },
                    _ => new TimelineClip { ContentKind = LayerKind.VideoFile, SourcePath = "D:/media/clip.mp4", AudioStreamIndex = 0, MediaOffset = b * 20, SourceDuration = 60 },
                };
                (placed.Start, placed.End, placed.State) = (start, start + 0.3 + c * 4, new LayerState { Name = "Placed" });
                return tx => tx.PlaceClip(track, placed, allowCrossfadeOverlap: c < 0.3);
            }
            default:
            {
                // Locks (or unlocks) a track; with two locked, one of them is unlocked again.
                var locked = doc.Tracks.Where(t => t.IsLocked).ToList();
                var track = locked.Count >= 2 ? locked[Pick(locked.Count)] : doc.Tracks[Pick(doc.Tracks.Count)];
                return tx => tx.SetTrack(track, locked: !track.IsLocked);
            }
        }
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
        null when type == typeof(Guid?) => Fixed(4243),
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
