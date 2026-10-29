using System.IO;
using System.Globalization;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Captions (4.4) on their own: the word ends whisper's output gives (no word left up through a silence), anchored captions
/// projected through clip windows as cuts, folds, speeds and J cuts place them, the Subtitle Editor's edits written back, and
/// Remove Dead Air's gaps between captioned words. All pure functions; each case writes down what it did and checks itself.
/// </summary>
public static class CaptionTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("captions-word-end-gaps",
            "# Word ends (4.4): silence skipping's gap rule, a segment's last word held to its length, captions split at 0.35 s gaps, and the caption file's ends.", WordEnds);
        yield return Case("captions-projection-folds",
            "# Anchored captions (4.4): projected through clip windows, a fold taken out and put back, speed, a J cut, a duplicate, and edits written back.", Projection);
        yield return Case("deadair-caption-gaps",
            "# Remove Dead Air by the captions (4.4): the gaps between words, the minimum gap, and the pads before and after each word.", DeadAirGaps);
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

    private static void Words(StringBuilder text, string title, IEnumerable<CaptionWord> words)
    {
        Line(text, $"{title}:");
        foreach (var word in words)
            Line(text, $"  {word.Text} {N(word.Start)}-{N(word.End)}");
    }

    private static void Cues(StringBuilder text, string title, IEnumerable<SubtitleCueState> cues)
    {
        Line(text, $"{title}:");
        foreach (var cue in cues)
            Line(text, $"  {N(cue.Start)}-{N(cue.End)} \"{cue.Text}\" breaks [{string.Join(", ", (cue.Breaks ?? []).Select(N))}]");
    }

    // ----- Word ends -----

    private static void WordEnds(StringBuilder text, List<string> problems)
    {
        // Two segments: "Hello there." then, after a pause the detector skipped, "Welcome back friends". Whisper's clock
        // with the silence skipped: speech 1.0-3.0 of the audio is 0-2 on it, speech 6.0-9.0 is 2.1-5.1 (a 0.1 s seam).
        const string json = """
            {"transcription":[
              {"tokens":[
                {"text":" Hello","offsets":{"from":0,"to":600}},
                {"text":" there","offsets":{"from":700,"to":1300}},
                {"text":".","offsets":{"from":1300,"to":2050}}]},
              {"tokens":[
                {"text":"[_BEG_]","offsets":{"from":2100,"to":2100}},
                {"text":" Welcome","offsets":{"from":2150,"to":2700}},
                {"text":" back","offsets":{"from":2750,"to":3100}},
                {"text":" friends","offsets":{"from":3150,"to":5100}}]}
            ]}
            """;
        var map = CaptionGenerator.SpeechWordMap([(1.0, 3.0, 0.0, 2.0), (6.0, 9.0, 2.1, 5.1)]);
        var words = CaptionGenerator.ParseWhisperJson(json, map);
        Words(text, "words heard (in the audio's time)", words);

        // "there." ends on the seam (2.05 on whisper's clock): the end of the first stretch, never the start of the next.
        var there = words.First(w => w.Text.StartsWith("there", StringComparison.Ordinal));
        Expect(problems, there.End <= 3.0 + 1e-9, "a word ending in the gap between two stretches ends where the earlier one ends");
        var friends = words.First(w => w.Text == "friends");
        Expect(problems, friends.End - friends.Start <= CaptionGenerator.LongestLastWord("friends") + 1e-9,
            "a segment's last word lasts no longer than its length allows");

        var plain = CaptionGenerator.ParseWhisperJson(json);
        Words(text, "words without silence skipping", plain);

        var style = new CaptionStyle { MaxWordsPerLine = 5 };
        var gapped = new List<CaptionWord>
        {
            new("one", 0.0, 0.3), new("two", 0.4, 0.7), new("three", 1.2, 1.5), new("four", 1.6, 1.9), new("five", 2.0, 2.2),
        };
        var lines = CaptionGenerator.Lines(gapped, style);
        Line(text, $"captions from words with a 0.5 s gap: {string.Join(" | ", lines.Select(l => string.Join(" ", l.Select(w => w.Text))))}");
        Expect(problems, lines.Count == 2, "a gap over 0.35 s starts a new caption");

        // The caption file: the first caption ends with its last word (the gap is too long to bridge).
        var path = Path.Combine(Path.GetTempPath(), $"handpeg-golden-captions-{Guid.NewGuid():N}.ass");
        try
        {
            CaptionGenerator.WriteAss(gapped, new CaptionStyle { MaxWordsPerLine = 5, Animation = CaptionStyle.KaraokeSweep }, 1920, 216, 1, path);
            var events = File.ReadAllLines(path).Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)).ToList();
            foreach (var line in events)
                Line(text, $"  {line}");
            Expect(problems, events.Count == 2 && events[0].Contains("0:00:00.70", StringComparison.Ordinal),
                "a caption followed by a silence ends when its last word ends");
        }
        finally
        {
            File.Delete(path);
        }

        // The joined clock of two stretches, as BuildExtractCommand writes them, back to the source's time.
        List<(double Start, double End)> ranges = [(10, 12), (20, 23)];
        Line(text, $"joined 0.5 -> {N(CaptionGenerator.JoinedToSource(ranges, 0.5))}, 2.5 -> {N(CaptionGenerator.JoinedToSource(ranges, 2.5))}, 9 -> {N(CaptionGenerator.JoinedToSource(ranges, 9))}");
        Expect(problems, Math.Abs(CaptionGenerator.JoinedToSource(ranges, 2.5) - 20.5) < 1e-9, "the joined clock maps into the second stretch");
        var across = CaptionGenerator.JoinedToSource(ranges, [new CaptionWord("across", 1.8, 2.4)]);
        Words(text, "a word heard across the joint", across);
        Expect(problems, across[0].End <= 12 + 1e-9, "a word ends at the latest where its stretch ends");
    }

    // ----- Projection -----

    private static void Projection(StringBuilder text, List<string> problems)
    {
        // Captions heard at 1-2, 4.2-5.5 and 7-8 of a source.
        List<CaptionWord> heard =
        [
            new("Hi", 1.0, 1.3), new("all.", 1.4, 2.0),
            new("This", 4.2, 4.5), new("is", 4.6, 4.8), new("folded", 5.1, 5.5),
            new("The", 7.0, 7.3), new("end.", 7.4, 8.0),
        ];
        var anchors = CaptionProjection.Anchor(CaptionGenerator.Lines(heard, new CaptionStyle { MaxWordsPerLine = 5 }));
        Cues(text, "anchors (the source's time)", anchors);

        // One clip of the whole source at 2 s on the timeline.
        CaptionSpan[] whole = [new(2, 12, 0, 1)];
        var shown = CaptionProjection.Project(anchors, whole);
        Cues(text, "one clip at 2 s", shown);
        Expect(problems, shown.Count == anchors.Count && Math.Abs(shown[0].Start - 3) < 1e-9, "a clip at 2 s shows each caption 2 s later");

        // The stretch 5.0-6.0 folded out: the clip split in two, what follows closed up.
        CaptionSpan[] folded = [new(2, 7, 0, 1), new(7, 11, 6, 1)];
        var afterFold = CaptionProjection.Project(anchors, folded);
        Cues(text, "with 5.0-6.0 of the source folded out", afterFold);
        Expect(problems, afterFold.All(c => !c.Text.Contains("folded", StringComparison.Ordinal)), "a word in the folded stretch is not shown");
        Expect(problems, afterFold.Any(c => c.Text == "The end." && Math.Abs(c.Start - 8) < 1e-9), "what follows the fold closes up with it");

        // Put back: the same anchors show everything again.
        var unfolded = CaptionProjection.Project(anchors, whole);
        Expect(problems, unfolded.Select(c => c.Text).SequenceEqual(shown.Select(c => c.Text)), "putting the fold back brings its words back");

        // At double speed, with a J cut of 0.5 s (the sound starts half a second, a second of the source, earlier).
        CaptionSpan[] fast = [new(10 - 0.5, 15, 0 - 0.5 * 2, 2)];
        Cues(text, "at double speed with a 0.5 s J cut, from 10 s", CaptionProjection.Project(anchors, fast));

        // A duplicate: the source played twice shows its captions twice.
        CaptionSpan[] twice = [new(0, 3, 0, 1), new(20, 23, 0, 1)];
        var duplicate = CaptionProjection.Project(anchors, twice);
        Cues(text, "the first 3 s of the source played twice", duplicate);
        Expect(problems, duplicate.Count(c => c.Text == "Hi all.") == 2, "a duplicated clip shows its captions twice");

        // Words through the windows, as an export shows them.
        Words(text, "words through the folded clip", CaptionProjection.ProjectWords(heard, folded));

        // Edits written back: nothing changed gives the anchors back whole; an edited cue goes back through its window.
        var (same, loose) = CaptionProjection.WriteBack(anchors, folded, afterFold);
        Expect(problems, same.Count == anchors.Count && same.Zip(anchors).All(p => ReferenceEquals(p.First, p.Second)) && loose.Count == 0,
            "cues as they were projected write back the very anchors");

        var edited = afterFold.Select(c => c.Text == "The end." ? c with { Text = "The very end." } : c).ToList();
        edited.Add(new SubtitleCueState(30, 31, "Off the clips"));
        var (written, kept) = CaptionProjection.WriteBack(anchors, folded, edited);
        Cues(text, "after an edit (\"The very end.\") and a cue typed where no clip plays", written);
        Cues(text, "kept on the timeline's clock", kept);
        Expect(problems, written.Any(a => a.Text == "The very end." && Math.Abs(a.Start - 7) < 1e-9), "an edited cue is written back into the source's time");
        Expect(problems, written.Any(a => a.Text.Contains("folded", StringComparison.Ordinal)), "words no window shows are kept through an edit");
        Expect(problems, kept.Count == 1 && kept[0].Text == "Off the clips", "a cue over no window stays on the timeline's clock");
    }

    // ----- Dead air -----

    private static void DeadAirGaps(StringBuilder text, List<string> problems)
    {
        List<CaptionWord> words = [new("a", 2.0, 2.4), new("b", 2.6, 3.0), new("c", 4.0, 4.5), new("d", 4.7, 5.0)];
        foreach (var (gap, before, after) in new[] { (0.5, 0.08, 0.12), (0.3, 0.05, 0.08), (1.5, 0.12, 0.2) })
        {
            var gaps = CaptionProjection.WordGaps(words, gap, before, after, 8);
            Line(text, $"minimum {N(gap)} s, pads {N(before)}/{N(after)}: {string.Join(", ", gaps.Select(g => $"{N(g.Start)}-{N(g.End)}"))}");
        }

        var natural = CaptionProjection.WordGaps(words, 0.5, 0.08, 0.12, 8);
        Expect(problems, natural.Count == 3, "the lead-in, the one long gap and the tail are dead air");
        Expect(problems, natural.Count == 3 && Math.Abs(natural[1].Start - 3.12) < 1e-9 && Math.Abs(natural[1].End - 3.92) < 1e-9,
            "a gap is cut from the last word's end plus its pad to the next word's start less its pad");
        Expect(problems, CaptionProjection.WordGaps([], 0.5, 0.08, 0.12, 8).Count == 0, "no words: nothing is cut");
    }
}
