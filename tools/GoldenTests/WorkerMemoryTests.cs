using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4D-TabPerf (implementer W): Instant Seek's frame cache sized by the hardware and grown on demand under the
/// Settings maximum, and the compositor worker giving back what it holds on a tab suspend or when idle.
/// <para>worker-cache-budget: the adaptive rule as pure functions (MemoryTier): the baseline, the ceiling, the maximum, the steps of
/// growth, what counts as thrash, the free list's cap, and Settings' typed maximum. worker-frame-cache: a real FrameCache on this PC's
/// card at a small scale with a clock of the test's own: no growth while what goes is old, a step when what goes is still needed, at most
/// one step every two seconds, never past the maximum, back to the baseline on Clear; a frame of a forgotten source never kept; the free
/// list capped and emptied; the texture pool emptied by ReleaseFree (and not by ReleaseUnusedSince(0)); the driver's Trim.
/// worker-trim: the protocol's new fields where they belong, and a real worker drawing 4K frames with blurs and readbacks, then trimmed:
/// the trim and a clear echoed even with a Redraw left after them, its private bytes down, the idle rule, and the same picture drawn
/// after it. Numbers that depend on this PC go to the console only.</para>
/// <para>The TabPerf follow-up (implementer W2, the user's X1: "keep scrubbing as smooth as possible if the hardware is there"): a big card
/// (discrete, more than 4 GB of its own) takes its whole maximum at once and keeps its frames across a tab put behind, its sources'
/// serials parked so a file registered again finds them (worker-cache-budget, worker-frame-cache); a tab put behind on this PC as the
/// card's rule says, and the renderer's kept textures let go of by a trim and made again with the same picture (worker-trim).</para>
/// </summary>
public static class WorkerMemoryTests
{
    private const long Kb = 1L << 10, Mb = 1L << 20, Gb = 1L << 30;

    /// <summary>The window-free cases (and those on a window of their own), before the UI-scale tests.</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("worker-cache-budget",
            "# The frame cache's adaptive budget (5.4D-TabPerf, the user's U3): a baseline grown on demand up to the hardware's ceiling or the user's maximum.",
            Budget);
        yield return Case("worker-frame-cache",
            "# A real frame cache at a small scale (5.4D-TabPerf): growth only on thrash, two seconds apart, back to the baseline on Clear; nothing kept that nothing holds.",
            RealCache);
        yield return Case("worker-trim",
            "# The compositor worker giving memory back (5.4D-TabPerf, the user's U2): counted requests, a real worker trimmed after 4K frames, the idle rule.",
            Trim);
    }

    /// <summary>The cases that need the WPF Application the UI-scale tests make: run last.</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
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

    private static void Line(StringBuilder text, FormattableString line) =>
        text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Expect<T>(List<string> problems, string what, T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: {actual}, not {expected}"));
    }

    private static string Size(long bytes) => bytes % Gb == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / Gb} GB")
        : bytes % Mb == 0 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / Mb} MB") : string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)Mb:0.##} MB");

    // ----- worker-cache-budget -----

    private static void Budget(StringBuilder text, List<string> problems)
    {
        // Each machine: baseline, ceiling, and the budget's steps of growth up to it (Auto).
        text.Append("each machine: where Auto starts, the most it grows to (before Windows' caps), and its steps of growth:\n");
        (string Machine, bool Warp, bool Unified, long Dedicated, long Physical, bool Busy, long Free, long Baseline, long Ceiling)[] machines =
        [
            ("RX 7900 XTX, 24 GB, 22 GB free, 64 GB computer", false, false, 24 * Gb, 64 * Gb, false, 22 * Gb, 2 * Gb, 22 * Gb - 24 * Gb / 5),
            ("RX 7900 XTX, Windows not asked yet", false, false, 24 * Gb, 64 * Gb, false, 0, 2 * Gb, 3 * Gb),
            ("RX 7900 XTX, a game on it", false, false, 24 * Gb, 64 * Gb, true, 6 * Gb, 2 * Gb, 3 * Gb),
            ("GTX 1070, 8 GB, 7 GB free, 16 GB computer", false, false, 8 * Gb, 16 * Gb, false, 7 * Gb, 2 * Gb, 5 * Gb),
            ("an 8 GB card, a game on it", false, false, 8 * Gb, 32 * Gb, true, 2 * Gb, 2 * Gb, 1 * Gb),
            ("a 4 GB card, 16 GB computer", false, false, 4 * Gb, 16 * Gb, false, 3 * Gb, 1 * Gb, 1 * Gb),
            ("a 4 GB card, 64 GB computer", false, false, 4 * Gb, 64 * Gb, false, 3 * Gb, 1 * Gb, 1 * Gb),
            ("a 4 GB card, 3.5 GB free, 32 GB computer", false, false, 4 * Gb, 32 * Gb, false, 7 * Gb / 2, 1536 * Mb, 1536 * Mb),
            ("a 4 GB card (Arc A310), a game on it, 64 GB computer", false, false, 4 * Gb, 64 * Gb, true, 1 * Gb, 512 * Mb, 512 * Mb),
            ("a 4 GB card, Windows not asked yet", false, false, 4 * Gb, 64 * Gb, false, 0, 512 * Mb, 512 * Mb),
            ("a 2 GB card, 1 GB free, 24 GB computer", false, false, 2 * Gb, 24 * Gb, false, 1 * Gb, 256 * Mb, 256 * Mb),
            ("a 2 GB card, 1.75 GB free, 32 GB computer", false, false, 2 * Gb, 32 * Gb, false, 1792 * Mb, 256 * Mb, 256 * Mb),
            ("a 2 GB card, a game on it", false, false, 2 * Gb, 32 * Gb, true, 512 * Mb, 256 * Mb, 256 * Mb),
            ("a 256 MB card, 16 GB computer", false, false, 256 * Mb, 16 * Gb, false, 0, 512 * Mb, 512 * Mb),
            ("Adreno X1-85, 16 GB computer (unified)", false, true, 128 * Mb, 16 * Gb, false, 7 * Gb, 2 * Gb, 4 * Gb),
            ("unified memory, 32 GB computer", false, true, 512 * Mb, 32 * Gb, false, 0, 2 * Gb, 8 * Gb),
            ("unified memory, 64 GB computer", false, true, 512 * Mb, 64 * Gb, false, 0, 2 * Gb, 32 * Gb),
            ("unified memory, 8 GB computer", false, true, 512 * Mb, 8 * Gb, false, 0, 1 * Gb, 2 * Gb),
            ("unified memory, its size not known", false, true, 128 * Mb, 0, false, 0, 512 * Mb, 512 * Mb),
            ("WARP, 16 GB computer", true, false, 0, 16 * Gb, false, 0, 512 * Mb, 512 * Mb),
            ("WARP, 32 GB computer", true, false, 0, 32 * Gb, false, 0, 512 * Mb, 512 * Mb),
        ];
        foreach (var m in machines)
        {
            var baseline = MemoryTier.Baseline(m.Warp, m.Unified, m.Dedicated, m.Physical, m.Busy, m.Free);
            var ceiling = MemoryTier.Ceiling(m.Warp, m.Unified, m.Dedicated, m.Physical, m.Busy, m.Free);
            var maximum = MemoryTier.Maximum(0, ceiling);
            var steps = new List<string>();
            for (var step = 0; step < 16; step++)
            {
                var grown = MemoryTier.Grown(baseline, step, maximum);
                steps.Add(Size(grown));
                if (grown >= maximum)
                    break;
            }

            Line(text, $"  {m.Machine}: {Size(baseline)}, at most {Size(ceiling)}; {string.Join(" -> ", steps)}");
            Expect(problems, $"the baseline of {m.Machine}", baseline, m.Baseline);
            Expect(problems, $"the ceiling of {m.Machine}", ceiling, m.Ceiling);
            if (MemoryTier.Grown(baseline, 0, maximum) > baseline || MemoryTier.Grown(baseline, 99, maximum) != maximum)
                problems.Add($"{m.Machine}: the steps do not start at the baseline (or below it) and end at the maximum");
        }

        // What is always kept free (5.4.1-M4): a discrete card max(2 GB, 20%) of its own; unified memory max(5 GB, 25%) of the computer's
        // memory up to 32 GB, max(12 GB, 30%) above, out of what is available (the cache's physical room).
        text.Append("kept free of the cache: a discrete card's reserve, unified memory's reserve and room (held + available - reserve):\n");
        (string What, long Actual, long Expected)[] reserves =
        [
            ("a 24 GB card's reserve", MemoryTier.VramReserve(24 * Gb), 24 * Gb / 5),
            ("an 8 GB card's reserve", MemoryTier.VramReserve(8 * Gb), 2 * Gb),
            ("16 GB unified: reserve", MemoryTier.UnifiedReserve(16 * Gb), 5 * Gb),
            ("32 GB unified: reserve", MemoryTier.UnifiedReserve(32 * Gb), 8 * Gb),
            ("64 GB unified: reserve", MemoryTier.UnifiedReserve(64 * Gb), 64 * Gb / 10 * 3),
            ("16 GB unified, 1 GB held, 7 GB available: room", MemoryTier.UnifiedRoom(1 * Gb, 7 * Gb, 16 * Gb), 3 * Gb),
            ("16 GB unified, nothing held, 4 GB available: room", MemoryTier.UnifiedRoom(0, 4 * Gb, 16 * Gb), 0),
        ];
        foreach (var r in reserves)
        {
            Line(text, $"  {r.What}: {Size(r.Actual)}");
            Expect(problems, r.What, r.Actual, r.Expected);
        }

        // The user's maximum (Settings' "Maximum frame cache"): caps the ceiling, never raises it.
        text.Append("the user's maximum on the RX 7900 XTX (ceiling 11 GB) and on the Adreno (ceiling 2 GB): Auto, 1, 4, 8, 16 GB:\n");
        foreach (var (card, ceiling) in new[] { ("RX 7900 XTX", 11 * Gb), ("Adreno", 2 * Gb) })
        {
            var maxima = new[] { 0L, 1 * Gb, 4 * Gb, 8 * Gb, 16 * Gb }.Select(asked => MemoryTier.Maximum(asked, ceiling)).ToList();
            Line(text, $"  {card}: {string.Join(", ", maxima.Select(Size))}");
            for (var i = 0; i < maxima.Count; i++)
            {
                var asked = new[] { 0L, 1 * Gb, 4 * Gb, 8 * Gb, 16 * Gb }[i];
                Expect(problems, $"{card}'s maximum for {Size(asked)}", maxima[i], asked == 0 ? ceiling : Math.Min(asked, ceiling));
            }
        }

        Expect(problems, "a 1 GB maximum below the 2 GB baseline: the budget is 1 GB from the start", MemoryTier.Grown(2 * Gb, 0, 1 * Gb), 1 * Gb);
        Line(text, $"  a maximum below the baseline holds the budget there from the start: 2 GB baseline, 1 GB maximum -> {Size(MemoryTier.Grown(2 * Gb, 0, 1 * Gb))}");

        // At most one step every two seconds, and none at the maximum.
        text.Append("a step of growth is allowed (two seconds apart, below the maximum):\n");
        (string What, int Steps, double Since, bool Expected)[] grows =
        [
            ("the first step", 0, double.PositiveInfinity, true),
            ("a step 1.9 s after the last", 1, 1.9, false),
            ("a step 2 s after the last", 1, 2, true),
            ("at the maximum (2 GB + 9 steps of an 11 GB ceiling)", 9, 60, false),
            ("one short of it", 8, 60, true),
        ];
        foreach (var g in grows)
        {
            var may = MemoryTier.MayGrow(2 * Gb, g.Steps, 11 * Gb, g.Since);
            Line(text, $"  {g.What}: {(may ? "yes" : "no")}");
            Expect(problems, $"may grow, {g.What}", may, g.Expected);
        }

        // What counts as thrash.
        text.Append("letting go of a frame shows the cache too small (thrash) when:\n");
        (string What, double Since, double Near, bool Expected)[] thrash =
        [
            ("decoded 1 s ago, far from every moment of interest", 1, double.PositiveInfinity, true),
            ("decoded 3.9 s ago", 3.9, double.PositiveInfinity, true),
            ("decoded 4 s ago, no moment of interest", 4, double.PositiveInfinity, false),
            ("decoded a minute ago, 1.5 s from the moment shown", 60, 1.5, true),
            ("decoded a minute ago, 2 s from a hint", 60, 2, true),
            ("decoded a minute ago, 2.5 s from the playhead", 60, 2.5, false),
        ];
        foreach (var t in thrash)
        {
            var thrashes = MemoryTier.IsThrash(t.Since, t.Near);
            Line(text, $"  {t.What}: {(thrashes ? "thrash" : "not")}");
            Expect(problems, $"thrash, {t.What}", thrashes, t.Expected);
        }

        // The free list's cap.
        text.Append("the free list keeps at most an eighth of the budget, at most 256 MB:\n");
        foreach (var (budget, expected) in new[] { (512 * Mb, 64 * Mb), (2 * Gb, 256 * Mb), (11 * Gb, 256 * Mb) })
        {
            var cap = MemoryTier.FreeListCap(budget);
            Line(text, $"  a budget of {Size(budget)}: {Size(cap)}");
            Expect(problems, $"the free list's cap of {Size(budget)}", cap, expected);
        }

        // Settings' row: what a typed maximum means, and how the list shows one.
        text.Append("Settings' Maximum Frame Cache (GB), typed -> the setting (MB; 0 Auto, -1 Off):\n");
        (string Typed, int? Expected)[] typed =
        [
            ("Auto", 0), ("auto (up to 11.0 GB here)", 0), ("Off", -1), ("8", 8192), ("8 GB", 8192), ("2.5", 2560), ("3G", 3072), ("0", 0),
            ("-1", -1), ("100", 65536), ("0.0001", 0), ("lots", null), ("", null),
        ];
        foreach (var (words, expected) in typed)
        {
            var parsed = SettingsWindow.ParseFrameCacheGb(words);
            Line(text, $"  \"{words}\": {(parsed is { } mb ? mb.ToString(CultureInfo.InvariantCulture) : "not a maximum")}");
            Expect(problems, $"typed \"{words}\"", parsed, expected);
        }

        var shown = SettingsWindow.FrameCacheChoices(2560, 11 * Gb).Select(c => c.Display).ToList();
        Line(text, $"  the list with 2.5 GB typed, on a card whose ceiling is 11 GB: {string.Join(", ", shown)}");
        Expect(problems, "the list's entries", string.Join("|", shown), "Auto (up to 11.0 GB here)|Off|1 GB|2 GB|2.5 GB|4 GB|8 GB|12 GB|16 GB");
        Expect(problems, "Auto's entry without a running player", SettingsWindow.FrameCacheChoices(0, 0)[0].Display, "Auto");

        // The big card's rule (the TabPerf follow-up, the user's X1): the budget the maximum from the start, and its frames kept across a
        // tab put behind; everything else as above, its cache emptied by a tab put behind.
        text.Append("the big card's rule (discrete, more than 4 GB of its own): where Auto's budget starts, and a tab put behind:\n");
        foreach (var m in machines)
        {
            var big = MemoryTier.IsBigCard(m.Warp, m.Unified, m.Dedicated);
            var start = MemoryTier.Budget(big, m.Baseline, 0, MemoryTier.Maximum(0, m.Ceiling));
            var empties = CompositorWorker.TrimEmptiesCache(asked: true, big);
            Line(text, $"  {m.Machine}: {(big ? "a big card" : "not a big card")}, starts at {Size(start)}; a tab put behind {(empties ? "empties the cache" : "keeps its frames")}");
            Expect(problems, $"where {m.Machine} starts", start, big ? m.Ceiling : m.Baseline);
            Expect(problems, $"a tab put behind on {m.Machine} empties the cache", empties, !big);
        }

        var capped = MemoryTier.Budget(true, 2 * Gb, 0, MemoryTier.Maximum(4 * Gb, 11 * Gb));
        Line(text, $"  the RX 7900 XTX with a 4 GB maximum: starts at {Size(capped)}; its steps of growth: none ({Size(MemoryTier.Budget(true, 2 * Gb, 5, 4 * Gb))} after five)");
        Expect(problems, "a big card's budget under a 4 GB maximum", capped, 4 * Gb);
        Expect(problems, "a big card's budget after steps", MemoryTier.Budget(true, 2 * Gb, 5, 4 * Gb), 4 * Gb);
        Expect(problems, "a small card's budget after two steps", MemoryTier.Budget(false, 2 * Gb, 2, 4 * Gb), 4 * Gb);

        text.Append("a trim empties the frame cache when:\n");
        foreach (var (asked, big) in new[] { (true, false), (true, true), (false, false), (false, true) })
        {
            var empties = CompositorWorker.TrimEmptiesCache(asked, big);
            Line(text, $"  {(asked ? "asked for (a tab put behind)" : "idle")}, {(big ? "a big card" : "unified memory or a small card")}: {(empties ? "emptied" : "frames kept")}");
            Expect(problems, $"a trim {(asked ? "asked for" : "idle")} on {(big ? "a big card" : "a small one")}", empties, asked && !big);
        }
    }

    // ----- worker-frame-cache -----

    private static void RealCache(StringBuilder text, List<string> problems)
    {
        using var gpu = GpuDevice.Create(-1);
        using var cache = new FrameCache(gpu);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        this PC's card: {gpu.AdapterName}; Auto here starts at {cache.BudgetBytes / (double)Gb:0.0} GB and grows to at most {cache.MaximumBytes / (double)Gb:0.0} GB (before Windows is asked)"));

        // The rule at a small scale: a 4 MB baseline, a 12 MB ceiling, 4 MB steps, frames of 384 KB, and the test's own clock.
        var now = 1_000L * Stopwatch.Frequency;
        void Wait(double seconds) => now += (long)(seconds * Stopwatch.Frequency);
        cache.ScaleForTests(4 * Mb, 12 * Mb, 4 * Mb, () => now);
        const int Side = 512;
        var frameBytes = (long)Side * Side * 3 / 2;
        void Insert(int source, double time)
        {
            var frame = cache.Rent(Side, Side, tenBit: false, software: true);
            (frame.Source, frame.Time, frame.Until, frame.Closed) = (source, time, time + 1 / 30.0, true);
            cache.Insert(frame);
        }

        Line(text, $"a frame cache on this PC's card, a 4 MB baseline, a 12 MB ceiling, 4 MB steps, frames of {frameBytes / Kb} KB:");
        Expect(problems, "the budget at the start", cache.BudgetBytes, 4 * Mb);
        Expect(problems, "the maximum", cache.MaximumBytes, 12 * Mb);

        // Old frames go: no growth.
        for (var i = 0; i < 10; i++)
            Insert(1, i / 30.0);
        Wait(10);
        for (var i = 10; i < 20; i++)
            Insert(1, i / 30.0);
        Line(text, $"  frames decoded 10 s before the ones that pushed them out, none near a moment of interest: {(cache.GrowSteps == 0 && cache.Evictions > 0 ? "let go of, the budget as it was" : "NOT")}");
        Expect(problems, "no growth while what goes is old", (cache.GrowSteps, cache.BudgetBytes), (0, 4 * Mb));
        if (cache.Evictions == 0)
            problems.Add("nothing was let go of over the budget");
        Expect(problems, "the free list within its cap", cache.FreeBytes <= MemoryTier.FreeListCap(cache.BudgetBytes), true);

        // Young frames go: a step.
        Wait(10);
        for (var i = 0; i < 12; i++)
        {
            Insert(2, i / 30.0);
            Wait(0.05);
        }

        Line(text, $"  frames decoded within the last second pushed out: {(cache.GrowSteps == 1 && cache.BudgetBytes == 8 * Mb ? "a step, 8 MB" : "NOT")}");
        Expect(problems, "a step of growth on thrash", (cache.GrowSteps, cache.BudgetBytes), (1, 8 * Mb));

        // Within two seconds of it: no second step, frames go instead.
        var evictions = cache.Evictions;
        for (var i = 12; i < 30; i++)
        {
            Insert(2, i / 30.0);
            Wait(0.05);
        }

        Line(text, $"  thrash again within two seconds of the step: {(cache.GrowSteps == 1 && cache.Evictions > evictions ? "no step, frames let go of" : "NOT")}");
        Expect(problems, "no second step within two seconds", cache.GrowSteps, 1);
        if (cache.Evictions == evictions)
            problems.Add("nothing was let go of within the two seconds");

        // Two seconds on: the next step, then the maximum holds.
        Wait(2);
        for (var i = 30; i < 50; i++)
        {
            Insert(2, i / 30.0);
            Wait(0.01);
        }

        Line(text, $"  thrash two seconds after the step: {(cache.GrowSteps == 2 && cache.BudgetBytes == 12 * Mb ? "another, 12 MB (the ceiling)" : "NOT")}");
        Expect(problems, "the second step", (cache.GrowSteps, cache.BudgetBytes), (2, 12 * Mb));
        Wait(3);
        for (var i = 50; i < 80; i++)
        {
            Insert(2, i / 30.0);
            Wait(0.01);
        }

        Line(text, $"  thrash at the ceiling: {(cache.GrowSteps == 2 && cache.BudgetBytes == 12 * Mb && cache.Bytes <= 12 * Mb ? "no step past it, the cache within it" : "NOT")}");
        Expect(problems, "no growth past the ceiling", (cache.GrowSteps, cache.BudgetBytes), (2, 12 * Mb));

        // Frames by a moment of interest count as needed however old.
        cache.Clear();
        // (Old frames 1 to 2.8 s after the moment shown, the farthest going first; once those within 2 s of it would go, a step.)
        cache.SetInterests([(3, 5.0, 1.0)]);
        for (var i = 0; i < 10; i++)
            Insert(3, 6.0 + i * 0.2);
        Wait(30);
        for (var i = 0; i < 6; i++)
            Insert(3, 5.0 + i * 0.1);
        Line(text, $"  frames decoded 30 s ago but within 2 s of the moment shown, pushed out (after those farther went with no step): {(cache.GrowSteps == 1 ? "a step" : "NOT")}");
        Expect(problems, "growth for frames in the seek window", cache.GrowSteps, 1);
        cache.SetInterests([]);

        // The user's maximum caps the grown budget.
        Wait(3);
        cache.SetBudget(6 * Mb);
        Line(text, $"  a 6 MB maximum asked for after a step (8 MB): {(cache.BudgetBytes == 6 * Mb && cache.MaximumBytes == 6 * Mb ? "6 MB" : "NOT")}");
        Expect(problems, "the user's maximum caps the budget", (cache.BudgetBytes, cache.MaximumBytes), (6 * Mb, 6 * Mb));

        // Clear: back to the baseline, nothing held.
        cache.Clear();
        Line(text, $"  Clear: {(cache.GrowSteps == 0 && cache.BudgetBytes == 4 * Mb && cache.Bytes == 0 && cache.Count == 0 ? "every frame gone, the budget back at 4 MB" : "NOT")}");
        Expect(problems, "Clear empties the cache and goes back to the baseline", (cache.GrowSteps, cache.BudgetBytes, cache.Bytes, cache.Count), (0, 4 * Mb, 0L, 0));

        // A source forgotten: a frame of it inserted afterwards (a lane that was decoding it) is not kept.
        Insert(4, 0);
        cache.Forget(4);
        var (before, beforeBytes) = (cache.Count, cache.Bytes);
        Insert(4, 1 / 30.0);
        Line(text, $"  a frame of a forgotten source inserted after Forget: {(cache.Count == before && cache.Bytes == beforeBytes && !cache.Has(4, 1 / 30.0) ? "not kept" : "NOT")}");
        Expect(problems, "a forgotten source's late frame is not kept", (cache.Count, cache.Bytes, cache.Has(4, 1 / 30.0)), (before, beforeBytes, false));
        Insert(5, 0);
        Expect(problems, "a source never forgotten is kept", cache.Has(5, 0), true);

        // DropFree: the free list emptied; a frame being drawn when it was retired goes once it is let go of.
        var pinned = cache.Exact(5, 0);
        cache.Clear();
        var dropped = cache.DropFree();
        var heldOn = pinned is not null && pinned.Planar is null && pinned.Luma is not null;
        if (pinned is not null)
            cache.Unpin(pinned);
        var droppedAfter = cache.DropFree();
        Line(text, $"  DropFree: {(cache.FreeBytes == 0 && heldOn && droppedAfter == frameBytes ? "the free list emptied; a frame drawn when cleared kept until let go of, then dropped" : "NOT")}");
        Expect(problems, "DropFree empties the free list", cache.FreeBytes, 0L);
        Expect(problems, "a pinned frame is not dropped while drawn", heldOn, true);
        Expect(problems, "the frame let go of is dropped by the next DropFree", droppedAfter, frameBytes);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        DropFree let go of {dropped / Kb} KB, then {droppedAfter / Kb} KB"));

        // A big card (the TabPerf follow-up, the user's X1): the budget its maximum at once; young frames fill it with no step and nothing
        // let go of; past it the least recently used go, still with no step; the user's maximum holds it lower at once.
        cache.SetBudget(0);
        cache.BigCardForTests(true);
        cache.Clear();
        Wait(10);
        Line(text, $"  a big card: the budget at the start {Size(cache.BudgetBytes)} (the 12 MB ceiling, not the 4 MB baseline)");
        Expect(problems, "a big card's budget at the start", (cache.BudgetBytes, cache.GrowSteps), (12 * Mb, 0));
        var bigEvictions = cache.Evictions;
        for (var i = 0; i < 30; i++)
        {
            Insert(6, i / 30.0);
            Wait(0.01);
        }

        var filled = cache.Evictions == bigEvictions && cache.Bytes == 30 * frameBytes && cache.GrowSteps == 0;
        Line(text, $"  30 frames decoded a moment apart (11.25 MB): {(filled ? "all kept, nothing let go of, no step of growth" : "NOT")}");
        Expect(problems, "a big card fills to its maximum without letting go", (cache.Evictions - bigEvictions, cache.Bytes, cache.GrowSteps), (0L, 30 * frameBytes, 0));
        for (var i = 30; i < 40; i++)
        {
            Insert(6, i / 30.0);
            Wait(0.01);
        }

        var lru = cache.Evictions > bigEvictions && cache.Bytes <= 12 * Mb && cache.BudgetBytes == 12 * Mb && cache.GrowSteps == 0
            && !cache.Has(6, 0) && cache.Has(6, 39 / 30.0);
        Line(text, $"  10 more past it: {(lru ? "the oldest let go of, the newest kept, within 12 MB, no step" : "NOT")}");
        Expect(problems, "a big card past its maximum", (cache.BudgetBytes, cache.GrowSteps, cache.Bytes <= 12 * Mb, cache.Has(6, 0), cache.Has(6, 39 / 30.0)),
            (12 * Mb, 0, true, false, true));
        cache.SetBudget(6 * Mb);
        Line(text, $"  a 6 MB maximum asked for: {(cache.BudgetBytes == 6 * Mb && cache.Bytes <= 6 * Mb ? "the budget 6 MB at once, the cache within it" : "NOT")}");
        Expect(problems, "a big card under the user's maximum", (cache.BudgetBytes, cache.Bytes <= 6 * Mb), (6 * Mb, true));
        cache.SetBudget(0);
        cache.BigCardForTests(null);
        cache.Clear();

        // The texture pool: ReleaseUnusedSince(0) frees nothing; ReleaseFree everything free.
        var pool = gpu.Textures;
        var rented = Enumerable.Range(0, 3).Select(_ => pool.Rent(64, 64)).ToList();
        pool.EndFrame();
        pool.ReleaseUnusedSince(0);
        var afterZero = pool.FreeCount;
        var released = pool.ReleaseFree();
        Line(text, $"  the texture pool, three textures free: ReleaseUnusedSince(0) leaves {afterZero}, ReleaseFree leaves {pool.FreeCount} ({released / Kb} KB)");
        Expect(problems, "ReleaseUnusedSince(0) frees nothing", afterZero, 3);
        Expect(problems, "ReleaseFree frees every free texture", (pool.FreeCount, released), (0, 3 * 64 * 64 * 8L));
        GC.KeepAlive(rented);

        // The driver's Trim (Windows 8.1 and later: IDXGIDevice3).
        var trimmed = gpu.Trim();
        Line(text, $"  the device's Trim (IDXGIDevice3): {(trimmed ? "called" : "NOT")}");
        Expect(problems, "IDXGIDevice3::Trim is there", trimmed, true);

        ParkedSources(gpu, text, problems);
    }

    /// <summary>
    /// A source let go of and registered again (the TabPerf follow-up, the user's X1), on a real DecoderPool reading a source table of the
    /// test's own as the worker reads the client's: on a big card its serial is parked and taken back, with its frames, by the same file
    /// registered again (another id, the path's case aside); a file written again meanwhile, or one that cannot be read, is forgotten; on
    /// unified memory or a small card it is forgotten as before. The 5.4D-TabPerf review's P13: the stamps are read on the pool, never on
    /// the worker's thread; a file on a network share is never stamped nor parked; a stamp slow to come back holds nothing up, and past its
    /// grace the source takes a fresh serial.
    /// </summary>
    private static void ParkedSources(GpuDevice gpu, StringBuilder text, List<string> problems)
    {
        Line(text, $"a source let go of (a tab put behind) and registered again (the tab brought back), on a real decoder pool:");
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.WorkerMemory");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "parked.bin");
        File.WriteAllBytes(file, new byte[4096]);
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);

        using var shared = CompositorSharedMemory.Create(string.Create(CultureInfo.InvariantCulture, $"HandPeg.GoldenTests.Parked.{Guid.NewGuid():N}"));
        using var pool = new DecoderPool(gpu, shared);
        var generation = 0;
        void Set(int id, string path, bool sync = true)
        {
            // As CompositorClient.SetSource writes it.
            ref var entry = ref shared.Source(id);
            (entry.Kind, entry.StreamIndex, entry.IFrameCount, entry.PathHash) = ((int)GpuSourceKind.Media, 0, 0, CompositorSharedMemory.PathHash(path));
            CompositorSharedMemory.WriteString(entry.Path, path);
            Volatile.Write(ref entry.Generation, ++generation);
            Interlocked.Increment(ref shared.Control.SourcesVersion);
            if (sync)
                Sync();
        }

        // The worker's turn, and the stamps read on the pool (the 5.4D-TabPerf review's P13) let come back and settled.
        void Sync()
        {
            pool.SyncSources();
            if (!pool.StampsBackForTests(10_000))
                problems.Add("a file's stamp was not read within 10 s");
        }

        void Clear(int id, bool sync = true)
        {
            // As CompositorClient.ClearSource (GpuResourceBroker.ReleaseAll, a tab put behind).
            Volatile.Write(ref shared.Source(id).Generation, 0);
            Interlocked.Increment(ref shared.Control.SourcesVersion);
            if (sync)
                pool.SyncSources();
        }

        void Decoded(int serial)
        {
            foreach (var time in new[] { 0, 1 / 30.0 })
            {
                var frame = pool.Cache.Rent(64, 64, tenBit: false, software: true);
                (frame.Source, frame.Time, frame.Until, frame.Closed) = (serial, time, time + 1 / 30.0, true);
                pool.Cache.Insert(frame);
            }
        }

        bool Holds(int serial) => pool.Cache.Has(serial, 0) && pool.Cache.Has(serial, 1 / 30.0);

        // A big card.
        pool.Cache.BigCardForTests(true);
        Set(0, file);
        var first = pool.SerialOf(0);
        Decoded(first);
        Clear(0);
        var parked = Holds(first) && pool.ParkedCount == 1 && pool.SerialOf(0) == 0;
        Line(text, $"  a big card, the file let go of: {(parked ? "its frames kept, its serial parked" : "NOT")}");
        Expect(problems, "a big card keeps a let-go source's frames, parked", (Holds(first), pool.ParkedCount), (true, 1));
        Set(5, file.ToUpperInvariant());
        var back = pool.SerialOf(5);
        Line(text, $"  the same file registered again (another id, the path in capitals): {(back == first && Holds(back) && pool.ParkedCount == 0 ? "its serial taken back, its frames found" : "NOT")}");
        Expect(problems, "a re-registered source takes its serial back", back, first);
        Expect(problems, "a re-registered source finds its frames", (Holds(back), pool.ParkedCount), (true, 0));

        // The same id given the same file again (a new generation): retired, parked and taken back in one reading.
        Set(5, file);
        Line(text, $"  the same file given its id again: {(pool.SerialOf(5) == first && Holds(first) ? "the same serial, its frames found" : "NOT")}");
        Expect(problems, "the same file at the same id again", (pool.SerialOf(5), Holds(first)), (first, true));

        // Let go of at one id and registered at a lower one in the same change (a tab put behind and the next, which shows it too, brought
        // to the front before the worker's next turn): every entry let go of is retired before a new one is read.
        Clear(5, sync: false);
        Set(1, file, sync: false);
        Sync();
        Line(text, $"  let go of at one id and registered at a lower one in the same change: {(pool.SerialOf(1) == first && Holds(first) && pool.ParkedCount == 0 ? "its serial taken back, its frames found" : "NOT")}");
        Expect(problems, "a file moved to a lower id in one change", (pool.SerialOf(1), Holds(first), pool.ParkedCount), (first, true, 0));
        Clear(1);

        // Written again while let go of: forgotten, a new serial.
        File.SetLastWriteTimeUtc(file, written.AddDays(1));
        Set(2, file);
        var changed = pool.SerialOf(2);
        Line(text, $"  the file written again while let go of: {(changed != first && changed > 0 && !Holds(first) && pool.ParkedCount == 0 ? "its old frames forgotten, a new serial" : "NOT")}");
        Expect(problems, "a file written again is not taken back", (changed != first, Holds(first), pool.ParkedCount), (true, false, 0));

        // A file that cannot be read (gone): never parked.
        var gone = Path.Combine(folder, "gone.bin");
        Set(7, gone);
        var goneSerial = pool.SerialOf(7);
        Decoded(goneSerial);
        Clear(7);
        Line(text, $"  a file that cannot be read, let go of: {(!Holds(goneSerial) && pool.ParkedCount == 0 ? "forgotten" : "NOT")}");
        Expect(problems, "a file that cannot be read is forgotten", (Holds(goneSerial), pool.ParkedCount), (false, 0));

        // The 5.4D-TabPerf review's P13: no file is read on the worker's thread, nor under the pool's lock. A file on a network share is
        // never stamped (a sleeping server held the worker for seconds), so never parked.
        var stampsBefore = pool.StampsRead.Count;
        Set(8, @"\\server\share\DVR\clip.mp4");
        var shareSerial = pool.SerialOf(8);
        Decoded(shareSerial);
        Clear(8);
        var shareKept = Holds(shareSerial) || pool.ParkedCount > 0;
        Line(text, $"  a file on a network share (\\\\server\\share), registered and let go of: {(pool.StampsRead.Count == stampsBefore && shareSerial > 0 && !shareKept ? "never stamped, never parked, forgotten" : "NOT")}");
        Expect(problems, "a file on a network share is never stamped", pool.StampsRead.Count, stampsBefore);
        Expect(problems, "a file on a network share is never parked", (shareSerial > 0, Holds(shareSerial), pool.ParkedCount), (true, false, 0));
        var unc = new[] { @"\\server\share\a.mp4", @"//server/share/a.mp4", @"\\?\UNC\server\share\a.mp4", @"\\?\C:\a.mp4", @"\\.\D:\a.mp4", @"C:\a.mp4", "" };
        var network = string.Join(", ", unc.Select(path => $"{(path.Length > 0 ? path : "(none)")} {(DecoderPool.IsNetworkPath(path) ? "network" : "local")}"));
        Line(text, $"  which paths are a network share's: {network}");
        Expect(problems, "the network paths", string.Join("|", unc.Select(DecoderPool.IsNetworkPath)), "True|True|True|False|False|False|False");

        // A local file's stamp is read on the pool; one that is slow to come back (a disk waking up) holds nothing: the worker's turn
        // returns at once, the source waits outside the table, and past its grace it takes a fresh serial, its parked frames forgotten.
        Set(9, file);
        var local = pool.SerialOf(9);
        var readOn = pool.StampsRead.Thread;
        Decoded(local);
        Clear(9);
        Line(text, $"  a local file's stamp: read on {(readOn != Environment.CurrentManagedThreadId && readOn > 0 ? "the pool, not the worker's thread" : "NOT the pool")}; let go of: {(pool.ParkedCount == 1 && Holds(local) ? "parked" : "NOT parked")}");
        Expect(problems, "a stamp read off the worker's thread", readOn != Environment.CurrentManagedThreadId && readOn > 0, true);
        Expect(problems, "a local file let go of is parked", (pool.ParkedCount, Holds(local)), (1, true));
        pool.StampDelayForTests(1500);
        var turn = Stopwatch.StartNew();
        Set(9, file, sync: false);
        pool.SyncSources();
        var turnMs = turn.Elapsed.TotalMilliseconds;
        var waiting = pool.SerialOf(9) == 0 && pool.ParkedCount == 0;
        Thread.Sleep(200);
        pool.SyncSources();
        var fresh = pool.SerialOf(9);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  note  worker-frame-cache: the worker's turn with a stamp 1.5 s slow took {turnMs:0.0} ms"));
        Line(text, $"  a stamp slow to come back: the worker's turn {(turnMs < 500 ? "returns at once" : "WAITS")}, the source {(waiting ? "waits outside the table" : "NOT waiting")}; past its grace {(fresh > 0 && fresh != local && !Holds(local) ? "a fresh serial, the parked frames forgotten" : "NOT")}");
        Expect(problems, "the worker's turn does not wait for a slow stamp", turnMs < 500, true);
        Expect(problems, "a source waiting for its stamp is out of the table", waiting, true);
        Expect(problems, "past its grace a fresh serial", (fresh > 0, fresh != local, Holds(local)), (true, true, false));
        pool.StampDelayForTests(0);
        if (!pool.StampsBackForTests(10_000))
            problems.Add("the slow stamp did not come back within 10 s");

        // Unified memory or a small card: forgotten as before.
        Decoded(changed);
        pool.Cache.BigCardForTests(false);
        Clear(2);
        var forgotten = !Holds(changed) && pool.ParkedCount == 0;
        Set(3, file);
        var renewed = pool.SerialOf(3);
        Line(text, $"  unified memory or a small card, the file let go of and registered again: {(forgotten && renewed != changed && !Holds(renewed) ? "its frames forgotten, a new serial" : "NOT")}");
        Expect(problems, "a small card forgets a let-go source", (Holds(changed), pool.ParkedCount, renewed != changed), (false, 0, true));
        pool.Cache.BigCardForTests(null);
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // The temp folder's.
        }
    }

    // ----- worker-trim -----

    private static void Trim(StringBuilder text, List<string> problems)
    {
        // The protocol: the new fields in the room left free, none over another.
        text.Append("the control block's new fields (5.4D-TabPerf), in the room 3120..3479 that was free:\n");
        (string Field, int Expected)[] fields =
        [
            (nameof(ControlBlock.TrimRequests), 3120), (nameof(ControlBlock.TrimsDone), 3124), (nameof(ControlBlock.WorkerPrivateBytes), 3128),
            (nameof(ControlBlock.CacheFreeBytes), 3136), (nameof(ControlBlock.ClearRequests), 3144), (nameof(ControlBlock.ClearsDone), 3148),
            (nameof(ControlBlock.CacheMaximumBytes), 3152), (nameof(ControlBlock.CacheCeilingBytes), 3160), (nameof(ControlBlock.CacheGrowSteps), 3168),
            (nameof(ControlBlock.IdleTrims), 3172),
        ];
        foreach (var (field, expected) in fields)
        {
            var at = (int)Marshal.OffsetOf<ControlBlock>(field);
            Line(text, $"  {field} at {at}");
            Expect(problems, $"the offset of {field}", at, expected);
        }

        var texturesEnd = (int)Marshal.OffsetOf<ControlBlock>(nameof(ControlBlock.TexturesMade)) + sizeof(int);
        var hitsAt = (int)Marshal.OffsetOf<ControlBlock>(nameof(ControlBlock.CacheHits));
        var newEnd = (int)Marshal.OffsetOf<ControlBlock>(nameof(ControlBlock.IdleTrims)) + sizeof(int);
        Line(text, $"  after TexturesMade (ends at {texturesEnd}), before CacheHits ({hitsAt}); the block still {Marshal.SizeOf<ControlBlock>()} bytes");
        Expect(problems, "the new fields start where TexturesMade ends", texturesEnd, 3120);
        Expect(problems, "the new fields end before CacheHits", newEnd <= hitsAt, true);
        Expect(problems, "the control block's size", Marshal.SizeOf<ControlBlock>(), CompositorProtocol.ControlBytes);

        // The idle rule (30 s, once, never with the Export Quality Preview on).
        var f = Stopwatch.Frequency;
        text.Append("the worker trims itself standing idle:\n");
        (string What, long Idle, bool Trimmed, bool Loopback, bool Expected)[] idle =
        [
            ("29 s with nothing asked", 29 * f, false, false, false),
            ("31 s with nothing asked", 31 * f, false, false, true),
            ("31 s, trimmed already since the last ask", 31 * f, true, false, false),
            ("31 s with the Export Quality Preview on", 31 * f, false, true, false),
        ];
        foreach (var i in idle)
        {
            var due = CompositorWorker.IdleTrimDue(1_000 * f + i.Idle, 1_000 * f, i.Trimmed, i.Loopback);
            Line(text, $"  {i.What}: {(due ? "trims" : "waits")}");
            Expect(problems, $"idle trim, {i.What}", due, i.Expected);
        }

        // An asked-for trim hands its memory back to Windows (the driver's Trim, the heap) only once the worker is left alone a moment
        // (the TabPerf follow-up): a run of quick tab switches pays for it once.
        text.Append("an asked-for trim hands its memory back to Windows (the driver's Trim, the heap):\n");
        (string What, long Quiet, bool Playing, bool Expected)[] giveBack =
        [
            ("0.1 s after the last scene (a quick switch to the next tab)", f / 10, false, false),
            ("0.25 s after it", f / 4, false, true),
            ("a minute after it, playing", 60 * f, true, false),
        ];
        foreach (var g in giveBack)
        {
            var due = CompositorWorker.GiveBackDue(1_000 * f + g.Quiet, 1_000 * f, g.Playing);
            Line(text, $"  {g.What}: {(due ? "now" : "waits")}");
            Expect(problems, $"give back, {g.What}", due, g.Expected);
        }

        // A real worker: 4K frames with blurs, read back (the pool's working textures, the readback's staging), then trimmed.
        const int W = 3840, H = 2160;
        using var client = new CompositorClient(adapter: -1, keepStandby: false);
        client.EnsureReadback((long)W * H * 4);
        var picture = new byte[(long)W * H * 4];
        long Draw(bool blur)
        {
            client.ClearScene();
            ref var scene = ref client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.OutputWidth, scene.OutputHeight) = (W, H, W, H);
            scene.Flags = FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagRawOutput | FrameSceneHeader.FlagReadback;
            for (var k = 0; k < 3; k++)
            {
                ref var layer = ref client.Layer(k);
                layer.SourceKind = (int)GpuSourceKind.TestPattern;
                layer.PatternKind = 1 + k % 2;
                (layer.X, layer.Y, layer.Width, layer.Height) = (k * 400, k * 200, W - k * 800, H - k * 400);
                (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
                (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
                (layer.CropRight, layer.CropBottom) = (1, 1);
                layer.Flags = GpuLayerDescriptor.FlagVisible;
                if (blur)
                    layer.Effects[layer.EffectCount++] = new GpuEffectPacket(GpuShaderSlot.BoxBlur, 8 + 4 * k, 0, 0, 0, 0, 0, 0, 0);
                client.Scene.ActiveLayerCount = k + 1;
            }

            var sequence = client.Publish();
            return client.TryReadback(sequence, picture, out _, out _, 20_000) ? sequence : -1;
        }

        var drawn = 0;
        for (var i = 0; i < 4; i++)
            drawn += Draw(blur: true) > 0 ? 1 : 0;
        if (drawn != 4)
        {
            problems.Add($"the worker drew {drawn} of 4 frames ({client.State}: {client.Message})");
            return;
        }

        var hashBefore = Hash(picture);

        // Its private bytes as it publishes them every 2 s: the newest after the frames.
        var clock = Stopwatch.StartNew();
        var published = client.WorkerPrivateBytes;
        while (clock.ElapsedMilliseconds < 4500 && client.WorkerPrivateBytes == published)
            Thread.Sleep(50);
        var before = client.WorkerPrivateBytes;

        // A trim with a Redraw left right after it (the supervisor's, after a recovery): served all the same.
        // A trim alone (the worker left alone already: its memory handed back at once): what it gives back.
        var asked = Stopwatch.StartNew();
        var served = client.WaitForTrim(client.TrimWorker(), 10_000);
        var servedMs = asked.Elapsed.TotalMilliseconds;
        var after = client.WorkerPrivateBytes;

        // A trim asked for right after a frame (a tab switched to), with a Redraw left after it (the supervisor's, after a recovery):
        // served all the same, its memory handed back to Windows once the worker has been left alone (CompositorWorker.GiveBackDue).
        var redrawn = Draw(blur: true) > 0;
        var right = Stopwatch.StartNew();
        var request = client.TrimWorker();
        client.Send(CompositorCommand.Redraw);
        var servedAgain = client.WaitForTrim(request, 10_000);
        var waited = right.Elapsed.TotalMilliseconds;
        Line(text, $"a worker that drew 4 frames of 3840 x 2160 with three blurred layers, each read back:");
        Line(text, $"  a trim with a Redraw sent right after it: {(servedAgain ? "served (its count echoed)" : "NOT served")}");
        Expect(problems, "the trim served despite a later Redraw", (served, servedAgain), (true, true));
        var deferred = redrawn && waited >= CompositorWorker.GiveBackQuietMs * 0.8;
        Line(text, $"  that trim asked for right after a frame: {(deferred ? "its memory handed back once the worker was left alone a quarter of a second" : "NOT deferred")}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        a trim asked for right after a frame served {waited:0} ms later"));
        if (!deferred)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"a trim asked for right after a frame was served {waited:0} ms later (frame drawn: {redrawn})"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        the worker's private bytes: {before / (double)Mb:0} MB before the trim, {after / (double)Mb:0} MB after ({(before - after) / (double)Mb:0} MB given back); the trim served {servedMs:0} ms after it was asked for"));
        var down = before > 0 && after > 0 && before - after >= 64 * Mb;
        Line(text, $"  its private bytes after the trim: {(down ? "down by at least 64 MB (the working textures and the readback's staging)" : "NOT down")}");
        if (!down)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the worker's private bytes went from {before / Mb} MB to {after / Mb} MB"));

        // A clear the same way.
        var cleared = client.ClearFrameCache();
        client.Send(CompositorCommand.Redraw);
        var clearServed = client.WaitForClear(cleared, 10_000);
        Line(text, $"  a clear with a Redraw sent right after it: {(clearServed ? "served" : "NOT served")}; the budget at its baseline: {(client.Memory.Control.CacheGrowSteps == 0 ? "yes" : "NOT")}");
        Expect(problems, "the clear served despite a later Redraw", clearServed, true);

        // The same picture after it: the pool, the readback's view and staging, and the device's state all made again.
        var again = Draw(blur: true) > 0;
        var same = again && Hash(picture) == hashBefore;
        Line(text, $"  the next frame after the trim: {(same ? "drawn and read back, the same picture as before it" : "NOT the same")}");
        if (!same)
            problems.Add(again ? "the frame after the trim differs from the one before it" : $"no frame after the trim ({client.State}: {client.Message})");

        // The memory published as the cache stands (a trim publishes it at once).
        var control = client.Memory.Control;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        cache: {control.CacheBytes / Mb} MB of {control.CacheBudgetBytes / Mb} MB (at most {control.CacheMaximumBytes / Mb} MB, ceiling {control.CacheCeilingBytes / Mb} MB), free list {control.CacheFreeBytes / Mb} MB"));
        Expect(problems, "the maximum is published", control.CacheMaximumBytes > 0 && control.CacheCeilingBytes >= control.CacheMaximumBytes, true);

        CachedFrames(client, text, problems);
        RendererTextures(text, problems);
    }

    /// <summary>
    /// The renderer's textures kept between frames (the TabPerf follow-up: SceneRenderer.ReleaseIdleTextures), on a real renderer in this
    /// process: a flat frame with a blend mode (the canvas copy) and an Explosion (its noise), a 3D frame (its picture and plane) and the
    /// loupe; the idle trim's release lets go of all but the noise (what carries a frame into the next), the asked-for one of that too;
    /// each frame drawn again after either is the same picture, and every texture is made again by the frame that wants it.
    /// </summary>
    private static unsafe void RendererTextures(StringBuilder text, List<string> problems)
    {
        const int W = 1280, H = 720;
        Line(text, $"the renderer's kept textures let go of by a trim and made again ({W} x {H}, a real renderer):");
        using var gpu = GpuDevice.Create(-1);
        using var shaders = new ShaderLibrary(gpu);
        using var shared = CompositorSharedMemory.Create(string.Create(CultureInfo.InvariantCulture, $"HandPeg.GoldenTests.Renderer.{Guid.NewGuid():N}"));
        using var decoders = new DecoderPool(gpu, shared);
        using var resources = new ResourceCache(gpu, shared);
        using var renderer = new SceneRenderer(gpu, shaders, decoders, resources);
        using var output = gpu.CreateTexture(W, H, DxgiFormat.R16G16B16A16Float, renderTarget: true);
        var staging = gpu.CreateStaging(W, H, DxgiFormat.R16G16B16A16Float);
        try
        {
            var layers = new GpuLayerDescriptor[2];
            for (var k = 0; k < layers.Length; k++)
            {
                ref var layer = ref layers[k];
                layer.SourceKind = (int)GpuSourceKind.TestPattern;
                layer.PatternKind = 1 + k;
                layer.ClipKey = 40 + k;
                (layer.X, layer.Y, layer.Width, layer.Height) = (k * 160, k * 90, W - k * 320, H - k * 180);
                (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
                (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
                (layer.CropRight, layer.CropBottom) = (1, 1);
                layer.Flags = GpuLayerDescriptor.FlagVisible;
            }

            // The top layer multiplied onto the one under it, with an Explosion on it.
            layers[1].BlendMode = 1;
            layers[1].Effects[layers[1].EffectCount++] = new GpuEffectPacket(GpuShaderSlot.Explosion, 0.4f, 0.6f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f);
            layers[1].Effects[layers[1].EffectCount++] = new GpuEffectPacket(GpuShaderSlot.Explosion, 0.5f, 0, 0, 0, 0, 0, 0, 0);

            var flat = new FrameSceneHeader { CanvasWidth = W, CanvasHeight = H, OutputWidth = W, OutputHeight = H, ActiveLayerCount = 2, PlayheadSeconds = 1 };
            var exploded = flat;
            (exploded.ViewMode, exploded.CameraYaw, exploded.CameraPitch, exploded.ExplodedZSpread) = (1, 20, 10, 200);
            var loupe = flat;
            (loupe.LoupeClipKey, loupe.LoupeKind, loupe.LoupeZoom) = (41, 1, 3);

            ulong Draw(in FrameSceneHeader header)
            {
                renderer.Render(header, layers, output.Rtv, W, H, raw: true);
                gpu.Context.CopyResource(staging, output.Texture);
                if (gpu.Context.Map(staging, 0, D3D11.MapRead, 0, out var mapped) < 0)
                    return 0;
                try
                {
                    var hash = 14695981039346656037UL;
                    for (var y = 0; y < H; y += 3)
                    {
                        var row = new ReadOnlySpan<byte>(mapped.Data + (long)y * mapped.RowPitch, W * 8);
                        for (var x = 0; x < row.Length; x += 13)
                            hash = (hash ^ row[x]) * 1099511628211UL;
                    }

                    return hash;
                }
                finally
                {
                    gpu.Context.Unmap(staging, 0);
                }
            }

            void Loupe()
            {
                renderer.Render(loupe, layers, output.Rtv, W, H, raw: true);
                if (renderer.PrepareLoupe(loupe, layers, W, H))
                    renderer.CompositeLoupe(output.Rtv, W, H);
            }

            // Drawn: the 3D view, the loupe (twice: its scan's staging is a ring of two), then the flat picture last.
            var threeD = Draw(exploded);
            Loupe();
            Loupe();
            var picture = Draw(flat);
            var held = renderer.IdleTexturesForTests();
            Line(text, $"  after a 3D frame, the loupe and a flat frame with a blend mode and an Explosion: {(held.Textures >= 6 && held.History >= 1 ? "the canvas copy, the 3D picture and plane, the loupe's textures and the noise held" : "NOT")}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        the renderer held {held.Textures} textures and {held.History} kept from frame to frame"));
            if (held.Textures < 6 || held.History < 1 || picture == 0 || threeD == 0)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"the renderer held {held.Textures} textures and {held.History} of history (pictures {picture:x}, {threeD:x})"));

            // The idle trim's release: all but what carries a frame into the next.
            renderer.ReleaseIdleTextures(history: false);
            var idle = renderer.IdleTexturesForTests();
            var flatAgain = Draw(flat);
            var threeDAgain = Draw(exploded);
            Line(text, $"  the idle trim's release: {(idle.Textures == 0 && idle.History == held.History ? "every one let go of but the noise" : "NOT")}; drawn again: {(flatAgain == picture && threeDAgain == threeD ? "the same flat and 3D pictures" : "NOT the same")}");
            Expect(problems, "the idle release", (idle.Textures, idle.History), (0, held.History));
            Expect(problems, "the flat picture after the idle release", flatAgain, picture);
            Expect(problems, "the 3D picture after the idle release", threeDAgain, threeD);

            // The asked-for trim's release: the noise too; drawn again, the same picture, every texture made again as it is wanted.
            renderer.ReleaseIdleTextures(history: true);
            var asked = renderer.IdleTexturesForTests();
            var flatAfter = Draw(flat);
            var threeDAfter = Draw(exploded);
            Loupe();
            Loupe();
            Draw(flat);
            var made = renderer.IdleTexturesForTests();
            Line(text, $"  the asked-for trim's release: {(asked == (0, 0) ? "every one let go of" : "NOT")}; drawn again: {(flatAfter == picture && threeDAfter == threeD ? "the same flat and 3D pictures" : "NOT the same")}; {(made == held ? "every texture made again" : "NOT made again")}");
            Expect(problems, "the asked-for release", asked, (0, 0));
            Expect(problems, "the flat picture after the asked-for release", flatAfter, picture);
            Expect(problems, "the 3D picture after the asked-for release", threeDAfter, threeD);
            Expect(problems, "the textures made again", made, held);
        }
        finally
        {
            Com.Release(ref staging);
        }
    }

    /// <summary>
    /// A tab put behind, as GpuPreviewEngine.Suspend does it to the worker (the TabPerf follow-up): four seconds of 1080p decoded into the
    /// cache, then the source cleared and the worker trimmed, no clear sent. The cache is emptied on unified memory or a small card and
    /// kept on a big card, where the same file registered again finds its frames with nothing decoded. Then Settings' Clear (a clear and a
    /// trim): its private bytes come down by at least half of what the frames held (on this PC's console: after the tab put behind, and
    /// after the clear).
    /// </summary>
    private static void CachedFrames(CompositorClient client, StringBuilder text, List<string> problems)
    {
        const int W = 1920, H = 1080, Frames = 120;
        Line(text, $"a tab put behind after {Frames} frames of {W} x {H} were decoded into the cache (the source cleared, the worker trimmed), then Settings' Clear:");
        var ffmpeg = GpuExportTests.FfmpegCandidates().FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            problems.Add("no FFmpeg (HANDPEG_FFMPEG) to make the clip");
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.WorkerMemory");
        Directory.CreateDirectory(folder);
        var clip = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"frames-{Guid.NewGuid():N}.mp4"));
        using (var make = Process.Start(new ProcessStartInfo(ffmpeg, $"-hide_banner -loglevel error -f lavfi -i \"testsrc2=s={W}x{H}:r=30:d={Frames / 30}\" -pix_fmt yuv420p -c:v libx264 -preset veryfast -g 30 -an -y \"{clip}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!)
        {
            var errors = make.StandardError.ReadToEnd();
            make.WaitForExit();
            if (make.ExitCode != 0)
            {
                problems.Add($"FFmpeg could not make the clip: {errors}");
                return;
            }
        }

        client.SetSource(0, clip);
        bool DrawFrame(int source, int i)
        {
            client.ClearScene();
            ref var scene = ref client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.OutputWidth, scene.OutputHeight) = (W, H, 640, 360);
            scene.Flags = FrameSceneHeader.FlagExactFrames;
            ref var layer = ref client.Layer(0);
            layer.SourceKind = (int)GpuSourceKind.Media;
            (layer.SourceId, layer.MediaSeconds, layer.ClipKey) = (source, (i + 0.5) / 30, 7);
            (layer.X, layer.Y, layer.Width, layer.Height) = (0, 0, W, H);
            (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
            (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
            (layer.CropRight, layer.CropBottom) = (1, 1);
            layer.Flags = GpuLayerDescriptor.FlagVisible;
            client.Scene.ActiveLayerCount = 1;
            return client.WaitForFrame(client.Publish(), 20_000);
        }

        var drawn = 0;
        for (var i = 0; i < Frames; i++)
            drawn += DrawFrame(0, i) ? 1 : 0;

        ref var control = ref client.Memory.Control;
        long Published(long seen)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 4500 && Volatile.Read(ref client.Memory.Control.WorkerPrivateBytes) == seen)
                Thread.Sleep(50);
            return Volatile.Read(ref client.Memory.Control.WorkerPrivateBytes);
        }

        var cached = Volatile.Read(ref control.CacheBytes);
        var cachedFrames = Volatile.Read(ref control.CachedFrames);
        var before = Published(client.WorkerPrivateBytes);

        // The tab put behind (GpuPreviewEngine.Suspend: GpuResourceBroker.ReleaseAll, then TrimWorker; no clear).
        bool big;
        using (var probe = GpuDevice.Create(-1))
        using (var probeCache = new FrameCache(probe))
            big = probeCache.BigCard;
        client.ClearSource(0);
        var asked = Stopwatch.StartNew();
        var suspended = client.WaitForTrim(client.TrimWorker(), 10_000);
        var suspendMs = asked.Elapsed.TotalMilliseconds;
        var afterSuspend = client.WorkerPrivateBytes;
        var keptFrames = Volatile.Read(ref control.CachedFrames);
        var asRule = big ? keptFrames == cachedFrames : keptFrames == 0;

        // On a big card the same file registered again (another id: the tab brought back) finds its frames: nothing decoded for them.
        var found = true;
        if (big)
        {
            client.SetSource(3, clip);
            var decodedBefore = client.DecodeCounters.Decoded;
            found = DrawFrame(3, Frames / 2) && DrawFrame(3, Frames / 2 + 1) && client.DecodeCounters.Decoded == decodedBefore;
            client.ClearSource(3);
        }

        // Settings' Clear: a clear and a trim.
        var cleared = client.WaitForClear(client.ClearFrameCache(), 10_000);
        var trimmed = client.WaitForTrim(client.TrimWorker(), 10_000);
        var afterTrim = client.WorkerPrivateBytes;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        {drawn} frames drawn, {cached / Mb} MB in the cache ({(big ? "a big card" : "unified memory or a small card")}): the worker's private bytes {before / Mb} MB, {afterSuspend / Mb} MB after the tab put behind ({keptFrames} frames kept, the trim served {suspendMs:0} ms after it was asked for), {afterTrim / Mb} MB after Settings' Clear (free list {Volatile.Read(ref control.CacheFreeBytes) / Mb} MB, {Volatile.Read(ref control.CachedFrames)} frames)"));
        Expect(problems, "every frame drawn", drawn, Frames);
        Expect(problems, "the tab put behind, the clear and the trim served", (suspended, cleared, trimmed), (true, true, true));
        var given = cached > 0 && before - afterTrim >= cached / 2;
        Line(text, $"  the frames decoded: {(cached >= Frames / 2 * (long)W * H * 3 / 2 ? "kept in the cache" : "NOT kept")}");
        Line(text, $"  the cache after the tab put behind: {(asRule ? "as the card's rule says (emptied on unified memory or a small card, every frame kept on a big card)" : "NOT")}");
        Line(text, $"  on a big card, the same file registered again: {(found ? "its frames found, nothing decoded (or not a big card)" : "NOT found")}");
        if (!asRule)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{keptFrames} of {cachedFrames} frames kept after the tab put behind ({(big ? "a big card" : "not a big card")})"));
        if (!found)
            problems.Add("on a big card, the file registered again did not find its frames");
        Line(text, $"  its private bytes after Settings' Clear: {(given ? "down by at least half of what the frames held" : "NOT down")}");
        Line(text, $"  the cache after it: {(Volatile.Read(ref control.CachedFrames) == 0 ? "no frame" : "NOT empty")}");
        if (cached < Frames / 2 * (long)W * H * 3 / 2)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"only {cached / Mb} MB of frames were kept in the cache"));
        if (!given)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the worker's private bytes went from {before / Mb} MB to {afterTrim / Mb} MB with {cached / Mb} MB of frames let go of"));
        try
        {
            File.Delete(clip);
        }
        catch (IOException)
        {
            // Still open in a decoder being let go of: the temp folder's.
        }
    }

    private static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037UL;
        for (var i = 0; i < bytes.Length; i += 61)
            hash = (hash ^ bytes[i]) * 1099511628211UL;
        return hash;
    }
}
