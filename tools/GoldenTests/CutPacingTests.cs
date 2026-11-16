using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp;
using HandPegApp.Compositor;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.6A (orchestrator; the user's Q1 b and Q2 b):
/// <para>cut-entry-decode-pacing: while playing, work ahead decodes within one budget of frames a second shared by the lanes (the Desktop's
/// 500 Hz trace: the swap chain's turns stalled once background decode passed ~150-200 fps); a cut that cannot make its moment within the
/// budget gets the rate it needs, the full speed only when that is near it; on a card of its own a cut into a file the playback reads is read as
/// far ahead as the hints reach (unified memory keeps 5.4.3's CutSoon); what is shown, read ahead or exported is never paced.</para>
/// <para>display-lead-seed: the saved Playback &amp; A/V Sync Test's display lead starts a player's first play when the refresh matches, as
/// four samples the real ones outvote after five, dropped when no real sample comes; never for the test's own engine.</para>
/// Every line says what it proved.
/// </summary>
public static class CutPacingTests
{
    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("cut-entry-decode-pacing",
            "# Background decode while playing: one shared budget of frames a second, a cut's own rate only when the budget cannot make it, cuts read earlier on a card of its own (5.6A).",
            Pacing);
        yield return Case("display-lead-seed",
            "# The saved Playback & A/V Sync Test's display lead seeds a player's first play when the refresh matches (5.6A, Q2 b).",
            Seed);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
    }

    private static void Pacing(Action<bool, string> expect, StringBuilder text)
    {
        const double full = 400;
        var budget = DecoderPool.PaceBudgetFps;
        expect(budget == 100 && DecoderPool.PaceMargin == 0.25, "the budget is 100 frames a second and a chase keeps 0.25 s in hand");
        expect(Environment.GetEnvironmentVariable("HANDPEG_DECODE_BUDGET_FPS") is not null || DecoderPool.BudgetFps == budget,
            "with no HANDPEG_DECODE_BUDGET_FPS the budget in effect is the default");

        static string Ms(double seconds) => (seconds * 1000).ToString("0.###", CultureInfo.InvariantCulture);

        var idle = DecoderPool.PaceInterval(0, double.NaN, budget, full);
        expect(Math.Abs(idle - 0.01) < 1e-12, $"work with no cut to make (a pre-roll, the pointer's) takes the budget's interval: {Ms(idle)} ms");

        var far = DecoderPool.PaceInterval(30, 2.75, budget, full);
        expect(Math.Abs(far - 0.01) < 1e-12, $"a cut 30 frames from its first, 2.75 s away, needs 12 fps: the budget's interval ({Ms(far)} ms)");

        var tight = DecoderPool.PaceInterval(226, 2.75, budget, full);
        expect(Math.Abs(tight - 1 / (226 / 2.5 * 1.25)) < 1e-12 && tight < 0.01,
            $"a cut 226 frames away at 2.75 s needs 90.4 fps: x1.25 is over the budget, so its own rate ({Ms(tight)} ms, ~113 fps)");

        var near = DecoderPool.PaceInterval(226, 1.25, budget, full);
        expect(Math.Abs(near - 1 / (226 * 1.25)) < 1e-12, $"the same cut 1.25 s away needs 226 fps (under 80 % of full speed): its own rate, {Ms(near)} ms");

        expect(DecoderPool.PaceInterval(300, 1.0, budget, full) == 0, "one that needs 80 % of the full speed or more runs at the full speed (no wait)");
        expect(DecoderPool.PaceInterval(10, 0.2, budget, full) == 0, "one whose margin is gone runs at the full speed");
        expect(DecoderPool.PaceInterval(226, 2.75 * 2, budget, full) == far || DecoderPool.PaceInterval(226, 5.5, budget, full) < 0.01,
            "the seconds left are what the caller gives (sequence seconds over the play rate): twice the time, half the rate");
        expect(DecoderPool.PaceInterval(10, double.NaN, budget, full) == idle, "a cut with no moment known takes the budget's interval");

        // The budget is shared: two lanes taking slots in turn decode at its rate together.
        long next = 0;
        var step = 10_000L;
        var slots = new List<long>();
        for (var i = 0; i < 20; i++)
            slots.Add(DecoderPool.TakeSlot(ref next, 0, step));
        var distinct = slots.Distinct().Count();
        expect(distinct == 20 && slots[^1] == 19 * step && slots.Zip(slots.Skip(1)).All(p => p.Second - p.First == step),
            "20 slots taken at once (two lanes in turn) are one interval apart: the lanes share the budget, not double it");
        long later = 0;
        DecoderPool.TakeSlot(ref later, 0, step);
        expect(DecoderPool.TakeSlot(ref later, 1_000_000, step) == 1_000_000, "a slot asked for after a pause is now: an idle budget is not owed");

        expect(DecoderPool.CutReadAhead(unifiedMemory: true, 1) == DecoderPool.CutSoon && DecoderPool.CutReadAhead(true, 4) == DecoderPool.CutSoon,
            "on unified memory a cut into a file the playback reads is read within CutSoon (1.5 s), at every rate (5.4.3's decoder-lock rule kept)");
        expect(DecoderPool.CutReadAhead(false, 1) == 3 && DecoderPool.CutReadAhead(false, 2) == 6 && DecoderPool.CutReadAhead(false, 0.25) == 3,
            "on a card of its own it is read as far as the hints reach: 3 s at 1x, 6 s at 2x, never less than 3 s");

        var pacing = SourceProbe.Src("Compositor", "DecoderPool.Pacing.cs");
        var lanes = SourceProbe.Src("Compositor", "DecoderPool.Lanes.cs");
        var pace = SourceProbe.Member(pacing, "private void Pace(Demand demand, MfVideoDecoder decoder)");
        expect(pace.Contains("if (!_lastWasPlaying || _exactOnly || demand.Kind is DemandKind.Visible or DemandKind.PlayAhead || ServesPlayback(demand, decoder))\n            return;", StringComparison.Ordinal),
            "nothing is paced but work ahead while playing: never a frame shown or read ahead of it, nor a read one waits on, never an exact-frame scene (exports, captures)");
        expect(SourceProbe.InOrder(SourceProbe.Member(pacing, "private bool ServesPlayback(Demand demand, MfVideoDecoder decoder)"), "lock (_gate)",
                   "d.Kind is DemandKind.Visible or DemandKind.PlayAhead && d.Lane < 0 && !d.Done && !d.Cancelled && d.Source == demand.Source",
                   "Delivers(demand, decoder, d.Target)", "return true;"),
            "5.6A review: a read a frame shown or played ahead waits on (one no lane reads, which this lane's read delivers: a pre-roll on a lane when play began) runs unpaced, before the budget and the 5.0 slices alike");
        expect(SourceProbe.InOrder(pace, "if (BudgetFps <= 0)", "if (Paced(demand, decoder))", "WaitForNextDraw();"),
            "HANDPEG_DECODE_BUDGET_FPS=0 brings back the 5.0 slices (the A/B runs' control)");
        expect(SourceProbe.InOrder(pace, "if (interval < 1 / BudgetFps - 1e-9)", "slot = Math.Max(now, _ownNext);", "slot = TakeSlot(ref _paceNext, now, step);", "PaceWait(Math.Min(wait, MostPaceWait));"),
            "a cut paced faster than the budget keeps its own clock; the rest take the shared budget's slots; a wait is at most 100 ms before the lane looks again");
        expect(SourceProbe.Member(pacing, "private unsafe void PaceWait(double seconds)").Contains("Kernel32.HighResolutionTimer", StringComparison.Ordinal)
               && SourceProbe.Member(pacing, "private unsafe void PaceWait(double seconds)").Contains("_stopping.SafeWaitHandle.DangerousGetHandle()", StringComparison.Ordinal),
            "a lane waits on a high-resolution waitable timer (not the 15.6 ms tick) and wakes when the pool stops");
        expect(SourceProbe.InOrder(SourceProbe.Member(lanes, "private void WorkOn("), "Interlocked.Increment(ref _decoded);", "Pace(demand, decoder);")
               && !lanes.Contains("WaitForNextDraw();", StringComparison.Ordinal),
            "each frame a lane decodes is followed by Pace (the slice rule no longer called directly)");
        expect(SourceProbe.Member(lanes, "private Demand? MostUrgent(long pick)").Contains("!(demand.At - _lastPlayhead <= CutReadAhead(_gpu.UnifiedMemory, Volatile.Read(ref _playRate))) && PlaybackReads(demand.Source)", StringComparison.Ordinal),
            "MostUrgent's same-file gate reads as far ahead as CutReadAhead says");
        expect(SourceProbe.InOrder(SourceProbe.Member(SourceProbe.Src("Compositor", "DecoderPool.cs"), "public void Dispose()"), "if (!joined)\n            return;", "ClosePaceTimers();"),
            "the lanes' timers are closed with the pool, once its lanes stopped");
    }

    private static void Seed(Action<bool, string> expect, StringBuilder text)
    {
        static string Show(double s) => double.IsNaN(s) ? "none" : (s * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " ms";

        var cases = new (double? Ms, bool Stats, double Hz, double Period, double Expected, string What)[]
        {
            (5.811, true, 500, 1 / 500.0, 0.005811, "the Desktop's 500 Hz test on its 500 Hz monitor"),
            (5.811, true, 499.5, 1 / 500.0, 0.005811, "a refresh DWM gives as 499.5 Hz against EnumDisplaySettings' 500"),
            (16.4, true, 59.94, 1 / 60.0, 0.0164, "59.94 Hz against 60 Hz"),
            (5.811, true, 144, 1 / 500.0, double.NaN, "a test made on another refresh (144 Hz)"),
            (null, true, 500, 1 / 500.0, double.NaN, "no lead measured"),
            (5.811, false, 500, 1 / 500.0, double.NaN, "a test without real frame statistics (DWM's grid assumed)"),
            (0, true, 500, 1 / 500.0, double.NaN, "a lead of 0"),
            (150, true, 500, 1 / 500.0, double.NaN, "a lead over 100 ms"),
            (double.NaN, true, 500, 1 / 500.0, double.NaN, "a lead not a number"),
            (5.811, true, 500, double.NaN, double.NaN, "a monitor whose refresh is not known"),
            (5.811, true, 0, 1 / 500.0, double.NaN, "a test whose refresh was not known (0)"),
        };
        foreach (var c in cases)
        {
            var seed = GpuViewport.SeedLeadFor(c.Ms, c.Stats, c.Hz, c.Period);
            var holds = double.IsNaN(c.Expected) ? double.IsNaN(seed) : Math.Abs(seed - c.Expected) < 1e-12;
            expect(holds, $"{c.What}: {Show(seed)}");
        }

        expect(GpuViewport.SeedCopies == 4 && GpuViewport.SeedExpiryPresents == 60,
            "a seed counts as four samples, and is dropped after 60 new frames with no real sample");
        var one = GpuViewport.SeededMedian(0.005811, [0.0062]);
        expect(Math.Abs(one - 0.005811) < 1e-12, $"one real sample after the seed: the published lead is still the seed's ({Show(one)})");
        var five = GpuViewport.SeededMedian(0.005811, [0.0062, 0.0061, 0.0063, 0.0062, 0.0060]);
        expect(five >= 0.0060 - 1e-12, $"five real samples outvote the four seeds: {Show(five)}");
        var backlog = GpuViewport.SeededMedian(0.005811, [0.0198]);
        expect(Math.Abs(backlog - 0.005811) < 1e-12, $"a cold play's backed-up first sample (19.8 ms, the 11-13 overshoot) no longer moves the lead: {Show(backlog)}");

        var viewport = SourceProbe.Src("Services", "GpuViewport.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(viewport, "public void SeedLead(double seconds)"), "if (!double.IsFinite(seconds) || _leadCount > 0)", "_leads[i] = seconds;",
                   "(_leadAt, _leadCount, _seedLeft) = (SeedCopies, SeedCopies, SeedExpiryPresents);", "Volatile.Write(ref _displayLead, seconds);"),
            "SeedLead only starts an unmeasured lead, as SeedCopies samples, and publishes it at once");
        expect(SourceProbe.InOrder(SourceProbe.Member(viewport, "private void MeasureLead()"), "if (id != 0 && FrameOrigin != 0)", "if (_seedLeft > 0 && --_seedLeft == 0 && _leadCount <= SeedCopies)", "ForgetSeed();",
                   "_leadCount = Math.Min(_leadCount + 1, LeadSamples);", "_seedLeft = 0;"),
            "a new frame counts the seed down; a real sample ends the countdown; with none after 60 the seed is forgotten (the lead unknown again)");

        var engine = SourceProbe.Src("Services", "GpuPreviewEngine.cs");
        var seedFile = SourceProbe.Src("Services", "GpuPreviewEngine.LeadSeed.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(engine, "private void Pump()"), "if (!playBegun)", "monitorPeriod = _viewport.MonitorRefreshSeconds;",
                   "_refresh = GpuViewport.StartRefresh(_refresh, monitorPeriod);", "SeedLeadOnce(monitorPeriod);"),
            "the pump seeds at a play's first turn, once the monitor's refresh is read");
        expect(SourceProbe.InOrder(SourceProbe.Member(seedFile, "private void SeedLeadOnce(double monitorPeriod)"), "if (!_player || _leadSeedTried)", "_leadSeedTried = true;",
                   "AppSettings.Current.LastPlaybackSyncTest", "GpuViewport.SeedLeadFor(", "_viewport.SeedLead(seed);"),
            "only a player engine seeds, only at its first play, from the saved test, through SeedLeadFor's rule");
        expect(engine.Contains("(_client, _audio, _player) = (client, audio, player);", StringComparison.Ordinal)
               && SourceProbe.Src("Services", "PlaybackSyncTest.cs").Contains("player: false", StringComparison.Ordinal),
            "the Playback & A/V Sync Test's own engine (player: false) is never seeded");
    }

    // ----- Helpers -----

}
