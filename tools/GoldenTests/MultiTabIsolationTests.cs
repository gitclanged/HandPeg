using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4.4 (lane A): tabs behind that cost the front tab's playback nothing: lean parking of a tab put behind (only the frames at its playhead
/// kept on unified memory or on battery), tab pre-roll as idle work that never runs while the front tab plays, scrubs or waits, pre-rolled sources
/// kept across the switch, and the overview and filmstrip workers paused while playing or scrubbing (multi-tab-unified-memory-isolation and its
/// neighbours).
/// <para>multi-tab-unified-memory-isolation: a real decoder pool on this PC's card, told to park lean (its goldens' word, never this PC's
/// hardware): a tab put behind keeps exactly the frame its playhead showed (the next one too when it was drawn playing), the tab brought back
/// finds it; parked as before when the client asks for nothing; the kept frames held to their cap, the oldest tab's first; a tab's pre-roll
/// becomes idle work, and the idle rule as a pure function.</para>
/// <para>multi-tab-preroll-kept: the resource broker keeps the sources a tab's pre-roll named across the switch and lets go of those the next
/// plan does not use. multi-tab-strips-pause: background work stops between its pieces while the player plays or a scrub holds it back, and
/// goes on when that is over. multi-tab-resume-working-set: a real worker keeps its working set across a tab switch (a trim followed by a
/// scene) and gives it back after a trim alone.</para>
/// <para>frame-latency-turns (5.4.4, the playback slowdown): every frame the player's swap chain queues takes a turn of its frame-latency
/// object (FrameLatencyTurns, driven here by a semaphore as DXGI drives the object), so the paused pump's presents no longer let the playing
/// pump run ahead of the display.</para>
/// </summary>
public static class MultiTabIsolationTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("multi-tab-unified-memory-isolation", "# A tab put behind on unified memory or on battery keeps only its playhead's frames; its pre-roll is idle work.", LeanParking);
        yield return Case("multi-tab-preroll-kept", "# The sources a tab's pre-roll named stay across the switch it was made for; the next plan lets go of the rest.", PreRollKept);
        yield return Case("multi-tab-strips-pause", "# Background work drawn in pieces stops between them while the player plays or a scrub is held, and goes on after.", StripsPause);
        yield return Case("multi-tab-resume-working-set", "# A worker keeps its working set across a tab switch, and gives it back after a trim with nothing in front.", ResumeWorkingSet);
        yield return Case("multi-tab-kept-share", "# Frames kept for tabs behind hold only a share of the budget as it is now: a budget that falls leaves the front tab its room.", KeptShareOfBudget);
        yield return Case("multi-tab-preroll-after-switch", "# The first plan after a switch lets go only of the kept sources nothing asked for since; a pre-roll or a drop made after it stays.", PreRollAfterSwitch);
        yield return Case("multi-tab-strips-resume-limit", "# Background work stopped between its pieces goes on within its lane's limit after a play, before work not started yet.", StripsResumeLimit);
        yield return Case("frame-latency-turns", "# Every frame the player queues takes a turn of its swap chain's frame-latency object, the paused pump's too: no pause lets the playing pump run ahead of the display.", LatencyTurns);
    }

    /// <summary>The cases that need the WPF Application (run after UiScaleTests made it).</summary>
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

    // ----- multi-tab-unified-memory-isolation -----

    private static void LeanParking(Action<bool, string> expect, StringBuilder text)
    {
        // The protocol's fields for it (the room 3212..3479 that was free), the hint kind, and the client's word.
        expect(Marshal.OffsetOf<ControlBlock>(nameof(ControlBlock.UnifiedMemory)) == 3212 && Marshal.OffsetOf<ControlBlock>(nameof(ControlBlock.TabParkingRequest)) == 3216
               && Marshal.SizeOf<ControlBlock>() == CompositorProtocol.ControlBytes && CompositorProtocol.Version == 8,
            "ControlBlock.UnifiedMemory at 3212 and TabParkingRequest at 3216, the block's size and the protocol's version as they were");
        expect((int)GpuSeekHintKind.TabRoll == 6, "a tab's pre-roll and a drop's file are their own hint kind (TabRoll, 6)");
        var engine = Read("Services", "GpuPreviewEngine.cs");
        expect(engine.Contains("client.TabParking = 1;", StringComparison.Ordinal), "the player's engine asks its worker (and the standby, which shares the block) to park lean where it should");

        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.MultiTab");
        Directory.CreateDirectory(folder);
        var files = Enumerable.Range(0, 10).Select(i => Path.Combine(folder, F($"tab{i}.bin"))).ToArray();
        foreach (var file in files)
        {
            File.WriteAllBytes(file, new byte[4096]);
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        using var gpu = GpuDevice.Create(-1);
        using var shared = CompositorSharedMemory.Create(F($"HandPeg.GoldenTests.MultiTab.{Guid.NewGuid():N}"));
        using var pool = new DecoderPool(gpu, shared);
        pool.Cache.BigCardForTests(false);
        pool.Cache.ScaleForTests(512L << 20, 512L << 20, 64L << 20, Stopwatch.GetTimestamp);
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
            // As CompositorClient.ClearSource (GpuResourceBroker.ReleaseAll: a tab put behind), and the worker's turn.
            Volatile.Write(ref shared.Source(id).Generation, 0);
            Interlocked.Increment(ref shared.Control.SourcesVersion);
            pool.SyncSources();
        }

        void Decoded(int serial, int frames, int width = 64, int height = 64)
        {
            for (var i = 0; i < frames; i++)
            {
                var frame = pool.Cache.Rent(width, height, tenBit: false, software: true);
                (frame.Source, frame.Time, frame.Until, frame.Closed) = (serial, i / 30.0, (i + 1) / 30.0, true);
                pool.Cache.Insert(frame);
            }
        }

        int Count(int serial, int frames) => Enumerable.Range(0, frames).Count(i => pool.Cache.Has(serial, i / 30.0 + 0.001));
        var at4 = 4 / 30.0 + 0.001;

        // Lean, paused: ten frames decoded, the layer showed the fifth; the tab put behind keeps that one alone.
        pool.LeanForTests(true);
        Set(0, files[0]);
        var first = pool.SerialOf(0);
        Decoded(first, 10);
        pool.ShownForTests(0, false, (11, at4));
        Clear(0);
        var keptOne = Count(first, 10) == 1 && pool.Cache.Has(first, at4) && pool.ParkedCount == 1 && pool.Cache.KeptBytes == 64 * 64 * 3 / 2;
        expect(keptOne, "lean, a tab put behind paused: of its ten frames only the one its playhead showed is kept, parked under its file");

        // The tab brought back: its file registered again (another id) takes the serial back, and the first scene finds the exact frame.
        Set(3, files[0]);
        expect(pool.SerialOf(3) == first && pool.HasExact(3, at4) && pool.ParkedCount == 0 && pool.Cache.KeptBytes == 0,
            "the tab brought back: its serial taken back, the exact frame at its playhead found at once, its frames ordinary again");

        // Drawn playing: the frame shown and the next one (the paused playhead may be a frame on); two layers of one file each keep theirs.
        Decoded(first, 10);
        pool.ShownForTests(3, true, (11, 2 / 30.0 + 0.001), (12, 7 / 30.0 + 0.001));
        Clear(3);
        expect(Count(first, 10) == 4 && pool.Cache.Has(first, 2 / 30.0 + 0.001) && pool.Cache.Has(first, 3 / 30.0 + 0.001) && pool.Cache.Has(first, 7 / 30.0 + 0.001)
               && pool.Cache.Has(first, 8 / 30.0 + 0.001),
            "drawn playing, two layers of one file: each layer's frame and the one after it are kept (four of ten)");

        // The client asks for nothing (strips, exports, tests' workers): forgotten on unified memory or a small card, as before.
        pool.LeanForTests(null);
        Volatile.Write(ref shared.Control.TabParkingRequest, 0);
        Set(4, files[1]);
        var plain = pool.SerialOf(4);
        Decoded(plain, 10);
        pool.ShownForTests(4, false, (13, at4));
        Clear(4);
        expect(Count(plain, 10) == 0 && pool.ParkedCount == 1, "a client that asks for nothing: the tab's frames forgotten as before (only the earlier lean park stays)");

        // The goldens' request (2): lean always.
        Volatile.Write(ref shared.Control.TabParkingRequest, 2);
        Set(5, files[2]);
        var asked = pool.SerialOf(5);
        Decoded(asked, 10);
        pool.ShownForTests(5, false, (14, at4));
        Clear(5);
        expect(Count(asked, 10) == 1 && pool.ParkedCount == 2, "the request for lean parking (2): the playhead's frame alone kept, parked");

        // Nothing drawn of it (a tab whose playhead showed none of the file): nothing parked.
        Set(6, files[3]);
        var unseen = pool.SerialOf(6);
        Decoded(unseen, 10);
        Clear(6);
        expect(Count(unseen, 10) == 0 && pool.ParkedCount == 2, "a file its playhead did not show: forgotten, not parked");

        // The cap: max(96 MB, an eighth of the budget); 512 MB here, so 96 MB. Nine tabs of one 4K frame each (11.9 MB): the oldest goes.
        expect(DecoderPool.KeptCap(512L << 20) == 96L << 20 && DecoderPool.KeptCap(2L << 30) == 256L << 20, "the kept frames' cap: 96 MB under a 768 MB budget, an eighth of it above (256 MB of 2 GB)");
        pool.Cache.Clear();
        pool.Cache.ScaleForTests(512L << 20, 512L << 20, 64L << 20, Stopwatch.GetTimestamp);
        var serials = new List<int>();
        for (var i = 0; i < 9; i++)
        {
            var id = 10 + i;
            Set(id, files[1 + i]);
            var serial = pool.SerialOf(id);
            serials.Add(serial);
            Decoded(serial, 1, 3840, 2160);
            pool.ShownForTests(id, false, (100 + i, 0.001));
            Clear(id);
        }

        var cap = DecoderPool.KeptCap(pool.Cache.BudgetBytes);
        expect(pool.Cache.KeptBytes <= cap && !pool.Cache.Has(serials[0], 0.001) && serials.Skip(1).All(s => pool.Cache.Has(s, 0.001)) && pool.ParkedCount == 8,
            "nine tabs behind of one 4K frame each over the 96 MB cap: the oldest tab's frame let go of, the other eight kept and parked");
        pool.Cache.Clear();
        expect(pool.Cache.KeptBytes == 0 && serials.All(s => !pool.Cache.Has(s, 0.001)), "a low-memory Clear still lets every kept frame go (the safety floor)");

        // A tab's pre-roll (TabRoll) is idle work in the pool; the timeline pointer's play-ahead (HoverRoll) stays the pointer's.
        Set(20, files[9]);
        ref var control = ref shared.Control;
        control.Hints[0] = new GpuSeekHint { SourceId = 20, Kind = (int)GpuSeekHintKind.HoverRoll, MediaSeconds = 1, Span = 0.5f };
        control.Hints[1] = new GpuSeekHint { SourceId = 20, Kind = (int)GpuSeekHintKind.TabRoll, MediaSeconds = 2, Span = 0.5f };
        Volatile.Write(ref control.HintCount, 2);
        Interlocked.Increment(ref control.HintsVersion);
        pool.TakeHints();
        expect(pool.DemandKindsForTests() == "Hover,Idle", F($"the hints taken: the pointer's play-ahead a Hover demand, a tab's pre-roll an Idle one ({pool.DemandKindsForTests()})"));

        // The hint writer hands a caller's extras on as they are.
        var into = new GpuSeekHint[16];
        new GpuHintWriter().Write(GpuScenePlan.Empty, 0, 1, into, out var count, extras: [new GpuSeekHint { SourceId = 3, Kind = (int)GpuSeekHintKind.TabRoll, MediaSeconds = 5, Span = 0.5f }]);
        expect(count >= 1 && into[0].Kind == (int)GpuSeekHintKind.TabRoll && into[0].SourceId == 3, "the hint writer passes a tab's pre-roll through unchanged");

        // The idle rule (pure): never playing, never within IdleQuietMs of the playhead's last move, never while a frame shown or played ahead
        // waits, one idle read at a time on unified memory (two on a card of its own).
        var f = Stopwatch.Frequency;
        (string What, bool Playing, long Now, bool Pending, int Busy, bool Unified, bool Runs)[] idle =
        [
            ("playing", true, 10 * f, false, 0, true, false),
            ("paused, the playhead moved 0.2 s ago", false, 10 * f - f / 5, false, 0, true, false),
            ("paused and still, a frame on screen still to decode", false, 11 * f, true, 0, true, false),
            ("paused and still, one idle read under way on unified memory", false, 11 * f, false, 1, true, false),
            ("paused and still, one idle read under way on a card of its own", false, 11 * f, false, 1, false, true),
            ("paused and still for 400 ms, nothing else waiting", false, 10 * f + DecoderPool.IdleQuietMs * f / 1000, false, 0, true, true),
        ];
        foreach (var (what, playing, now, pending, busy, unified, runs) in idle)
            expect(DecoderPool.IdleMayRun(playing, now, 10 * f, pending, busy, unified) == runs, F($"idle work, {what}: {(runs ? "runs" : "waits")}"));
        expect(!GpuPreviewEngine.ExtrasMayBeWritten(true, false, 5000) && !GpuPreviewEngine.ExtrasMayBeWritten(false, true, 5000) && !GpuPreviewEngine.ExtrasMayBeWritten(false, false, 399)
               && GpuPreviewEngine.ExtrasMayBeWritten(false, false, 400),
            "the tabs' pre-roll is named to the worker only paused, not scrubbing, and 400 ms after the last seek");
        expect(MainWindow.TabHoverRestMs == 200 && MainWindow.LeanPrewarmDue(true, false, false, false) && !MainWindow.LeanPrewarmDue(false, false, false, false)
               && !MainWindow.LeanPrewarmDue(true, true, false, false) && !MainWindow.LeanPrewarmDue(true, false, true, false) && !MainWindow.LeanPrewarmDue(true, false, false, true),
            "a lean machine decodes a hovered tab ahead after 200 ms of rest, only while still hovered, paused, not scrubbing and not switching");
        var warm = Read("MainWindow.TabWarm.cs");
        expect(InOrder(Method(warm, "private void PrewarmLikelyTabs("), "if (engine.LeanTabs)", "if (hovered is null || ReferenceEquals(hovered, front))", "engine.PrewarmTabs([]);", "_tabRestWarm.Start();"),
            "a lean machine pre-rolls nothing on a switch (the list emptied), and a hovered tab only from its rest timer");

        // The decoders of a source retired under a lane are disposed by that lane at once, outside the lock; none under it.
        var lanes = Read("Compositor", "DecoderPool.Lanes.cs");
        expect(InOrder(Method(lanes, "private void RunLane("), "BuryIdle(demand.Source);", "if (demand.Source.Retired)", "DisposeBuried();")
               && InOrder(Method(lanes, "private MfVideoDecoder? OpenFor("), "_graveyard.Add(decoder);", "DisposeBuried();")
               && !Method(lanes, "private MfVideoDecoder? OpenFor(").Contains("decoder.Dispose();", StringComparison.Ordinal),
            "a decoder whose source retired is disposed at once by its lane, never under the pool's lock");

        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // The temp folder's.
            }
        }
    }

    // ----- multi-tab-preroll-kept -----

    private static void PreRollKept(Action<bool, string> expect, StringBuilder text)
    {
        var broker = new GpuResourceBroker(null);
        broker.BeginPlan();
        var front = broker.SourceFor(@"C:\DVR\front.mp4", 0);
        var next = broker.SourceFor(@"C:\DVR\next.mp4", 0);
        var shared = broker.SourceFor(@"C:\DVR\both.mp4", 0);
        expect(front >= 0 && next >= 0 && shared >= 0 && new[] { front, next, shared }.Distinct().Count() == 3, "three files in the source table");

        // The tab put behind: every source let go of but those the next tab's pre-roll named.
        broker.ReleaseAllBut(new HashSet<int> { next, shared });
        expect(broker.PathHashOf(front) == 0 && broker.PathHashOf(next) != 0 && broker.PathHashOf(shared) != 0, "a switch lets go of the tab's own files but keeps those the pre-roll named");

        // The tab brought to the front: its plan asks for its files (the pre-rolled one at the same id), then the kept ones it does not use go.
        broker.BeginPlan();
        expect(broker.SourceFor(@"c:\dvr\NEXT.mp4", 0) == next, "the tab brought to the front finds its pre-rolled file at the same id (its decoders and frames in the worker)");
        broker.ReleaseKeptUnasked();
        expect(broker.PathHashOf(next) != 0 && broker.PathHashOf(shared) == 0, "its first plan lets go of the kept file it does not use");
        broker.ReleaseAll();
        expect(broker.PathHashOf(next) == 0, "ReleaseAll still lets go of everything");

        var engine = Read("Services", "GpuPreviewEngine.cs");
        expect(InOrder(Method(engine, "public void Suspend()"), "Volatile.Read(ref _tabPrewarm)", "Empty();", "ReleaseSourcesForSwitch(prewarmed);", "_client.TrimWorker();")
               && InOrder(Method(engine, "public void SetState("), "GpuScenePlan.Create(state, _broker);", "ReleaseUnusedKeptSources();"),
            "Suspend keeps the pre-roll's sources (read before Empty lets go of it); the first SetState after lets go of those its plan does not use");
        expect(InOrder(Method(engine, "public void PrewarmTabs("), "Kind = (int)GpuSeekHintKind.TabRoll") && Method(engine, "private void LeaveHints(").Contains("Kind = (int)GpuSeekHintKind.TabRoll", StringComparison.Ordinal)
               && !Method(engine, "private void LeaveHints(").Contains("GpuSeekHintKind.HoverRoll", StringComparison.Ordinal),
            "the tabs' pre-roll and a drop's file are TabRoll hints");
    }

    // ----- multi-tab-strips-pause -----

    private static void StripsPause(Action<bool, string> expect, StringBuilder text)
    {
        var raised = 0;
        void Count() => Interlocked.Increment(ref raised);
        WorkScheduler.PauseChanged += Count;
        try
        {
            WorkScheduler.IsPlaying = false;
            var quiet = !WorkScheduler.BackgroundPaused;
            WorkScheduler.IsPlaying = true;
            var playing = WorkScheduler.BackgroundPaused == AppSettings.Current.PauseBackgroundWhilePlaying;
            WorkScheduler.IsPlaying = false;
            expect(quiet && playing && !WorkScheduler.BackgroundPaused && raised >= 2, "background work stops while the player plays (as Settings asks), and is told so both ways");

            // A scrub's moves hold it back 400 ms after the last one, whatever the setting.
            WorkScheduler.HoldBackground(TimeSpan.FromMilliseconds(150));
            var held = WorkScheduler.BackgroundPaused;
            var clock = Stopwatch.StartNew();
            while (WorkScheduler.BackgroundPaused && clock.ElapsedMilliseconds < 3000)
                Thread.Sleep(10);
            expect(held && !WorkScheduler.BackgroundPaused && GpuPreviewEngine.ScrubHold == TimeSpan.FromMilliseconds(400), "a scrub holds background work back (400 ms after each move), and it goes on when the hold ends");

            expect(!WorkScheduler.PausePoint()(), "outside any work (a golden, a tool) nothing ever pauses");

            // Inside background work: the question says stop while playing; a wait gives the lane back and ends when the player pauses.
            var (stops, waited, resumed) = (false, false, false);
            WorkScheduler.Run(new WorkSpec("multi-tab pause", WorkLane.Background) { Visible = false }, async work =>
            {
                var pause = WorkScheduler.PausePoint();
                WorkScheduler.IsPlaying = true;
                stops = pause() == AppSettings.Current.PauseBackgroundWhilePlaying;
                var wait = WorkScheduler.WhenBackgroundMayRunAsync(work.Token);
                await Task.Delay(300);
                waited = !wait.IsCompleted || !AppSettings.Current.PauseBackgroundWhilePlaying;
                WorkScheduler.IsPlaying = false;
                resumed = await Task.WhenAny(wait, Task.Delay(2000)) == wait;
            }).Wait(10_000);
            expect(stops && waited && resumed, "inside background work: stopped while the player plays, still waiting 300 ms on, going on as it pauses");

            var urgent = true;
            WorkScheduler.Run(new WorkSpec("multi-tab urgent", WorkLane.Background) { Visible = false, Urgent = true }, _ =>
            {
                var pause = WorkScheduler.PausePoint();
                WorkScheduler.IsPlaying = true;
                urgent = pause();
                WorkScheduler.IsPlaying = false;
                return Task.CompletedTask;
            }).Wait(10_000);
            expect(!urgent, "work the user waits on (urgent) never stops");
        }
        finally
        {
            WorkScheduler.IsPlaying = false;
            WorkScheduler.PauseChanged -= Count;
        }

        // The strip workers ask between pictures, after their cancellation check; a still never asks. The engine's Seek holds the work back.
        var film = Read("Services", "GpuFilmstrips.cs");
        expect(InOrder(Method(film, "private static Result Draw("), "if (!MayGoOn(sheet))", "client ??= Start();", "sheet.Token.IsCancellationRequested || sheet.Done.Task.IsCompleted", "if (!MayGoOn(sheet))", "WriteScene(")
               && film.Contains("sheet = new Sheet(videoPath, imagePath, layout, targets, complete, token) { Pause = WorkScheduler.PausePoint() };", StringComparison.Ordinal)
               && InOrder(Method(film, "private static async Task<bool> RunAsync("), "WorkScheduler.GpuDecodeGate.Release();", "if (result.Outcome != Outcome.Paused)", "await WorkScheduler.WhenBackgroundMayRunAsync(token);"),
            "a filmstrip stops before it starts and between pictures, after its cancellation check; stopped, it gives its permit back and waits");
        var program = Read("Services", "GpuProgramStrip.cs");
        expect(InOrder(Method(program, "private ThumbnailSheet? DrawSheet("), "job.Token.IsCancellationRequested || job.Asked != Volatile.Read(ref _asked)", "if (!_pictures.TryGetValue(key, out var picture))", "if (!MayGoOn(job))", "client.Publish();")
               && InOrder(Method(program, "private async void Draw("), "var pause = WorkScheduler.PausePoint();", "WorkScheduler.GpuDecodeGate.Release();", "await WorkScheduler.WhenBackgroundMayRunAsync(work.Token);", "SheetMark = mark;", "SheetReady?.Invoke(sheet);")
               && !Method(program, "public async Task<ThumbnailSheet?> DrawNowAsync(").Contains("Pause", StringComparison.Ordinal),
            "the overview sheet stops before a picture it has to draw (drawn ones are reused); stopped, it leaves its lane and permit; DrawNowAsync never stops");
        var engine = Read("Services", "GpuPreviewEngine.cs");
        expect(InOrder(Method(engine, "public void Seek("), "if (elapsed < ScrubbingFor)", "WorkScheduler.HoldBackground(ScrubHold);"), "each move of a scrub holds background work back");
    }

    // ----- multi-tab-resume-working-set -----

    private static void ResumeWorkingSet(Action<bool, string> expect, StringBuilder text)
    {
        const int W = 1920, H = 1080;
        using var client = new CompositorClient(adapter: -1, keepStandby: false);
        var started = Stopwatch.StartNew();
        while (client.State == CompositorWorkerState.Starting && started.ElapsedMilliseconds < 10_000)
            Thread.Sleep(10);

        bool Draw()
        {
            client.ClearScene();
            ref var scene = ref client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.OutputWidth, scene.OutputHeight) = (W, H, W, H);
            for (var k = 0; k < 2; k++)
            {
                ref var layer = ref client.Layer(k);
                layer.SourceKind = (int)GpuSourceKind.TestPattern;
                layer.PatternKind = 1 + k;
                (layer.X, layer.Y, layer.Width, layer.Height) = (k * 200, k * 100, W - k * 400, H - k * 200);
                (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
                (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
                (layer.CropRight, layer.CropBottom) = (1, 1);
                layer.Flags = GpuLayerDescriptor.FlagVisible;
                layer.Effects[layer.EffectCount++] = new GpuEffectPacket(GpuShaderSlot.BoxBlur, 8 + 4 * k, 0, 0, 0, 0, 0, 0, 0);
                client.Scene.ActiveLayerCount = k + 1;
            }

            return client.WaitForFrame(client.Publish(), 20_000);
        }

        int Made() => Volatile.Read(ref client.Memory.Control.TexturesMade);
        var drawn = Draw() && Draw();
        var before = Made();

        // A tab switch: the trim asked for, and the next tab's scene right after it.
        var switched = client.TrimWorker();
        var next = Draw();
        var served = client.WaitForTrim(switched, 10_000);
        var again = Draw();
        var kept = Made() - before;

        // A trim with nothing in front after it (the last tab let go of): everything handed back, the next frame makes its textures again.
        var alone = client.WaitForTrim(client.TrimWorker(), 10_000);
        var after = Made();
        var redrawn = Draw();
        var remade = Made() - after;
        Console.WriteLine(F($"        textures made: {kept} across a trim followed by a scene, {remade} after a trim alone"));
        expect(drawn && next && served && again && kept == 0, "a trim followed by a scene (a tab switch): the next frames draw with the working set kept, no texture made");
        expect(alone && redrawn && remade > 0, "a trim with nothing after it: the working set handed back, the next frame makes its textures again");
        var worker = Read("Compositor", "CompositorWorker.cs");
        expect(InOrder(worker, "sceneAfterTrim |= giveBackWaiting && isNew;", "if (giveBackWaiting && GiveBackDue(now, askedLast, decoders.Playing))", "if (!sceneAfterTrim)", "ReleaseWorkingSet(gpu, decoders, renderer, asked: true);", "GiveBack(ref control, gpu, decoders.Cache);"),
            "the driver's allocations and the heap are handed back after an asked-for trim only when no scene followed it (the idle trim does it later)");
    }

    // ----- multi-tab-kept-share -----

    private static void KeptShareOfBudget(Action<bool, string> expect, StringBuilder text)
    {
        expect(FrameCache.KeptShare(96L << 20) == 24L << 20 && FrameCache.KeptShare(200L << 20) == 50L << 20 && FrameCache.KeptShare(512L << 20) == 96L << 20
               && FrameCache.KeptShare(2L << 30) == 256L << 20 && DecoderPool.KeptCap(200L << 20) == FrameCache.KeptShare(200L << 20),
            "the kept frames' share: max(96 MB, an eighth of the budget), never above a quarter of it (24 MB of the 96 MB floor, 50 MB of 200 MB, 96 MB of 512 MB, 256 MB of 2 GB); the parks' cap is the same");

        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.KeptShare");
        Directory.CreateDirectory(folder);
        var files = Enumerable.Range(0, 7).Select(i => Path.Combine(folder, F($"tab{i}.bin"))).ToArray();
        foreach (var file in files)
        {
            File.WriteAllBytes(file, new byte[4096]);
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        using var gpu = GpuDevice.Create(-1);
        using var shared = CompositorSharedMemory.Create(F($"HandPeg.GoldenTests.KeptShare.{Guid.NewGuid():N}"));
        using var pool = new DecoderPool(gpu, shared);
        pool.Cache.BigCardForTests(false);
        pool.Cache.ScaleForTests(512L << 20, 512L << 20, 64L << 20, Stopwatch.GetTimestamp);
        pool.LeanForTests(true);
        var generation = 0;
        void Set(int id, string path)
        {
            ref var entry = ref shared.Source(id);
            (entry.Kind, entry.StreamIndex, entry.IFrameCount, entry.PathHash) = ((int)GpuSourceKind.Media, 0, 0, CompositorSharedMemory.PathHash(path));
            CompositorSharedMemory.WriteString(entry.Path, path);
            Volatile.Write(ref entry.Generation, ++generation);
            Interlocked.Increment(ref shared.Control.SourcesVersion);
            pool.SyncSources();
            pool.StampsBackForTests(10_000);
        }

        void Decoded(int serial, int frames, int width, int height, int first = 0, bool keyFirst = false)
        {
            for (var i = first; i < first + frames; i++)
            {
                var frame = pool.Cache.Rent(width, height, tenBit: false, software: true);
                (frame.Source, frame.Time, frame.Until, frame.Closed, frame.KeyFrame) = (serial, i / 30.0, (i + 1) / 30.0, true, keyFirst && i == first);
                pool.Cache.Insert(frame);
            }
        }

        // Five tabs put behind, one 4K frame each kept at its playhead (5 x 11.9 MB), under the 96 MB cap of a 512 MB budget.
        var parked = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            Set(i, files[i]);
            var serial = pool.SerialOf(i);
            parked.Add(serial);
            Decoded(serial, 1, 3840, 2160);
            pool.ShownForTests(i, false, (100 + i, 0.001));
            Volatile.Write(ref shared.Source(i).Generation, 0);
            Interlocked.Increment(ref shared.Control.SourcesVersion);
            pool.SyncSources();
        }

        var fourK = 3840L * 2160 * 3 / 2;
        expect(pool.Cache.KeptBytes == 5 * fourK && pool.ParkedCount == 5, "five tabs behind keep their playhead's 4K frame each (59 MB, within the cap of a 512 MB budget)");

        // The tab in front played through a clip (5.4.4): six of its 1080p frames, a key frame first, stay cached with no plan wanting them.
        Set(11, files[6]);
        var played = pool.SerialOf(11);
        Decoded(played, 6, 1920, 1080, keyFirst: true);

        // Other programs take the memory: the budget falls to 64 MB (its share 16 MB). The frames no plan wants go before any kept frame
        // beyond the share; then only the oldest tab's kept frame has to go (80 MB brought under 90 % of 64 MB).
        pool.Cache.ScaleForTests(64L << 20, 64L << 20, 8L << 20, Stopwatch.GetTimestamp);
        var (dead, keptNow) = (Enumerable.Range(0, 6).Count(i => pool.Cache.Has(played, i / 30.0 + 0.001)), parked.Count(serial => pool.Cache.Has(serial, 0.001)));
        Console.WriteLine(F($"        budget fallen to 64 MB: {dead} of the played clip's 6 frames left, {keptNow} of 5 tabs behind keep their frame"));
        expect(dead == 0 && keptNow == 4 && !pool.Cache.Has(parked[0], 0.001),
            "the budget fallen to 64 MB: the frames no plan wants went first (their key frame too), then only the oldest tab's kept frame; four tabs behind still find their playhead");

        // The tab in front decodes ten 1080p frames (30 MB) at the playhead it shows (an interest of the cache, as its frames' plan makes).
        Set(10, files[5]);
        var front = pool.SerialOf(10);
        pool.Cache.SetInterests([(front, 0.0, 1.0)]);
        Decoded(front, 10, 1920, 1080);
        int Held(int frames) => Enumerable.Range(0, frames).Count(i => pool.Cache.Has(front, i / 30.0 + 0.001));
        var share = FrameCache.KeptShare(pool.Cache.BudgetBytes);
        var (ten, keptThen) = (Held(10), pool.Cache.KeptBytes);
        Console.WriteLine(F($"        budget {pool.Cache.BudgetBytes >> 20} MB (share {share >> 20} MB): the front tab holds {ten} of its 10 frames, {keptThen >> 20} MB kept for tabs behind"));
        expect(pool.Cache.BudgetBytes == 64L << 20 && ten == 10 && !pool.Cache.Has(parked[0], 0.001) && pool.Cache.Has(parked[^1], 0.001),
            "then, under four tabs' kept frames: the tab in front keeps all ten frames it decoded; the kept frames beyond the share went before them, the oldest tab's first");

        // The tab in front decodes ten more: what is kept for the tabs behind comes down to the share; the newest tab behind keeps its frame.
        Decoded(front, 10, 1920, 1080, first: 10);
        var twenty = Held(20);
        Console.WriteLine(F($"        then the front tab holds {twenty} of its 20 frames, {pool.Cache.KeptBytes >> 20} MB kept for tabs behind"));
        expect(pool.Cache.KeptBytes <= share && pool.Cache.Has(parked[^1], 0.001) && twenty >= 15,
            "ten frames more: the kept frames held to the share of the budget as it is now (the tab in front holds the rest); the newest tab behind still finds its playhead");

        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // The temp folder's.
            }
        }
    }

    // ----- multi-tab-preroll-after-switch -----

    private static void PreRollAfterSwitch(Action<bool, string> expect, StringBuilder text)
    {
        var broker = new GpuResourceBroker(null);
        broker.BeginPlan();
        var own = broker.SourceFor(@"C:\DVR\own.mp4", 0);
        var next = broker.SourceFor(@"C:\DVR\next.mp4", 0);
        var unused = broker.SourceFor(@"C:\DVR\unused.mp4", 0);
        var again = broker.SourceFor(@"C:\DVR\again.mp4", 0);

        // The switch: the pre-roll's three files kept, the tab's own let go of (its id free for the next file).
        broker.ReleaseAllBut(new HashSet<int> { next, unused, again });
        var ownGone = broker.PathHashOf(own) == 0;

        // Before the next tab's first plan (its view restored at Background priority): the next pre-roll names a new file and one kept
        // file again, and a file dragged over the player is opened ahead of its drop.
        var preroll = broker.SourceFor(@"C:\DVR\preroll.mp4", 0);
        broker.SourceFor(@"C:\DVR\again.mp4", 0);
        var dropped = broker.SourceFor(@"C:\DVR\dropped.mp4", 0);

        // The first plan uses the pre-rolled file; then the kept sources nothing asked for since go.
        broker.BeginPlan();
        broker.SourceFor(@"C:\DVR\next.mp4", 0);
        broker.ReleaseKeptUnasked();
        expect(ownGone && broker.PathHashOf(next) != 0 && broker.PathHashOf(unused) == 0,
            "the first plan after a switch: its pre-rolled file stays, the kept file nothing asked for since goes");
        expect(broker.PathHashOf(preroll) != 0 && broker.PathHashOf(dropped) != 0 && broker.PathHashOf(again) != 0,
            "the next pre-roll's new file, a kept file it named again, and a drop's file opened after the switch all stay (their decoders and frames kept)");

        // Once only: the next plan lets go of nothing more.
        broker.BeginPlan();
        broker.ReleaseKeptUnasked();
        expect(broker.PathHashOf(preroll) != 0 && broker.PathHashOf(dropped) != 0 && broker.PathHashOf(next) != 0, "only the first plan after a switch lets go of kept sources");
        broker.ReleaseAll();
        expect(new[] { next, preroll, again, dropped }.All(id => broker.PathHashOf(id) == 0), "ReleaseAll still lets go of everything");
        var engine = Read("Services", "GpuPreviewEngine.Tabs.cs");
        expect(Method(engine, "private void ReleaseUnusedKeptSources()").Contains("_broker.ReleaseKeptUnasked();", StringComparison.Ordinal),
            "the engine's first SetState after a Suspend asks the broker for exactly that");
    }

    // ----- multi-tab-strips-resume-limit -----

    private static void StripsResumeLimit(Action<bool, string> expect, StringBuilder text)
    {
        var settings = AppSettings.Current;
        var (jobs, pauses) = (settings.BackgroundJobs, settings.PauseBackgroundWhilePlaying);
        (settings.BackgroundJobs, settings.PauseBackgroundWhilePlaying) = (2, true);
        try
        {
            WorkScheduler.IsPlaying = false;
            var limit = WorkScheduler.LaneLimit(WorkLane.Background);
            var before = WorkScheduler.LaneForTests(WorkLane.Background);
            var (active, most) = (0, 0);
            var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Enter(string who)
            {
                order.Enqueue(who);
                var now = Interlocked.Increment(ref active);
                for (var seen = Volatile.Read(ref most); now > seen; seen = Volatile.Read(ref most))
                {
                    if (Interlocked.CompareExchange(ref most, now, seen) == seen)
                        break;
                }
            }

            void Leave() => Interlocked.Decrement(ref active);

            // The lane filled with strips drawn in pieces; the player plays; each stops between its pieces (its slot given back).
            using var started = new CountdownEvent(limit);
            using var stopping = new CountdownEvent(limit);
            using var playing = new ManualResetEventSlim();
            var strips = Enumerable.Range(0, limit).Select(i => WorkScheduler.Run(new WorkSpec(F($"multi-tab strip {i}"), WorkLane.Background) { Visible = false }, async work =>
            {
                Enter("strip");
                started.Signal();
                await Task.Run(() => playing.Wait(5000));
                Leave();
                stopping.Signal();
                await WorkScheduler.WhenBackgroundMayRunAsync(work.Token);
                Enter("strip again");
                await Task.Delay(300);
                Leave();
            })).ToArray();
            var filled = started.Wait(5000);
            WorkScheduler.IsPlaying = true;
            playing.Set();
            var stopped = stopping.Wait(5000);
            Thread.Sleep(150);
            var whileStopped = WorkScheduler.LaneForTests(WorkLane.Background);

            // Work asked for while playing waits; the player pauses: the stopped strips go on first, the lane never over its limit.
            var waited = Enumerable.Range(0, limit).Select(i => WorkScheduler.Run(new WorkSpec(F($"multi-tab waiting {i}"), WorkLane.Background) { Visible = false }, async _ =>
            {
                Enter("waited");
                await Task.Delay(300);
                Leave();
            })).ToArray();
            Thread.Sleep(100);
            WorkScheduler.IsPlaying = false;
            var done = Task.WaitAll(strips.Concat(waited).ToArray(), 10_000);

            // (A task ends a moment before its work gives its slot back.)
            SpinWait.SpinUntil(() => WorkScheduler.LaneForTests(WorkLane.Background) == before, 2000);
            var after = WorkScheduler.LaneForTests(WorkLane.Background);
            var sequence = order.ToArray();
            var firstWaited = Array.IndexOf(sequence, "waited");
            var lastAgain = Array.LastIndexOf(sequence, "strip again");
            Console.WriteLine(F($"        lane limit {limit}: at most {most} at once; while stopped {whileStopped.Running} running, {whileStopped.Stopped} stopped; order {string.Join(", ", sequence)}"));
            expect(filled && stopped && whileStopped.Stopped == limit && whileStopped.Running == before.Running,
                "the strips stopped between their pieces while the player plays give their slots back");
            expect(done && most <= limit, "after the play, the stopped strips and the work that waited never run more than the lane's limit at once");
            expect(firstWaited > lastAgain && lastAgain >= 0, "the stopped strips go on first; the work asked for meanwhile waits for their room");
            expect(after == before, "every slot accounted for once all of it ended (none given back twice, none kept)");

            // Cancelled while it waits for the pause to end: its slot not given back twice when its work ends.
            using var cancel = new CancellationTokenSource();
            using var waiting = new ManualResetEventSlim();
            var cancelled = WorkScheduler.Run(new WorkSpec("multi-tab cancelled", WorkLane.Background) { Visible = false }, async work =>
            {
                WorkScheduler.IsPlaying = true;
                var wait = WorkScheduler.WhenBackgroundMayRunAsync(work.Token);
                waiting.Set();
                await wait;
            }, cancel.Token);
            waiting.Wait(5000);
            cancel.Cancel();
            var ended = SpinWait.SpinUntil(() => cancelled.IsCompleted, 5000) && cancelled.IsCanceled;
            WorkScheduler.IsPlaying = false;
            SpinWait.SpinUntil(() => WorkScheduler.LaneForTests(WorkLane.Background) == before, 2000);
            expect(ended && WorkScheduler.LaneForTests(WorkLane.Background) == before, "work cancelled while it is stopped ends with its slot accounted for once");

            // "Pause background work while playing" turned off mid-play (5.4.4, Settings saved: WorkScheduler.ApplySettings): the strips stopped
            // between their pieces go on while the player still plays, and the work asked for meanwhile starts in the room kept for them.
            using var startedAgain = new CountdownEvent(limit);
            using var stoppingAgain = new CountdownEvent(limit);
            using var playingAgain = new ManualResetEventSlim();
            var goneOn = 0;
            var stripsAgain = Enumerable.Range(0, limit).Select(i => WorkScheduler.Run(new WorkSpec(F($"multi-tab setting strip {i}"), WorkLane.Background) { Visible = false }, async work =>
            {
                startedAgain.Signal();
                await Task.Run(() => playingAgain.Wait(5000));
                stoppingAgain.Signal();
                await WorkScheduler.WhenBackgroundMayRunAsync(work.Token);
                Interlocked.Increment(ref goneOn);
            })).ToArray();
            var filledAgain = startedAgain.Wait(5000);
            WorkScheduler.IsPlaying = true;
            playingAgain.Set();
            var stoppedAgain = stoppingAgain.Wait(5000) && SpinWait.SpinUntil(() => WorkScheduler.LaneForTests(WorkLane.Background).Stopped == before.Stopped + limit, 2000);
            var meanwhile = WorkScheduler.Run(new WorkSpec("multi-tab setting waiting", WorkLane.Background) { Visible = false }, _ => Task.CompletedTask);
            settings.PauseBackgroundWhilePlaying = false;
            WorkScheduler.TakeSettings();
            var wentOn = Task.WaitAll(stripsAgain, 3000) && meanwhile.Wait(3000);
            var (stillPlaying, onWhilePlaying, ranWhilePlaying) = (WorkScheduler.IsPlaying, Volatile.Read(ref goneOn), meanwhile.IsCompleted);
            WorkScheduler.IsPlaying = false;
            settings.PauseBackgroundWhilePlaying = true;
            Task.WaitAll([.. stripsAgain, meanwhile], 5000);
            SpinWait.SpinUntil(() => WorkScheduler.LaneForTests(WorkLane.Background) == before, 2000);
            Console.WriteLine(F($"        setting off mid-play: {onWhilePlaying} of {limit} stopped strips went on while playing, the waiting work {(ranWhilePlaying ? "ran" : "waited")}"));
            expect(filledAgain && stoppedAgain && wentOn && stillPlaying && onWhilePlaying == limit && ranWhilePlaying && WorkScheduler.LaneForTests(WorkLane.Background) == before,
                "the setting turned off mid-play: the stopped strips go on while the player still plays, and the work asked for meanwhile runs in their room");
        }
        finally
        {
            WorkScheduler.IsPlaying = false;
            (settings.BackgroundJobs, settings.PauseBackgroundWhilePlaying) = (jobs, pauses);
        }

        var scheduler = Read("Services", "WorkScheduler.cs");
        expect(InOrder(Method(scheduler, "private static void Pump()"), "GoOnLocked(resumed, counted: true);", "var kept = Running[index] + Stopped[index] >= LaneLimit(lane);", "StartLocked(next);"),
            "the scheduler serves stopped work going on before work not started yet, and keeps its room meanwhile");
        expect(InOrder(Method(scheduler, "public static void ApplySettings()"), "JobObjects.Configure(", "TakeSettings();")
               && InOrder(Method(scheduler, "internal static void TakeSettings()"), "Pump();", "RaisePauseChanged();"),
            "saved Settings reach work stopped between its pieces (ApplySettings: Pump, then PauseChanged)");
    }

    // ----- frame-latency-turns -----

    private static void LatencyTurns(Action<bool, string> expect, StringBuilder text)
    {
        // DXGI's frame-latency object, modelled: a semaphore holding the swap chain's one turn (its latency, 1), released once for every
        // frame the display retires. The pump tries two turns a refresh (it is faster than the display), each waited for 0 ms (one thread:
        // a turn not free now never comes during the wait), and the display retires one queued frame a refresh.
        static (int MostQueued, int OwedAfterPause) Session(bool turnsTaken, int pausedPresents, int refreshes)
        {
            using var semaphore = new Semaphore(1, 4096);
            Func<int, bool> wait = milliseconds => semaphore.WaitOne(milliseconds);
            var turns = new FrameLatencyTurns();
            var queued = 0;
            void Retire()
            {
                if (queued > 0)
                {
                    queued--;
                    semaphore.Release();
                }
            }

            void Queue()
            {
                queued++;
                if (turnsTaken)
                    turns.Queued(wait);
            }

            // Paused: frames presented with no wait before them (a seek's frames, a redraw), most retired before the next comes, every
            // fourth not (a scrub faster than the display); then the display shows what is left.
            for (var i = 0; i < pausedPresents; i++)
            {
                Queue();
                if (i % 4 != 3)
                    Retire();
            }

            var owed = turns.Owed;
            while (queued > 0)
                Retire();

            // Playing: a frame queued for every turn taken.
            var most = 0;
            for (var r = 0; r < refreshes; r++)
            {
                for (var t = 0; t < 2; t++)
                {
                    if (turnsTaken ? turns.Wait(wait, 0) : wait(0))
                    {
                        Queue();
                        most = Math.Max(most, queued);
                    }
                }

                Retire();
            }

            return (most, owed);
        }

        // 40 turnless presents: the XPS 13's count before its sixth play of a session (5.4.4's play bench).
        var before = Session(turnsTaken: false, pausedPresents: 40, refreshes: 120);
        var now = Session(turnsTaken: true, pausedPresents: 40, refreshes: 120);
        text.Append(F($"frames queued at most while playing after 40 paused presents: {before.MostQueued} without turns, {now.MostQueued} with them ({now.OwedAfterPause} owed after the pause)\n"));
        expect(before.MostQueued > 30, "the bug as it was: presents made without a turn leave the object counts, and the playing pump then runs that many frames ahead of the display");
        expect(now.MostQueued == 1, "every frame queued takes a turn: after any pause the playing pump is at most one frame ahead of the display (the swap chain's latency)");
        expect(now.OwedAfterPause == 9, "a paused present that finds no turn free owes one (nine of the forty came while the display had not retired the one before)");

        // Owed turns are taken back as they come free, before a wait takes its own; a turn held is never taken twice.
        using (var semaphore = new Semaphore(0, 16))
        {
            Func<int, bool> wait = milliseconds => semaphore.WaitOne(milliseconds);
            var turns = new FrameLatencyTurns();
            turns.Queued(wait);
            turns.Queued(wait);
            var owedTwo = turns.Owed == 2 && !turns.Taken;
            semaphore.Release();
            var firstWait = turns.Wait(wait, 0);
            var afterFirst = (turns.Owed, turns.Taken);
            semaphore.Release(2);
            var secondWait = turns.Wait(wait, 0);
            var afterSecond = (turns.Owed, turns.Taken);
            semaphore.Release();
            var thirdWait = turns.Wait(wait, 0);
            var leftFree = semaphore.WaitOne(0);
            expect(owedTwo && !firstWait && afterFirst == (1, false) && secondWait && afterSecond == (0, true),
                "two frames queued with no turn free owe two; the next waits take them back as turns come free, then take their own");
            expect(thirdWait && leftFree && turns.Taken,
                "a wait while a turn is held keeps it and takes no second one (the turn freed meanwhile is still there)");
            turns.Queued(wait);
            expect(!turns.Taken && turns.Owed == 0, "a frame queued uses the turn held");
            turns.Reset();
            expect(!turns.Taken && turns.Owed == 0, "a new swap chain starts with nothing taken or owed");
        }

        // The player's viewport: its playing waits go through the turns, and every frame its present queued (S_OK) takes one.
        var viewport = Read("Controls", "GpuViewportControl.cs");
        expect(viewport.Contains("_ = _turns.Wait(_takeTurn, milliseconds);", StringComparison.Ordinal)
               && InOrder(viewport, "var hr = Dxgi.Present(_swapChain, vsync ? 1u : 0u, 0);", "if (hr == 0)", "_turns.Queued(_takeTurn);")
               && InOrder(viewport, "_waitable = Dxgi.FrameLatencyWaitable(_swapChain);", "_turns.Reset();"),
            "GpuViewportControl: WaitForTurn waits through FrameLatencyTurns, PresentNewest's queued frames take their turn, a new swap chain resets them");
        expect(InOrder(viewport, "if (_waitable == 0 || _nothingQueued)", "Thread.Sleep(Math.Min(milliseconds, 16));", "_ = _turns.Wait(_takeTurn, milliseconds);")
               && InOrder(viewport, "_turns.Queued(_takeTurn);", "_nothingQueued = hr > 0;"),
            "a present that queued nothing (an occluded window) keeps its turn, so the playing pump's next wait is paced by the clock, never spinning");
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
