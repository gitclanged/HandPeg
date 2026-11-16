using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// Anchored captions: stored in source time (the voice file or main stream) and projected onto the timeline through every clip window
/// playing that source, so cuts, trims, folds, dead-air removal and moves stay aligned by construction, and hidden words are retained.
/// Pure: identical anchors and windows always yield identical cues, so undo, load and commit simply re-project.
/// </summary>
public static class CaptionProjection
{
    // A piece of a caption with no words of its own shorter than this on the timeline is not shown.
    private const double ShortestPiece = 0.04;

    // A word is never shorter than this on the timeline.
    private const double ShortestWord = 0.05;

    /// <summary>
    /// Projects anchors to timeline cues through each window (words starting inside it; untimed captions clipped to it). A cut yields a
    /// piece per side; a source played twice appears twice. Each cue carries its <see cref="CaptionPiece"/> (for write-back) and is
    /// marked <see cref="SubtitleCueState.Projected"/>.
    /// </summary>
    /// <param name="anchors">Captions in source time, sorted by start.</param>
    /// <param name="spans">Clip windows playing the source.</param>
    public static List<SubtitleCueState> Project(IReadOnlyList<SubtitleCueState> anchors, IReadOnlyList<CaptionSpan> spans)
    {
        var cues = new List<SubtitleCueState>();
        if (anchors.Count == 0 || spans.Count == 0)
            return cues;

        // Captions are found by their start; one that starts before a window may still reach into it by its length.
        var longest = 0.0;
        foreach (var anchor in anchors)
            longest = Math.Max(longest, anchor.End - anchor.Start);

        foreach (var span in spans)
        {
            if (!(span.Speed > 0) || span.End - span.Start < ShortestPiece)
                continue;

            var (from, to) = (span.MediaOffset, span.MediaEnd);
            for (var i = FirstStartingAfter(anchors, from - longest); i < anchors.Count && anchors[i].Start < to; i++)
            {
                if (anchors[i].End <= from || Cut(anchors[i], from, to) is not { } part)
                    continue;

                var start = Math.Max(span.ToSequence(part.Start), span.Start);
                var end = Math.Min(span.ToSequence(part.End), span.End);
                if (end - start < ShortestPiece)
                    continue;

                var breaks = Breaks(part, span, start, end);
                cues.Add(new SubtitleCueState(start, end, part.Text, breaks)
                {
                    Projected = true,
                    Piece = new CaptionPiece(span, anchors[i], part, start, end, part.Text, breaks),
                });
            }
        }

        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    /// <summary>Projects source-time words onto the timeline through their windows, in playback order.</summary>
    public static List<CaptionWord> ProjectWords(IReadOnlyList<CaptionWord> words, IReadOnlyList<CaptionSpan>? spans)
    {
        if (spans is null || spans.Count == 0)
            return [.. words];

        var shown = new List<CaptionWord>(words.Count);
        foreach (var span in spans)
        {
            if (!(span.Speed > 0) || span.End <= span.Start)
                continue;

            var (from, to) = (span.MediaOffset, span.MediaEnd);
            for (var i = FirstWordFrom(words, from); i < words.Count && words[i].Start < to; i++)
            {
                var word = words[i];
                var start = span.ToSequence(word.Start);
                var end = span.ToSequence(Math.Min(word.End, to));
                shown.Add(word with { Start = start, End = Math.Max(end, start + ShortestWord) });
            }
        }

        // Windows may be unordered or overlap (J/L cuts): output is sorted by timeline time.
        if (spans.Count > 1)
            shown.Sort((a, b) => a.Start.CompareTo(b.Start));
        return shown;
    }

    /// <summary>
    /// Groups source-time words into captions per window (CaptionGenerator.Lines) so no caption spans a cut; results are concatenated in
    /// order. Without windows, groups the words directly.
    /// </summary>
    public static List<List<CaptionWord>> ProjectLines(IReadOnlyList<CaptionWord> words, IReadOnlyList<CaptionSpan>? spans, CaptionStyle style)
    {
        if (spans is null || spans.Count == 0)
            return CaptionGenerator.Lines(words, style);

        var lines = new List<List<CaptionWord>>();
        foreach (var span in spans)
            lines.AddRange(CaptionGenerator.Lines(ProjectWords(words, [span]), style));
        lines.Sort((a, b) => a[0].Start.CompareTo(b[0].Start));
        return lines;
    }

    /// <summary>
    /// Splits a caption at the word boundary under <paramref name="at"/> (words are divided, never trimmed): earlier words stay first; the
    /// second starts at the first word beginning at or after it (else the last word). Words keep their times; the split point is the
    /// second part's first word. Word positions come from <see cref="SubtitleCueState.Breaks"/> (else letter shares). Null for fewer than
    /// two words or an out-of-range time.
    /// </summary>
    public static (SubtitleCueState First, SubtitleCueState Second)? SplitAtWord(SubtitleCueState cue, double at)
    {
        var words = SplitWords(cue.Text);
        var length = cue.End - cue.Start;
        if (words.Length < 2 || !(length > 0) || !(at > cue.Start) || !(at < cue.End))
            return null;

        var breaks = ValidBreaks(cue.Breaks, words);
        var kept = 1;
        while (kept < words.Length && cue.Start + length * breaks[kept - 1] < at)
            kept++;
        kept = Math.Min(kept, words.Length - 1);

        var share = breaks[kept - 1];
        var cut = cue.Start + length * share;
        double[]? firstBreaks = kept > 1 ? [.. breaks[..(kept - 1)].Select(b => b / share)] : null;
        double[]? secondBreaks = words.Length - kept > 1 ? [.. breaks[kept..].Select(b => (b - share) / (1 - share))] : null;
        return (new SubtitleCueState(cue.Start, cut, string.Join(" ", words[..kept]), firstBreaks),
                new SubtitleCueState(cut, cue.End, string.Join(" ", words[kept..]), secondBreaks));
    }

    /// <summary>Groups source-time words into anchors (CaptionGenerator.Lines), each keeping its word times.</summary>
    public static List<SubtitleCueState> Anchor(IReadOnlyList<List<CaptionWord>> lines)
    {
        var anchors = new List<SubtitleCueState>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Count == 0)
                continue;

            var times = new double[line.Count * 2];
            for (var w = 0; w < line.Count; w++)
                (times[2 * w], times[2 * w + 1]) = (line[w].Start, Math.Max(line[w].End, line[w].Start));
            var end = Math.Max(line[^1].End, line[0].Start + 0.1);
            anchors.Add(new SubtitleCueState(line[0].Start, end, string.Join(" ", line.Select(w => w.Text.Trim()))) { Words = times });
        }

        anchors.Sort((a, b) => a.Start.CompareTo(b.Start));
        return anchors;
    }

    /// <summary>
    /// Flattens anchors into ordered source-time words; untimed captions distribute words by length.
    /// </summary>
    public static List<CaptionWord> Words(IReadOnlyList<SubtitleCueState> anchors)
    {
        var words = new List<CaptionWord>();
        foreach (var anchor in anchors)
        {
            var texts = SplitWords(anchor.Text);
            if (texts.Length == 0)
                continue;

            var timed = WithWordTimes(anchor);
            for (var w = 0; w < texts.Length; w++)
                words.Add(new CaptionWord(texts[w], timed.Words![2 * w], timed.Words![2 * w + 1]));
        }

        words.Sort((a, b) => a.Start.CompareTo(b.Start));
        return words;
    }

    /// <summary>
    /// Adds word times to an untimed caption (e.g. imported subtitles) by distributing words by length, so cuts and folds divide words
    /// and hidden parts are retained. Timed or empty captions are returned unchanged.
    /// </summary>
    public static SubtitleCueState WithWordTimes(SubtitleCueState anchor)
    {
        var texts = SplitWords(anchor.Text);
        if (texts.Length == 0 || HasWordTimes(anchor, texts.Length))
            return anchor;

        var (letters, before) = (Math.Max(texts.Sum(t => t.Length), 1), 0);
        var length = anchor.End - anchor.Start;
        var times = new double[texts.Length * 2];
        for (var w = 0; w < texts.Length; w++)
        {
            times[2 * w] = anchor.Start + length * before / letters;
            before += texts[w].Length;
            times[2 * w + 1] = anchor.Start + length * before / letters;
        }

        return anchor with { Words = times };
    }

    /// <summary>
    /// Writes timeline cues back into anchors (Subtitle Studio edits). Unedited projected cues return their exact words; edited ones map
    /// back through their window (or, if moved off it, are treated as new). New cues anchor through the window under their midpoint;
    /// cues over no window stay on the timeline clock (<c>Loose</c>). Hidden words are retained. A source shown twice writes back once,
    /// with edited showings winning; captions unedited in every showing stay whole.
    /// </summary>
    public static (List<SubtitleCueState> Anchors, List<SubtitleCueState> Loose) WriteBack(
        IReadOnlyList<SubtitleCueState> anchors, IReadOnlyList<CaptionSpan> spans, IReadOnlyList<SubtitleCueState> cues)
    {
        var written = new List<SubtitleCueState>();
        var loose = new List<SubtitleCueState>();

        // How many showings each caption has now, and how many of them are still on the timeline as projected.
        var expected = new Dictionary<SubtitleCueState, int>(ReferenceEqualityComparer.Instance);
        foreach (var shown in Project(anchors, spans))
        {
            if (shown.Piece is { } piece)
                expected[piece.Anchor] = expected.GetValueOrDefault(piece.Anchor) + 1;
        }
        var intact = new Dictionary<SubtitleCueState, int>(ReferenceEqualityComparer.Instance);
        var touched = new HashSet<SubtitleCueState>(ReferenceEqualityComparer.Instance);
        foreach (var cue in cues)
        {
            if (cue.Piece is not { } piece)
                continue;
            if (IsAsProjected(cue, piece))
                intact[piece.Anchor] = intact.GetValueOrDefault(piece.Anchor) + 1;
            else
                touched.Add(piece.Anchor);
        }

        // A caption untouched is kept whole; of one edited or deleted somewhere, what no window shows stays as it is.
        var whole = new HashSet<SubtitleCueState>(ReferenceEqualityComparer.Instance);
        foreach (var anchor in anchors)
        {
            if (!touched.Contains(anchor) && intact.GetValueOrDefault(anchor) == expected.GetValueOrDefault(anchor))
            {
                whole.Add(anchor);
                written.Add(anchor);
            }
            else
            {
                written.AddRange(Hidden(anchor, spans));
            }
        }

        // The edited first: their sources are what the other showings of the same words give way to.
        var replaced = new HashSet<(double, string)>();
        var unchanged = new List<SubtitleCueState>();
        foreach (var cue in cues)
        {
            var middle = (cue.Start + cue.End) / 2;
            if (cue.Piece is { } piece)
            {
                if (IsAsProjected(cue, piece))
                {
                    if (!whole.Contains(piece.Anchor))
                        unchanged.Add(piece.Source);
                    continue;
                }

                // Still over its window: map back through it. Moved off: treat as a new cue (window under its midpoint, or timeline clock).
                replaced.Add((piece.Source.Start, piece.Source.Text));
                if (middle >= piece.Span.Start && middle < piece.Span.End)
                {
                    if (ToSource(cue, piece.Span) is { } edited)
                        written.Add(edited);
                    continue;
                }
            }

            // Typed in by hand (or moved off its window): anchored through the window under its middle, when there is one.
            var under = -1;
            for (var s = 0; s < spans.Count && under < 0; s++)
            {
                if (spans[s].Speed > 0 && middle >= spans[s].Start && middle < spans[s].End)
                    under = s;
            }

            if (under >= 0 && ToSource(cue, spans[under]) is { } anchored)
                written.Add(anchored);
            else
                loose.Add(cue with { Projected = false, Piece = null });
        }

        var given = new HashSet<(double, string)>();
        foreach (var source in unchanged)
        {
            if (!replaced.Contains((source.Start, source.Text)) && given.Add((source.Start, source.Text)))
                written.Add(source);
        }

        written.Sort((a, b) => a.Start.CompareTo(b.Start));
        return (written, loose);
    }

    /// <summary>
    /// Silences between words in source time for Remove Dead Air: gaps (including before the first and after the last, up to
    /// <paramref name="sourceEnd"/>) of at least <paramref name="minimumGap"/>, minus small pads around words.
    /// </summary>
    /// <param name="words">Words sorted by start.</param>
    /// <param name="sourceEnd">Source end; &lt;= 0 when unknown (the trailing gap is open-ended).</param>
    public static List<(double Start, double End)> WordGaps(
        IReadOnlyList<CaptionWord> words, double minimumGap, double padBefore, double padAfter, double sourceEnd)
    {
        var gaps = new List<(double Start, double End)>();
        if (words.Count == 0)
            return gaps;

        (minimumGap, padBefore, padAfter) = (Math.Max(minimumGap, 0.01), Math.Max(padBefore, 0), Math.Max(padAfter, 0));
        var heardUntil = double.NegativeInfinity;
        foreach (var word in words)
        {
            // The silence before the recording's first word runs from its start; any other from where the last word ended.
            var silentFrom = double.IsNegativeInfinity(heardUntil) ? 0 : heardUntil;
            if (word.Start - silentFrom >= minimumGap)
            {
                var (from, to) = (double.IsNegativeInfinity(heardUntil) ? 0 : heardUntil + padAfter, word.Start - padBefore);
                if (to > from)
                    gaps.Add((from, to));
            }

            heardUntil = Math.Max(heardUntil, word.End);
        }

        var end = sourceEnd > 0 ? sourceEnd : 1e9;
        if (end - heardUntil >= minimumGap && end > heardUntil + padAfter)
            gaps.Add((heardUntil + padAfter, end));
        return gaps;
    }

    /// <summary>
    /// Remaps Sequence captions onto an output made of concatenated stretches, shifted by <paramref name="offset"/>: words outside every
    /// stretch are dropped and emptied captions removed. With no stretches (exports pass none), only the shift applies.
    /// </summary>
    public static List<List<CaptionWord>> ToOutputClock(
        IReadOnlyList<List<CaptionWord>> lines, IReadOnlyList<(double Start, double End)> ranges, double offset)
    {
        var output = new List<List<CaptionWord>>(lines.Count);
        foreach (var line in lines)
        {
            var kept = new List<CaptionWord>(line.Count);
            foreach (var word in line)
            {
                if (ranges.Count == 0)
                {
                    kept.Add(word with { Start = word.Start + offset, End = word.End + offset });
                    continue;
                }

                var before = 0.0;
                foreach (var (start, end) in ranges)
                {
                    if (word.Start >= start && word.Start < end)
                    {
                        var at = before + word.Start - start + offset;
                        kept.Add(word with { Start = at, End = at + Math.Max(Math.Min(word.End, end) - word.Start, 0) });
                        break;
                    }

                    before += Math.Max(end - start, 0);
                }
            }

            if (kept.Count > 0)
                output.Add(kept);
        }

        return output;
    }

    // ----- The pieces -----

    /// <summary>What of a caption a window of its source [from, to) shows, in the source's time; null for nothing.</summary>
    private static SubtitleCueState? Cut(SubtitleCueState anchor, double from, double to)
    {
        var texts = SplitWords(anchor.Text);
        if (!HasWordTimes(anchor, texts.Length))
        {
            var (start, end) = (Math.Max(anchor.Start, from), Math.Min(anchor.End, to));
            return end - start < ShortestPiece ? null
                : start == anchor.Start && end == anchor.End ? anchor
                : anchor with { Start = start, End = end, Words = null };
        }

        var (first, last) = (-1, -1);
        for (var w = 0; w < texts.Length; w++)
        {
            var said = anchor.Words![2 * w];
            if (said >= from && said < to)
            {
                if (first < 0)
                    first = w;
                last = w;
            }
            else if (first >= 0)
            {
                break;
            }
        }

        if (first < 0)
            return null;
        if (first == 0 && last == texts.Length - 1 && anchor.End <= to)
            return anchor;

        var times = anchor.Words![(2 * first)..(2 * last + 2)];
        for (var t = 1; t < times.Length; t += 2)
            times[t] = Math.Min(times[t], to);
        var text = first == 0 && last == texts.Length - 1 ? anchor.Text : string.Join(" ", texts[first..(last + 1)]);
        return new SubtitleCueState(times[0], Math.Max(Math.Min(times[^1], to), times[0]), text) { Words = times };
    }

    /// <summary>Start fractions of each word after the first within the cue; null when indeterminable.</summary>
    private static double[]? Breaks(SubtitleCueState part, CaptionSpan span, double start, double end)
    {
        if (part.Words is not { Length: >= 4 } times || end <= start)
            return null;

        var breaks = new double[times.Length / 2 - 1];
        for (var w = 0; w < breaks.Length; w++)
        {
            breaks[w] = (span.ToSequence(times[2 * (w + 1)]) - start) / (end - start);
            if (breaks[w] <= (w == 0 ? 0 : breaks[w - 1]) || breaks[w] >= 1)
                return null;
        }

        return breaks;
    }

    /// <summary>Runs of consecutive words no window shows, retained unchanged while hidden.</summary>
    private static IEnumerable<SubtitleCueState> Hidden(SubtitleCueState anchor, IReadOnlyList<CaptionSpan> spans)
    {
        var texts = SplitWords(anchor.Text);
        if (!HasWordTimes(anchor, texts.Length))
        {
            var seen = false;
            foreach (var span in spans)
                seen |= span.Speed > 0 && anchor.Start < span.MediaEnd && anchor.End > span.MediaOffset;
            if (!seen)
                yield return anchor;
            yield break;
        }

        var run = -1;
        for (var w = 0; w <= texts.Length; w++)
        {
            var hidden = w < texts.Length && !Shown(anchor.Words![2 * w], spans);
            if (hidden && run < 0)
                run = w;
            if (hidden || run < 0)
                continue;

            if (run == 0 && w == texts.Length)
            {
                yield return anchor;
                yield break;
            }

            var times = anchor.Words![(2 * run)..(2 * w)];
            yield return new SubtitleCueState(times[0], times[^1], string.Join(" ", texts[run..w])) { Words = times };
            run = -1;
        }
    }

    /// <summary>Whether a moment of the source is played by any window.</summary>
    private static bool Shown(double moment, IReadOnlyList<CaptionSpan> spans)
    {
        foreach (var span in spans)
        {
            if (span.Speed > 0 && moment >= span.MediaOffset && moment < span.MediaEnd)
                return true;
        }

        return false;
    }

    /// <summary>Whether a cue is still what the projection made of it: the same times, words and word boundaries.</summary>
    private static bool IsAsProjected(SubtitleCueState cue, CaptionPiece piece) =>
        Math.Abs(cue.Start - piece.Start) < 1e-6 && Math.Abs(cue.End - piece.End) < 1e-6 && cue.Text == piece.Text
        && (cue.Breaks ?? []).AsSpan().SequenceEqual(piece.Breaks ?? []);

    /// <summary>Maps a timeline cue back to source time through a window, timing words by their boundaries.</summary>
    private static SubtitleCueState? ToSource(SubtitleCueState cue, CaptionSpan span)
    {
        var texts = SplitWords(cue.Text);
        if (texts.Length == 0 || !(span.Speed > 0) || cue.End <= cue.Start)
            return null;

        var breaks = ValidBreaks(cue.Breaks, texts);
        var times = new double[texts.Length * 2];
        for (var w = 0; w < texts.Length; w++)
        {
            var (from, to) = (w == 0 ? 0 : breaks[w - 1], w < breaks.Length ? breaks[w] : 1);
            times[2 * w] = span.ToSource(cue.Start + (cue.End - cue.Start) * from);
            times[2 * w + 1] = span.ToSource(cue.Start + (cue.End - cue.Start) * to);
        }

        return new SubtitleCueState(span.ToSource(cue.Start), span.ToSource(cue.End), cue.Text) { Words = times };
    }

    /// <summary>Stored word boundaries if they match the words; otherwise letter-share boundaries (as SubtitleCue.GetBreaks).</summary>
    private static double[] ValidBreaks(double[]? kept, string[] words)
    {
        if (words.Length < 2)
            return [];

        if (kept is { } given && given.Length == words.Length - 1 && given[0] > 0 && given[^1] < 1)
        {
            var ordered = true;
            for (var i = 1; i < given.Length && ordered; i++)
                ordered = given[i] > given[i - 1];
            if (ordered)
                return given;
        }

        var breaks = new double[words.Length - 1];
        double letters = Math.Max(words.Sum(w => w.Length), 1), before = 0;
        for (var i = 0; i < breaks.Length; i++)
        {
            before += words[i].Length;
            breaks[i] = before / letters;
        }

        return breaks;
    }

    private static string[] SplitWords(string? text) =>
        (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasWordTimes(SubtitleCueState anchor, int words) => words > 0 && anchor.Words is { } times && times.Length == words * 2;

    /// <summary>The first caption whose start is at or after a moment (they are ordered by their start).</summary>
    private static int FirstStartingAfter(IReadOnlyList<SubtitleCueState> anchors, double moment)
    {
        var (low, high) = (0, anchors.Count);
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (anchors[middle].Start < moment)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>The first word that starts at or after a moment (they are ordered by their start).</summary>
    private static int FirstWordFrom(IReadOnlyList<CaptionWord> words, double moment)
    {
        var (low, high) = (0, words.Count);
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (words[middle].Start < moment)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}
