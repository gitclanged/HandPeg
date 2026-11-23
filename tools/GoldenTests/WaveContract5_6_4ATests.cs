using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.6.4A Wave 0 executable contract. Lock-free scrub seeks (a one-word mail-slot drained by the pump under _transport), the scrub
/// gate that defers heavy PositionMs listeners until release, ghost-pause play-scrubbing, Dynamic Preview Scaling's draw-time gate and
/// upward recovery, Cutout filmstrip crops with a 16 x 16 strip-pixel floor, the aspect-locked diagonal corner resize, batched drag writes
/// and the anchor-dot hit rule. Cases marked "Wave 1" read wiring in files Wave 0 does not own and stay red until it lands.
/// </summary>
public static class WaveContract5_6_4ATests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return GoldenKit.Case("wave-contract-5-6-4a-scrub-seek-mailslot", "# Scrub seeks are lock-free: one mail-slot word, latest wins, drained by the pump under _transport.", SeekMailSlotCase);
        yield return GoldenKit.Case("wave-contract-5-6-4a-scrub-gate", "# A held scrub defers box rebuilds, hit masks and Effects pane refreshes to its release.", ScrubGateCase);
        yield return GoldenKit.Case("wave-contract-5-6-4a-scrub-ghost-pause", "# Scrubbing while playing ghost-pauses; an 80 ms rest plays a segment; release resumes whole.", GhostPauseCase);
        yield return GoldenKit.Case("wave-contract-5-6-4a-dynamic-preview-recovery", "# Only shader draw time counts as slow; 30 on-time frames step back up (0.5 -> 0.75 -> 1.0).", ScalingCase);
        yield return GoldenKit.Case("wave-contract-5-6-4a-cutout-film-crop", "# A Cutout's filmstrip shows its own region of the shared strip, at least 16 x 16 strip pixels.", FilmCropCase);
        yield return GoldenKit.Case("wave-contract-5-6-4a-gizmo", "# Aspect-locked corners resize along the diagonal; drags batch their writes; the anchor dot takes a 6 x 6 hit.", GizmoCase);
        yield return GoldenKit.Case("wave-contract-5-6-4a-avsync-virtual-sink", "# A/V Sync treats Steam Streaming sinks as virtual, as Remote Audio (Wave 1).", AvSyncCase);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static bool Has(string text, string token) => SourceProbe.IndexOf(text, token) >= 0;

    private static void SeekMailSlotCase(Action<bool, string> expect)
    {
        var slot = SeekMailSlot.Empty;
        expect(!SeekMailSlot.IsPending(ref slot) && !SeekMailSlot.TryTake(ref slot, out var none) && double.IsNaN(none), "an empty slot has nothing to take");

        SeekMailSlot.Post(ref slot, 1.5);
        SeekMailSlot.Post(ref slot, 2.25);
        expect(SeekMailSlot.PeekOr(ref slot, -1) == 2.25 && SeekMailSlot.TryTake(ref slot, out var latest) && latest == 2.25 && !SeekMailSlot.IsPending(ref slot),
            "two posts before a take: the take is the latest and empties the slot");

        SeekMailSlot.Post(ref slot, -0.0);
        expect(SeekMailSlot.TryTake(ref slot, out var zero) && zero == 0 && !double.IsNegative(zero), "-0.0 (the empty word's bits) is posted as +0.0, never lost");
        SeekMailSlot.Post(ref slot, double.NaN);
        SeekMailSlot.Post(ref slot, double.PositiveInfinity);
        expect(!SeekMailSlot.IsPending(ref slot), "non-finite targets are never posted");

        // The engine's own scrub path, timed while another thread holds its real _transport (as a pump turn applying a seek does): 500 paused
        // Seeks across two minutes of a Sequence, then a play-scrub's GhostPause and 500 playing Seeks. The instance is made without its
        // constructor (no compositor worker, no sound device), so a UI path that touched the audio engine would throw here.
        var instance = (GpuPreviewEngine)RuntimeHelpers.GetUninitializedObject(typeof(GpuPreviewEngine));
        FieldInfo Field(string name) => typeof(GpuPreviewEngine).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var planMaker = Field("_plan").FieldType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single(c => c.GetParameters().Length == 5);
        // Made untyped and handed to the field as is (a Lock converted to object would draw CS9216); held below through its Lock type.
        var transportObject = Activator.CreateInstance(typeof(Lock))!;
        var transport = (Lock)transportObject;
        using var wake = new AutoResetEvent(false);
        Field("_transport").SetValue(instance, transportObject);
        Field("_dirty").SetValue(instance, wake);
        Field("_plan").SetValue(instance, planMaker.Invoke([1920, 1080, 120.0, Array.CreateInstance(planMaker.GetParameters()[3].ParameterType.GetElementType()!, 0), null]));
        Field("_pendingSeekBits").SetValue(instance, SeekMailSlot.Empty);
        Field("_lastSeekAt").SetValue(instance, double.NaN);

        using var held = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (transport)
            {
                held.Set();
                done.Wait(5000);
            }
        }) { IsBackground = true };
        holder.Start();
        held.Wait(2000);
        for (var i = 0; i < 8; i++)
            instance.Seek(i);
        double Timed(double from)
        {
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 500; i++)
                instance.Seek(from + i * 0.1);
            return clock.Elapsed.TotalMilliseconds;
        }

        var paused = Timed(1);
        var pausedAt = instance.Position;
        Field("_playing").SetValue(instance, true);
        var ghostClock = Stopwatch.StartNew();
        var ghosted = instance.GhostPause();
        var playing = Timed(2) + ghostClock.Elapsed.TotalMilliseconds;
        var playingAt = instance.Position;
        done.Set();
        holder.Join(2000);

        // The golden line stays byte-stable: the measured times are only spelled out when the case fails.
        var quick = paused < 5 && playing < 5;
        expect(quick && Math.Abs(pausedAt - 50.9) < 1e-9 && ghosted && instance.IsGhostPaused && Math.Abs(playingAt - 51.9) < 1e-9,
            quick ? "with the engine's _transport held elsewhere, 500 paused Seeks and a play-scrub's GhostPause with 500 playing Seeks each take under 5 ms; the clock reads each final target"
                : F($"with the engine's _transport held elsewhere: 500 paused Seeks {paused:0.000} ms, GhostPause with 500 playing Seeks {playing:0.000} ms (each < 5); targets {pausedAt:0.###}, {playingAt:0.###}"));

        // The engine's wiring, read from the source.
        var seek = SourceProbe.MemberOfType("GpuPreviewEngine", "public void Seek(");
        expect(!Has(seek, "lock (_transport)") && !Has(seek, "_audio.Seek(") && Has(seek, "Volatile.Write(ref _pausedAt")
               && SourceProbe.InOrder(seek, "SeekMailSlot.Post(", "Interlocked.Increment(ref _transportSerial)"),
            "Seek takes no lock and never seeks the audio engine: it writes the clock, posts, then bumps the serial (a turn reading the new serial sees the target pending)");
        var drain = SourceProbe.MemberOfType("GpuPreviewEngine", "private void DrainSeek(");
        expect(SourceProbe.InOrder(drain, "lock (_transport)", "_audio.Seek(", "Interlocked.CompareExchange(ref _pendingSeekBits, SeekMailSlot.Empty, bits)"),
            "DrainSeek applies the newest target to the audio engine under _transport and empties the slot only afterwards (a newer target stays)");
        var pump = SourceProbe.MemberOfType("GpuPreviewEngine", "private void Pump(");
        expect(SourceProbe.InOrder(pump, "ApplyGhostPause();", "DrainSeek();", "_scaleResetOwed", "EndScaledRun()") && SourceProbe.InOrder(pump, "EndPriming();", "ApplyGhostPause();", "DrainSeek();", "_audio.Position"),
            "the pump applies ghost pauses and drains seeks at the top of every turn, and again before a playing turn measures its moment");
        expect(SourceProbe.InOrder(SourceProbe.MemberOfType("GpuPreviewEngine", "private double PlayingPosition("), "Volatile.Read(ref _pendingSeekBits)", "_audio.Position")
               && Has(SourceProbe.MemberOfType("GpuPreviewEngine", "private void PauseAtEnd("), "SeekMailSlot.TryCommitEnd(ref _transportSerial, serial, ref _pendingSeekBits)")
               && SourceProbe.InOrder(pump, "var serial = Volatile.Read(ref _transportSerial);", "SeekMailSlot.IsPending(ref _pendingSeekBits)", "PauseAtEnd("),
            "the playing clock shows a posted target until the audio clock has it; the Sequence's end is never measured on a clock a pending seek has not moved");
        var engine = SourceProbe.Src("Services", "GpuPreviewEngine.cs");
        expect(!Has(engine, "_transportSerial++"), "the transport serial is only ever bumped atomically (Seek bumps it without the lock)");
        expect(Has(SourceProbe.MemberOfType("GpuPreviewEngine", "public void Play("), "SeekMailSlot.ResumeFrom(ref _pendingSeekBits,"), "public void Play folds a pending seek in itself");
        foreach (var signature in new[] { "public void Pause(", "private void ApplyGhostPause(" })
            expect(Has(SourceProbe.MemberOfType("GpuPreviewEngine", signature), "SeekMailSlot.TryTake("), $"{signature.Trim('(')} folds a pending seek in itself");

        // A1-1: a drain read target A, the UI posted B, the drain then wrote A to the paused clock: the paused clock and Play both use B.
        var (slotB, staleA) = (SeekMailSlot.Empty, 3.0);
        SeekMailSlot.Post(ref slotB, 7.5);
        var shownB = SeekMailSlot.PeekOr(ref slotB, staleA);
        var resumed = SeekMailSlot.ResumeFrom(ref slotB, staleA, 60, 1);
        Field("_pendingSeekBits").SetValue(instance, BitConverter.DoubleToInt64Bits(7.5));
        Field("_pausedAt").SetValue(instance, staleA);
        Field("_playing").SetValue(instance, false);
        expect(shownB == 7.5 && resumed == 7.5 && !SeekMailSlot.IsPending(ref slotB) && instance.Position == 7.5 && SeekMailSlot.ResumeFrom(ref slotB, 60, 60, 1) == 0,
            "a target posted after a drain read the older one: the paused clock shows it and Play resumes from it (from the end, Play restarts at 0)");

        // A1-2: the end's commit point against a lock-free Seek (posts, then bumps the serial).
        var (serial, slot2) = (5L, SeekMailSlot.Empty);
        var clean = SeekMailSlot.TryCommitEnd(ref serial, 5, ref slot2) && serial == 6;
        SeekMailSlot.Post(ref slot2, 4);
        var postedNotBumped = !SeekMailSlot.TryCommitEnd(ref serial, 6, ref slot2);
        serial++;
        var bumped = !SeekMailSlot.TryCommitEnd(ref serial, 7, ref slot2) && serial == 8;
        expect(clean && postedNotBumped && bumped,
            "the end commits in one compare-exchange: a seek posted before it (serial bumped or not yet) keeps playback going; one after it lands on the paused clock");
    }

    private static void ScrubGateCase(Action<bool, string> expect)
    {
        var raised = 0;
        void Count() => raised++;
        ScrubGate.Released += Count;
        try
        {
            ScrubGate.Hold();
            ScrubGate.Hold();
            var first = ScrubGate.Release();
            var second = ScrubGate.Release();
            expect(first && !second && raised == 1 && !ScrubGate.IsHeld, "held twice, released twice: Released is raised exactly once and the gate is open");
        }
        finally
        {
            ScrubGate.Released -= Count;
        }

        var scrub = SourceProbe.MemberOfType("MainWindow", "private void ScrubPlayheadTo(");
        expect(SourceProbe.InOrder(scrub, "ScrubGate.Hold()", "GhostPauseForScrub()", "PlayerSeek(", "Scrub.Move("), "a scrub move holds the gate, ghost-pauses, seeks, then queues the commit");
        var end = SourceProbe.MemberOfType("MainWindow", "private void EndScrub(");
        expect(SourceProbe.InOrder(end, "_scrub?.Release(", "ReleaseScrubGate()", "EndScrubScaling()"), "release commits the last moment, opens the gate, then ends the scaled run");
        var release = SourceProbe.MemberOfType("MainWindow", "private void ReleaseScrubGate(");
        expect(SourceProbe.InOrder(release, "ScrubGate.Release()", "_scrubState.RedrawOwed", "RedrawForClipsShown()"), "the deferred box rebuild runs once as the gate opens");
        var layout = SourceProbe.MemberOfType("MainWindow", "private void InitializePlayerLayout(");
        expect(SourceProbe.InOrder(layout, "case nameof(MainViewModel.PositionMs):", "ScrubDefersLayout", "_scrubState.RedrawOwed = true", "RedrawForClipsShown()"),
            "a held scrub's commits skip the player/Canvas box rebuild (LayerBox and LayerHitMask work at cut boundaries)");

        // Wave 1: the Effects pane skips animated-value refreshes while held and refreshes once on release.
        var effects = SourceProbe.Src("Views", "EffectsPane.xaml.cs");
        expect(Has(effects, "ScrubGate.IsHeld") && Has(effects, "ScrubGate.Released"), "Wave 1: EffectsPane defers OnPlayheadMoved while ScrubGate.IsHeld and catches up on Released");
    }

    private static void GhostPauseCase(Action<bool, string> expect)
    {
        expect(GpuPreviewEngine.GhostDwell == 0.080 && GpuPreviewEngine.GhostDwell == GpuPreviewEngine.CrispAfter, "the pointer rest that plays a segment is 80 ms, CrispAfter (user decision)");
        var ghost = SourceProbe.MemberOfType("GpuPreviewEngine", "public bool GhostPause(");
        expect(!Has(ghost, "lock (") && !Has(ghost, "PauseCore(") && !Has(ghost, "_audio.")
               && SourceProbe.InOrder(ghost, "_ghostPaused = true", "_ghostOwed", "Interlocked.Increment(ref _transportSerial)", "_scaleResetOwed"),
            "GhostPause is lock-free on the UI thread: it marks the hold, posts the request, cancels a measured end and ends the scaled run");
        var apply = SourceProbe.MemberOfType("GpuPreviewEngine", "private void ApplyGhostPause(");
        expect(SourceProbe.InOrder(apply, "Interlocked.Exchange(ref _ghostOwed, 0)", "lock (_transport)", "_ghostPaused", "PauseCore(") && !Has(apply, "AppLog.Write") && !Has(apply, "Ended"),
            "the pump pauses the audio under _transport unless the hold was dropped first; no session log line and no Ended");
        var resume = SourceProbe.MemberOfType("GpuPreviewEngine", "public bool GhostResume(");
        expect(SourceProbe.InOrder(resume, "_ghostPaused = false", "Play()"), "GhostResume drops the hold and plays from the scrubbed clock");
        foreach (var signature in new[] { "public void Play(", "public void Pause(" })
            expect(Has(SourceProbe.MemberOfType("GpuPreviewEngine", signature), "_ghostPaused = false"), $"{signature.Trim('(')} drops a ghost hold (Space during a play-scrub wins)");
        var dwell = SourceProbe.MemberOfType("MainWindow", "private DispatcherTimer NewGhostDwell(");
        expect(Has(dwell, "GpuPreviewEngine.GhostDwell") && SourceProbe.InOrder(dwell, "IsScrubHeld", "GhostResume()"), "a rest while still held plays on from the scrubbed moment");
        var end = SourceProbe.MemberOfType("MainWindow", "private void EndScrub(");
        expect(SourceProbe.InOrder(end, "IsGhostPaused", "ResumeAfterScrub", "DispatcherPriority.Input"), "release resumes the held play after the caller's exact release seek");
    }

    private static void ScalingCase(Action<bool, string> expect)
    {
        expect(!GpuPreviewEngine.SlowByDraw(7.9, bigCardOnAc: true) && GpuPreviewEngine.SlowByDraw(8.1, bigCardOnAc: true), "a big card on AC power: slow only past 8 ms of shader draw");
        expect(!GpuPreviewEngine.SlowByDraw(11.9, bigCardOnAc: false) && GpuPreviewEngine.SlowByDraw(12.1, bigCardOnAc: false) && !GpuPreviewEngine.SlowByDraw(double.NaN, false),
            "other devices: slow only past SlowDrawMs (12 ms) of shader draw; an unknown draw time is not slow");

        var (onTime, stepUp) = (0, false);
        for (var i = 0; i < GpuPreviewEngine.RecoverAfter - 1; i++)
            (onTime, stepUp) = GpuPreviewEngine.Recovered(onTime, slowFrame: false, step: 3, start: 0);
        expect(onTime == 29 && !stepUp, "29 on-time frames: no step yet");
        (onTime, stepUp) = GpuPreviewEngine.Recovered(onTime, slowFrame: false, step: 3, start: 0);
        expect(onTime == 0 && stepUp, "the 30th on-time frame steps up and restarts the streak");
        expect(GpuPreviewEngine.Recovered(17, slowFrame: true, step: 3, start: 0) == (0, false) && GpuPreviewEngine.Recovered(17, false, step: 1, start: 1) == (0, false),
            "a slow frame resets the streak; a run at its start step never steps up");

        // 1280 x 720 viewport, 1080p Sequence, 100 %: step 1 is whole, so 0.5 -> 0.75 -> 1.0.
        float Scale(int step) => GpuPreviewEngine.StepScale(step, 1280, 720, 1920, 1080, 1);
        var up1 = GpuPreviewEngine.PrevStep(3, 0, 1280, 720, 1920, 1080, 1);
        var up2 = GpuPreviewEngine.PrevStep(up1, 0, 1280, 720, 1920, 1080, 1);
        expect(Scale(3) == 0.5f && up1 == 2 && Scale(up1) == 0.75f && Scale(up2) == 1f, F($"recovery goes 0.5 -> {Scale(up1):0.00} -> {Scale(up2):0.00}"));
        expect(GpuPreviewEngine.PrevStep(3, 2, 1280, 720, 1920, 1080, 1) == 2 && GpuPreviewEngine.PrevStep(0, 0, 1280, 720, 1920, 1080, 1) == 0,
            "never above the run's start step; a whole run stays whole");

        // Wave 1: CountFrame counts by shader draw time only and recovers upward.
        var count = SourceProbe.MemberOfType("GpuPreviewEngine", "private void CountFrame(");
        expect(Has(count, "SlowByDraw(") && Has(count, "Recovered(") && !Has(count, "LastAcquireMs"),
            "Wave 1: CountFrame uses SlowByDraw (no acquire time, no lateness) and Recovered/PrevStep to step back up");
    }

    private static void FilmCropCase(Action<bool, string> expect)
    {
        expect(FilmCrop.Full.IsFull && default(FilmCrop).IsFull && !new FilmCrop(0.1, 0, 1, 1).IsFull, "full, empty and partial regions are told apart");
        var crop = FilmCrop.FromRect(0.75, 0.05, 0.2, 0.15);
        var outline = FilmCrop.Bounds([new[] { new Point(0.1, 0.1), new Point(0.5, 0.1), new Point(0.5, 0.3) }], 0.5, 0, 0.5, 0.5, offsetX: 0.1);
        expect(outline is { } o && Math.Abs(o.UMin - 0.6) < 1e-9 && Math.Abs(o.UMax - 0.8) < 1e-9 && Math.Abs(o.VMin - 0.05) < 1e-9 && Math.Abs(o.VMax - 0.15) < 1e-9,
            "outline bounds map through the part rect with the mask offset");
        var both = crop.Intersect(outline!.Value);
        expect(Math.Abs(both.UMin - 0.75) < 1e-9 && Math.Abs(both.UMax - 0.8) < 1e-9 && Math.Abs(both.VMin - 0.05) < 1e-9 && Math.Abs(both.VMax - 0.15) < 1e-9,
            "mask bounds intersected with the crop rect (user decision)");
        expect(crop.Intersect(new FilmCrop(0, 0, 0.1, 0.1)) == crop, "disjoint regions keep the crop");

        var tiny = new FilmCrop(0.5, 0.5, 0.52, 0.51).ToPixels(160, 90);
        expect(tiny.Width == FilmCrop.MinStripPixels && tiny.Height == FilmCrop.MinStripPixels && Math.Abs(tiny.X + tiny.Width / 2.0 - 81.6) <= 1 && Math.Abs(tiny.Y + tiny.Height / 2.0 - 45.45) <= 1,
            F($"a tiny HUD region is cut at 16 x 16 strip pixels about its centre ({tiny.X},{tiny.Y} {tiny.Width}x{tiny.Height})"));
        var edge = new FilmCrop(0.99, 0.99, 1, 1).ToPixels(160, 90);
        expect(edge.X + edge.Width <= 160 && edge.Y + edge.Height <= 90 && edge.Width == 16 && edge.Height == 16, "the floor stays inside the frame at its edge");

        // A 4-frame 640 x 90 strip of a 1080p file: a killfeed region is cut from every frame (24 x 16 here), the whole clip keeps 160 x 90.
        var pixels = new byte[640 * 90 * 4];
        var strip = BitmapSource.Create(640, 90, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
        strip.Freeze();
        var whole = MainWindow.FilmOf(strip, 1920, 1080, 0, 10, 1);
        var cut = MainWindow.FilmOf(strip, 1920, 1080, 0, 10, 1, new FilmCrop(0.8, 0, 0.95, 0.1));
        expect(whole is { Frames: [BitmapSource { PixelWidth: 160, PixelHeight: 90 }, ..] } && cut is { Frames.Count: 4 } c && c.Frames[0] is BitmapSource { PixelWidth: 24, PixelHeight: 16 },
            "FilmOf cuts each strip frame to the Cutout's region, with the floor, and leaves other clips whole");
        expect(UnifiedTimelineControl.FilmAspectRange is { Least: > 0, Most: > 1 }
               && SourceProbe.InOrder(SourceProbe.MemberOfType("UnifiedTimelineControl", "private static void DrawFilm("), "var (pictureWidth, pictureHeight)", "drawing.DrawImage(frames[index], new Rect(origin + k * slot + (slot - pictureWidth) / 2"),
            "DrawFilm holds a sliver's slot aspect to a range and letterboxes the picture at its own shape, never stretched");

        var cutout = SourceProbe.MemberOfType("MainWindow", "internal static FilmCrop CutoutFilmCrop(");
        expect(SourceProbe.InOrder(cutout, "CroppedSource", "MaskInvert", "ParseShapes(", ".Intersect("), "a Cutout's region: cropped source, then (unless inverted) its outline's bounds intersected");
    }

    private static void GizmoCase(Action<bool, string> expect)
    {
        // A 960 x 540 box on a 1080p frame (half each way), dragged by its bottom-right corner.
        var along = MainWindow.DiagonalDelta(GripCorner.BottomRight, 0.5, 0.5, 0.05, 0.05, 1920, 1080);
        var down = MainWindow.DiagonalDelta(GripCorner.BottomRight, 0.5, 0.5, 0, 0.1, 1920, 1080);
        var across = MainWindow.DiagonalDelta(GripCorner.BottomRight, 0.5, 0.5, 0.1, 0, 1920, 1080);
        var upLeft = MainWindow.DiagonalDelta(GripCorner.TopLeft, 0.5, 0.5, 0, -0.1, 1920, 1080);
        expect(Math.Abs(along.X - 0.05) < 1e-9 && Math.Abs(along.Y - 0.05) < 1e-9, "a move along the diagonal is unchanged");
        expect(down.X > 0.01 && across.X > 0.01 && upLeft.X < -0.01 && Math.Abs(down.X - down.Y) < 1e-9, "vertical and horizontal moves resize along the diagonal, outward from either corner");
        var resized = MainWindow.ResizedFromCorner(0.2, 0.2, 0.5, 0.5, 0.5, 0.5, GripCorner.BottomRight, down.X, down.Y, heightFollows: true, 0.5, fromOpposite: false);
        expect(resized.Width > 0.5, F($"an aspect-locked box grows on a straight-down drag (width {resized.Width:0.000})"));

        expect(GizmoHitRules.AnchorTakes(new Point(102.9, 97.1), new Point(100, 100), 200, 120) && !GizmoHitRules.AnchorTakes(new Point(104, 100), new Point(100, 100), 200, 120)
            && !GizmoHitRules.AnchorTakes(new Point(100, 100), new Point(100, 100), 35, 120) && GizmoHitRules.AnchorDotSize == 9,
            "the 9 x 9 dot takes a 6 x 6 hit, and none on a box under 36 DIP (user decision)");

        foreach (var signature in new[] { "private void MoveTrackLayer(", "private void ResizeTrackLayer(", "private void CropCanvas_MouseMove(", "private void WireGizmo(" })
            expect(Has(SourceProbe.MemberOfType("MainWindow", signature), "BatchLookWrites()"), $"{signature.Replace("private void ", "").Trim('(')} batches its look writes");
        expect(Has(SourceProbe.MemberOfType("MainWindow", "private void ResizeTrackLayer("), "DiagonalDelta("), "aspect-locked resizes take the diagonal projection");

        // An anchor at the moving edge (0.9 of the width): a 54 px straight-down drag scales by its projection on the anchor-to-corner span
        // (270 px tall), never amplified by the narrow horizontal share.
        var near = MainWindow.DiagonalDelta(GripCorner.BottomRight, 0.5, 0.5, 0, 0.05, 1920, 1080, 0.1, 0.5);
        var scaled = MainWindow.ResizedFromCorner(0.2, 0.2, 0.5, 0.5, 0.9, 0.5, GripCorner.BottomRight, near.X, near.Y, heightFollows: true, 1, fromOpposite: false);
        expect(scaled.Width > 0.5 && scaled.Width / 0.5 <= 1 + 54.0 / 270 + 1e-9,
            F($"an anchor at the moving edge does not amplify a vertical drag (x{scaled.Width / 0.5:0.000}, at most x1.200)"));

        // Guide snapping of a single-corner resize: the moving edge only, the fixed corner stays, a locked box keeps its shape.
        var free = MainWindow.SnappedCorner((0.2, 0.2, 0.695, 0.5), GripCorner.BottomRight, 0, 0, heightFollows: false, 1, 6 / 960.0, 6 / 540.0);
        var locked = MainWindow.SnappedCorner((0.2, 0.2, 0.695, 0.695), GripCorner.BottomRight, 0, 0, heightFollows: true, 1, 6 / 960.0, 6 / 540.0);
        var far = MainWindow.SnappedCorner((0.2, 0.2, 0.6, 0.6), GripCorner.BottomRight, 0, 0, heightFollows: true, 1, 6 / 960.0, 6 / 540.0);
        expect(Math.Abs(free.X - 0.2) < 1e-12 && Math.Abs(free.X + free.Width - 0.9) < 1e-12 && free.Height == 0.5
               && Math.Abs(locked.Width - 0.7) < 1e-12 && Math.Abs(locked.Height - 0.7) < 1e-12 && far == (0.2, 0.2, 0.6, 0.6),
            "a corner resize snaps only its moving edge to a guide within reach (the fixed corner stays, a locked box keeps its shape, out of reach nothing moves)");
        var resize = SourceProbe.MemberOfType("MainWindow", "private void ResizeTrackLayer(");
        expect(SourceProbe.InOrder(resize, "ModifierKeys.Alt", "SnappedCorner("), "resize snapping honours Alt and the Snap Layout Guides setting");

        // Wave 1: the adorner's anchor hit uses the rule.
        expect(Has(SourceProbe.Src("Controls", "LayoutGizmos.cs"), "GizmoHitRules.AnchorTakes("), "Wave 1: LayerGizmoAdorner hit-tests its anchor dot with GizmoHitRules.AnchorTakes");
    }

    private static void AvSyncCase(Action<bool, string> expect)
    {
        var folder = Path.Combine(SourceProbe.Root, "tools", "AvSync");
        var text = string.Join("\n", Directory.GetFiles(folder, "*.cs").Select(File.ReadAllText));
        expect(text.Contains("Steam Streaming", StringComparison.Ordinal) && text.Contains("Remote Audio", StringComparison.Ordinal)
            && text.Contains("SKIPPED (virtual audio sink)", StringComparison.Ordinal),
            "Wave 1: Steam Streaming sinks count as virtual like Remote Audio; hardware endpoints first, else SKIPPED (virtual audio sink)");
    }
}
