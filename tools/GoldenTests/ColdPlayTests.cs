using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.5A lane A (the user's Q4 b): for the first second of a cold play (after a tab switch or a cold seek), cut-entry reads more than CutSoon ahead wait.
/// <para>cold-play-cut-holdback: the rule as a pure function (DecoderPool.ColdPlayHoldsBack, the time injected as Stopwatch ticks), the window
/// armed, opened and closed in a real decoder pool on this PC's card (new sources, a cold seek, a warm one, a pause, an exact-frame scene),
/// and the source of the lanes' picks and give-way. Every line says what it proved; no line prints a time measured here.</para>
/// </summary>
public static class ColdPlayTests
{
    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("cold-play-cut-holdback", "# For the first second of a cold play (new sources, a seek that missed), the cuts more than CutSoon ahead wait: the playhead's own frames first.", ColdPlay);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
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
            run(Expect, text);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static void ColdPlay(Action<bool, string> expect, StringBuilder text)
    {
        Rule(expect);
        Pool(expect);
        Sources(expect);
    }

    // ----- 1. The rule (pure) -----

    private static void Rule(Action<bool, string> expect)
    {
        expect(DecoderPool.ColdPlaySeconds == 1.0 && DecoderPool.CutSoon == 1.5, "the window is 1 s of playback (ColdPlaySeconds) and the cuts within 1.5 s of the playhead (CutSoon) are never held");
        var f = Stopwatch.Frequency;
        var until = 11 * f;
        (string What, bool Playing, long Now, long Until, double At, double Playhead, bool Held)[] rows =
        [
            ("playing 0.2 s into the window, a cut 2.0 s ahead", true, 10 * f + f / 5, until, 12.0, 10.0, true),
            ("a cut exactly 1.5 s ahead", true, 10 * f + f / 5, until, 11.5, 10.0, false),
            ("a cut 1.0 s ahead", true, 10 * f + f / 5, until, 11.0, 10.0, false),
            ("a cut behind the playhead", true, 10 * f + f / 5, until, 9.0, 10.0, false),
            ("the window's end reached (now == until)", true, until, until, 12.0, 10.0, false),
            ("paused", false, 10 * f + f / 5, until, 12.0, 10.0, false),
            ("no window (until 0)", true, 10 * f + f / 5, 0, 12.0, 10.0, false),
            ("a playhead not known (NaN)", true, 10 * f + f / 5, until, 12.0, double.NaN, false),
            ("a loop's cut shifted past the loop's end (62 against a playhead at 59.5)", true, 10 * f + f / 5, until, 62.0, 59.5, true),
        ];
        foreach (var (what, playing, now, end, at, playhead, held) in rows)
            expect(DecoderPool.ColdPlayHoldsBack(playing, now, end, at, playhead, 1) == held, F($"{what}: {(held ? "held back" : "read")}"));

        // 5.5A review (A3-2): the lead is real time, CutSoon x the play rate in Sequence seconds, never less than CutSoon.
        (string What, double At, double Rate, bool Held)[] rated =
        [
            ("at 1x, a cut 2.0 s ahead (2.0 s of real time)", 12.0, 1, true),
            ("at 1x, a cut 1.5 s ahead", 11.5, 1, false),
            ("at 2x, a cut 2.0 s ahead (1.0 s of real time)", 12.0, 2, false),
            ("at 2x, a cut exactly 3.0 s ahead (1.5 s of real time)", 13.0, 2, false),
            ("at 2x, a cut 3.5 s ahead (1.75 s of real time)", 13.5, 2, true),
            ("at 0.5x, a cut 2.0 s ahead (never less than CutSoon)", 12.0, 0.5, true),
            ("at 0.5x, a cut 1.5 s ahead", 11.5, 0.5, false),
            ("a rate not known (NaN)", 12.0, double.NaN, false),
        ];
        foreach (var (what, at, rate, held) in rated)
            expect(DecoderPool.ColdPlayHoldsBack(true, 10 * f + f / 5, until, at, 10.0, rate) == held, F($"{what}: {(held ? "held back" : "read")}"));

        (string What, double Moved, double Seconds, double Rate)[] rates =
        [
            ("0.2 s of the Sequence in 0.1 s of the clock", 0.2, 0.1, 2),
            ("0.1 s in 0.1 s", 0.1, 0.1, 1),
            ("0.025 s in 0.1 s (0.25x)", 0.025, 0.1, 0.25),
            ("a loop's wrap (-5 s in 0.1 s): the slowest", -5, 0.1, 0.25),
            ("a stall's catch-up (2 s in 0.1 s): the fastest", 2, 0.1, 4),
            ("no time passed", 0.1, 0, 1),
            ("a playhead not known (NaN)", double.NaN, 0.1, 1),
        ];
        foreach (var (what, moved, seconds, rate) in rates)
        {
            var made = DecoderPool.PlayRateOf(moved, seconds);
            expect(Math.Abs(made - rate) < 1e-9, F($"PlayRateOf, {what}: {made:0.##}x"));
        }
    }

    // ----- 2. A real pool: armed, opened, closed -----

    private static void Pool(Action<bool, string> expect)
    {
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.ColdPlay");
        Directory.CreateDirectory(folder);
        var files = Enumerable.Range(0, 3).Select(i => Path.Combine(folder, F($"cold{i}.bin"))).ToArray();
        foreach (var file in files)
        {
            File.WriteAllBytes(file, new byte[4096]);
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        using var gpu = GpuDevice.Create(-1);
        using var shared = CompositorSharedMemory.Create(F($"HandPeg.GoldenTests.ColdPlay.{Guid.NewGuid():N}"));
        using var pool = new DecoderPool(gpu, shared);
        pool.Cache.BigCardForTests(false);
        var generation = 0;
        void Set(int id, string path)
        {
            // As CompositorClient.SetSource writes it, then the worker's turn and the stamps read on the pool.
            ref var entry = ref shared.Source(id);
            (entry.Kind, entry.StreamIndex, entry.IFrameCount, entry.PathHash) = ((int)GpuSourceKind.Media, 0, 0, CompositorSharedMemory.PathHash(path));
            CompositorSharedMemory.WriteString(entry.Path, path);
            Volatile.Write(ref entry.Generation, ++generation);
            Interlocked.Increment(ref shared.Control.SourcesVersion);
            pool.SyncSources();
            pool.StampsBackForTests(10_000);
        }

        void Clear(int id)
        {
            // As CompositorClient.ClearSource (a tab put behind), and the worker's turn.
            Volatile.Write(ref shared.Source(id).Generation, 0);
            Interlocked.Increment(ref shared.Control.SourcesVersion);
            pool.SyncSources();
        }

        var f = Stopwatch.Frequency;
        var frame = 0L;

        // A playing frame's BeginFrame bracketed by the clock: whether it opened a window of ColdPlaySeconds from that moment.
        bool OpensWindow(double playhead)
        {
            var before = Stopwatch.GetTimestamp();
            pool.BeginFrame(++frame, playing: true, playhead);
            var after = Stopwatch.GetTimestamp();
            var (until, armed) = pool.ColdPlayForTests;
            return !armed && until >= before + f && until <= after + f;
        }

        // A fresh worker: its first sources are new, so its first play is cold.
        pool.BeginFrame(++frame, playing: false, 0);
        expect(pool.ColdPlayForTests is { Until: 0, Armed: true }, "a fresh pool, paused: its first sources arm a cold play; no window while paused");
        expect(OpensWindow(0.0167), "its first playing frame opens the window: 1 s from that frame (ColdPlaySeconds), the arm spent");
        pool.BeginFrame(++frame, playing: true, 0.0333);
        var (held, _) = pool.ColdPlayForTests;
        pool.BeginFrame(++frame, playing: false, 0.0333);
        expect(held != 0 && pool.ColdPlayForTests is { Until: 0, Armed: false }, "the next playing frame keeps the window; a pause closes it at once");
        pool.BeginFrame(++frame, playing: true, 0.0333);
        expect(pool.ColdPlayForTests is { Until: 0, Armed: false }, "playing again with nothing new (warm): no window");

        // A tab switch while paused: the tab behind's sources cleared, the front tab's registered.
        pool.BeginFrame(++frame, playing: false, 0.05);
        Set(0, files[0]);
        Clear(0);
        Set(1, files[1]);
        expect(pool.ColdPlayForTests is { Until: 0, Armed: true }, "a tab switch while paused (sources cleared, others registered): armed, the window waits for the play");
        pool.BeginFrame(++frame, playing: false, 0.05);
        expect(pool.ColdPlayForTests is { Until: 0, Armed: true }, "a paused frame after it keeps the arm (a pause never spends it)");
        expect(OpensWindow(0.0667), "the play after the switch opens the window from its first frame");
        pool.BeginFrame(++frame, playing: false, 0.0667);

        // A warm seek: the frame at the new moment is in the cache, so the jump's first picture is exact; nothing is armed.
        var serial = pool.SerialOf(1);
        var cached = pool.Cache.Rent(64, 64, tenBit: false, software: true);
        (cached.Source, cached.Time, cached.Until, cached.Closed) = (serial, 40.0, 40.0 + 1 / 30.0, true);
        pool.Cache.Insert(cached);
        pool.BeginFrame(++frame, playing: false, 40.001);
        var hit = pool.Resolve(1, 40.001, 0, 101) is not null && pool.LastFrameExact;
        pool.EndFrame();
        pool.BeginFrame(++frame, playing: false, 40.001);
        expect(hit && pool.ColdPlayForTests is { Until: 0, Armed: false }, "a warm seek (the jump's frame found in the cache): not armed");
        pool.BeginFrame(++frame, playing: true, 40.0177);
        expect(pool.ColdPlayForTests is { Until: 0, Armed: false }, "and the play after it opens no window");
        pool.BeginFrame(++frame, playing: false, 40.0177);

        // A cold seek: a new file (armed by the sources, spent by a play), then a jump whose first picture misses its frame.
        Set(2, files[2]);
        expect(OpensWindow(40.0177), "a file opened arms the pool and the next playing frame opens the window");
        pool.BeginFrame(++frame, playing: false, 40.0177);
        expect(pool.ColdPlayForTests is { Until: 0, Armed: false }, "paused again: the window closed, nothing armed");
        pool.BeginFrame(++frame, playing: false, 70);
        _ = pool.Resolve(2, 70, 0, 102);
        var missed = !pool.LastFrameExact;
        pool.EndFrame();
        expect(missed && pool.ColdPlayForTests is { Until: 0, Armed: false }, "a jump to 70 s whose picture misses its frame: not armed until the next frame looks back at it");
        pool.BeginFrame(++frame, playing: false, 70);
        expect(pool.ColdPlayForTests is { Until: 0, Armed: true }, "the next frame: the jump's picture missed, a cold seek: armed");

        // An exact-frame scene (an export, a capture) neither opens nor spends the window; the player's next play does.
        pool.BeginFrame(++frame, playing: true, 70, exactOnly: true);
        expect(pool.ColdPlayForTests is { Until: 0, Armed: true }, "an exact-frame scene, even one marked playing: no window opened, the arm kept");
        expect(OpensWindow(70.0167), "the player's next playing frame opens it");
        pool.BeginFrame(++frame, playing: false, 70.0167);

        // 5.5A review (A3-2): the play rate, sampled from the playhead's pace over the clock (the clock injected as Stopwatch ticks).
        expect(pool.PlayRateForTests == 1, "paused: the play rate is 1");
        var t = 1000 * f;
        long At(double seconds) => t + (long)Math.Ceiling(seconds * f - 1e-6);
        bool RateIs(double rate) => Math.Abs(pool.PlayRateForTests - rate) < 1e-4;
        pool.SamplePlayRate(playing: true, jumped: false, 10.0, At(0));
        pool.SamplePlayRate(playing: true, jumped: false, 10.1, At(0.05));
        var early = RateIs(1);
        pool.SamplePlayRate(playing: true, jumped: false, 10.2, At(0.1));
        expect(early && RateIs(2), "playing at 2x: still 1 before a sample's 0.1 s (PlayRateSampleSeconds) has run, then 2");
        pool.SamplePlayRate(playing: true, jumped: false, 10.3, At(0.2));
        expect(RateIs(1), "the speed set to 1x while playing: 1 within the next sample");
        pool.SamplePlayRate(playing: true, jumped: true, 30.0, At(0.25));
        var afterJump = RateIs(1);
        pool.SamplePlayRate(playing: true, jumped: false, 30.4, At(0.35));
        expect(afterJump && RateIs(4), "a jump while playing starts the sample again at 1; 0.4 s in 0.1 s then reads 4x (the fastest)");
        pool.SamplePlayRate(playing: false, jumped: false, 30.4, At(0.4));
        expect(RateIs(1), "a pause: 1 again");
        pool.SamplePlayRate(playing: true, jumped: false, 30.4, At(0.5));
        pool.SamplePlayRate(playing: true, jumped: false, 30.6, At(0.6));
        var beforeSources = RateIs(2);
        Clear(2);
        pool.SamplePlayRate(playing: true, jumped: false, 30.8, At(0.7));
        var restarted = RateIs(1);
        pool.SamplePlayRate(playing: true, jumped: false, 31.0, At(0.8));
        expect(beforeSources && restarted && RateIs(2), "new sources (a tab switch) start the sample again at 1, as a pause does; the next sample reads 2x again");
        pool.BeginFrame(++frame, playing: false, 31.0);
        expect(pool.PlayRateForTests == 1, "a paused BeginFrame resets the rate to 1 (BeginFrame samples every frame of the player)");
    }

    // ----- 3. The source: where the lanes skip and give way -----

    private static void Sources(Action<bool, string> expect)
    {
        var pool = Read("Compositor", "DecoderPool.cs");
        var lanes = Read("Compositor", "DecoderPool.Lanes.cs");
        expect(InOrder(Method(lanes, "private Demand? MostUrgent(long pick)"), "if (demand.Kind == DemandKind.Idle && !(idleMayRun ??= IdleMayRunNow(countLanes: true)))",
                   "if (HeldForColdPlay(demand))\n                continue;", "BackgroundBusy() >= BackgroundLanes"),
            "MostUrgent skips a held cut before any rank is weighed (it stays a demand: no frame taken, no decoder opened)");
        expect(InOrder(Method(lanes, "private void WorkOn("), "lock (_gate)", "if (HeldForColdPlay(demand))\n                    return;", "if (frames >= 4 && demand.Rank > 0 && MoreUrgentWaiting(demand, decoder))"),
            "a lane on a far cut gives way at once when a cold play opens (the read is taken up again where it stopped)");
        expect(InOrder(Method(lanes, "private bool MoreUrgentWaiting("), "if (HeldForColdPlay(d))\n                continue;", "return true;"),
            "a held cut never counts as waiting, so nothing gives way to it");
        expect(InOrder(Method(pool, "public void BeginFrame("), "var jumped = ", "if (!exactOnly)", "_coldArmed |= _jumpProbe && !LastFrameExact;", "_jumpProbe = jumped;",
                   "Volatile.Write(ref _coldPlayUntil, 0);", "Volatile.Write(ref _coldPlayUntil, Stopwatch.GetTimestamp() + (long)(ColdPlaySeconds * Stopwatch.Frequency));", "LastFrameExact = true;"),
            "BeginFrame arms on a jump whose picture missed (before LastFrameExact is reset), closes the window on a pause and opens it on the first playing frame");
        expect(InOrder(Method(pool, "public void SyncSources()"), "if (version != _sourcesVersion)", "_coldArmed = true;", "Resync();"),
            "SyncSources arms a cold play whenever the sources change (a tab switch, a file opened, a fresh worker)");
        expect(Method(pool, "private bool HeldForColdPlay(Demand demand)").Contains("demand.HintKind != GpuSeekHintKind.CutEntry || _exactOnly", StringComparison.Ordinal)
               && !Method(pool, "private bool HeldForColdPlay(Demand demand)").Contains("UnifiedMemory", StringComparison.Ordinal),
            "only cut entries are held, never in an exact-frame scene, and on every memory tier (no unified-memory gate)");

        // 5.5A review (A3-1): a cut held while the lane waited for the draw gets no decoder opened and no seek.
        expect(InOrder(Method(lanes, "private void RunLane(int lane)"), "WaitWhileDrawing();", "if (demand.Kind == DemandKind.Idle)", "lock (_gate)", "held = HeldForColdPlay(demand);",
                   "if (held)\n                            demand.Source.Opening = Math.Max(demand.Source.Opening - 1, 0);", "if (held)\n                        continue;", "decoder = OpenFor(demand.Source, lane);"),
            "RunLane looks again under the lock after waiting for the draw: a held cut opens no decoder (the open it counted on undone, the demand left to a later pick)");
        expect(InOrder(Method(lanes, "private void WorkOn("), "WaitWhileDrawing();", "lock (_gate)", "if (HeldForColdPlay(demand))\n                return;", "decoder.Prepare(StartOf(demand, decoder, target));"),
            "WorkOn looks under the lock before its first Prepare: a held cut is never sought on a decoder it was given");

        // 5.5A review (A3-2): the lead follows the play rate BeginFrame samples.
        expect(Method(pool, "private bool HeldForColdPlay(Demand demand)").Contains("_lastPlayhead, Volatile.Read(ref _playRate))", StringComparison.Ordinal)
               && Method(pool, "internal static bool ColdPlayHoldsBack(").Contains("cutAt - playhead > CutSoon * Math.Max(rate, 1)", StringComparison.Ordinal),
            "HeldForColdPlay holds a cut only more than CutSoon x the play rate ahead (never less than CutSoon)");
        expect(InOrder(Method(pool, "public void BeginFrame("), "var arrived = Stopwatch.GetTimestamp();", "SyncSources();", "if (!exactOnly)", "SamplePlayRate(playing, jumped, playhead, arrived);")
               && InOrder(Method(pool, "public void SyncSources()"), "if (version != _sourcesVersion)", "_rateRestart = true;"),
            "BeginFrame samples the rate with the clock read as the scene came (before any wait), never in an exact-frame scene; new sources start the sample again");
    }

    // ----- Source text -----

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), "src", .. parts])).ReplaceLineEndings("\n");

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HandPegApp.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("the repository (HandPegApp.csproj) above " + AppContext.BaseDirectory);
    }

    /// <summary>A member's text: from its signature to the end of its body (the next line that closes at its indent).</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..(end + 6)];
    }

    /// <summary>Whether every one of <paramref name="parts"/> is in <paramref name="text"/>, each after the one before.</summary>
    private static bool InOrder(string text, params string[] parts)
    {
        var at = 0;
        foreach (var part in parts)
        {
            var found = text.IndexOf(part, at, StringComparison.Ordinal);
            if (found < 0)
                return false;
            at = found + part.Length;
        }

        return true;
    }
}
