using System.Windows;
using System.Windows.Media;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Shell;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.7.1 lane L3: frame-edge playhead scrubbing (raw moves only stamped, the live cursor sampled once per rendered frame, one engine
/// seek per frame, the Overview Scrubber frozen mid-drag), the pointer rest timed by timestamp, the Cursor-Locked Scrub Pointer (off by
/// default, drawn in the playhead visual, always given back), and the Slice-Preview ribbon (velocity hysteresis, video clips only,
/// spacing readout, the shared-memory burst and its strip). Every line says what it proved.
/// </summary>
public static class ScrubAndSlice571Tests
{
    private const long Frequency = 10_000_000;

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("scrub-and-slice-571-frame-edge", "# Frame-edge scrub: raw moves stamped, the live cursor sampled per frame, one engine seek per frame, the Overview Scrubber frozen mid-drag.", FrameEdge);
        yield return Case("scrub-and-slice-571-ghost-dwell", "# Pointer rest by timestamp: a held play-scrub plays on 80 ms after the last move to a new target, once per rest.", GhostDwell);
        yield return Case("scrub-and-slice-571-cursor-lock", "# Cursor-Locked Scrub Pointer: off by default, drawn inside the playhead visual, the OS cursor always given back.", CursorLock);
        yield return Case("scrub-and-slice-571-velocity-gate", "# Slice-Preview gate: dormant above 300 DIP/s, shown after 120 ms below 250 DIP/s, kept in between, asleep off video.", VelocityGate);
        yield return Case("scrub-and-slice-571-video-only", "# Slice-Preview request: video clips only (Q3), slots inside the clip under the playhead, file times through speed.", VideoOnly);
        yield return Case("scrub-and-slice-571-spacing-readout", "# Slice-Preview spacing for the zoom and its readout (+-1f .. +-1s), slot count, placement and cell sizes.", SpacingReadout);
        yield return Case("scrub-and-slice-571-burst-seam", "# Slice-Preview burst: posted by the pump, decoded by the worker behind visible work, answered, drawn as one strip in place.", BurstSeam);
    }

    private static string ScrubFile => SourceProbe.Src("MainWindow.Scrub.cs");

    private static string RulerFile => SourceProbe.Src("Controls", "TimelineParts.cs");

    // ----- scrub-and-slice-571-frame-edge -----

    private static void FrameEdge(Action<bool, string> expect)
    {
        var flushed = new List<double>();
        var scrub = new ScrubCoalescer(flushed.Add);
        var seeks = new List<double>();
        for (var frame = 0; frame < 10; frame++)
        {
            for (var step = 0; step < 16; step++)
                scrub.Move(frame * 100 + step);
            if (scrub.TakeSeek(out var at))
                seeks.Add(at);
            scrub.Tick(frame * 16.7);
        }

        expect(seeks.Count == 10 && seeks.Select((at, frame) => at == frame * 100 + 15).All(ok => ok),
            "16 raw moves per 60 Hz frame (a 1000 Hz pointer) hand out one engine seek per frame, to the newest target");
        expect(!scrub.TakeSeek(out _), "a second TakeSeek in the same frame hands out nothing");
        scrub.Move(915);
        expect(!scrub.HasSeek && !scrub.TakeSeek(out _), "a still pointer (a move to the target last sought) owes no seek");
        scrub.Move(950);
        scrub.Move(915);
        expect(!scrub.TakeSeek(out _), "a move away and back within one frame owes nothing (the engine is already there)");
        scrub.Move(990);
        var owed = scrub.TakeSeek(out var last) && last == 990;
        scrub.Release(500);
        expect(owed && flushed[^1] == 990 && !scrub.IsHeld && !scrub.HasSeek && !scrub.HasPending,
            "release: the last owed seek is taken, then PositionMs is committed once with the last position");
        scrub.Move(915);
        expect(scrub.TakeSeek(out var again) && again == 915, "a new drag starts with nothing handed out: its first move seeks, even to the last drag's target");
        scrub.Release(600);
        scrub.Move(300, sought: true);
        var firstOwes = scrub.HasSeek;
        scrub.Move(301);
        expect(!firstOwes && scrub.TakeSeek(out var next) && next == 301 && !scrub.TakeSeek(out _),
            "a drag's first move, sought at once by the host (press latency), owes no second seek; the next move owes the frame's one");

        expect(double.IsNaN(TimelineRuler.SampleTarget(120, 120, 800)) && TimelineRuler.SampleTarget(121, 120, 800) == 121
               && TimelineRuler.SampleTarget(-40, 0.5, 800) == 0 && TimelineRuler.SampleTarget(900, 10, 800) == 800
               && double.IsNaN(TimelineRuler.SampleTarget(900, 800, 800)) && double.IsNaN(TimelineRuler.SampleTarget(10, 5, 0))
               && double.IsNaN(TimelineRuler.SampleTarget(double.NaN, 5, 800)),
            "a frame sample seeks only to a new x, clamped to the ruler; the same x, no layout or no cursor seeks nothing");

        var rendered = TimeSpan.FromMilliseconds(16.7);
        expect(TimelineRuler.SamplesFrame(rendered, TimeSpan.MinValue) && !TimelineRuler.SamplesFrame(rendered, rendered)
               && TimelineRuler.SamplesFrame(rendered + TimeSpan.FromMilliseconds(2), rendered) && TimelineRuler.SamplesFrame(null, rendered),
            "one cursor sample per rendered frame: a second render callback of the frame (same RenderingTime) skips it; a direct call samples");

        // Frame N's cursor sampled, then the frame's one seek taken (one ordered callback), against the old split hooks (seek drained first).
        var cursor = new[] { 100.0, 130, 170, 220, 280 };
        var (ordered, split) = (new ScrubCoalescer(_ => { }), new ScrubCoalescer(_ => { }));
        var (orderedSeeks, splitSeeks) = (new List<double>(), new List<double>());
        ordered.Move(90, sought: true);
        split.Move(90, sought: true);
        foreach (var x in cursor)
        {
            ordered.Move(x);
            if (ordered.TakeSeek(out var sameFrame))
                orderedSeeks.Add(sameFrame);
            if (split.TakeSeek(out var lastFrame))
                splitSeeks.Add(lastFrame);
            split.Move(x);
        }

        expect(orderedSeeks.SequenceEqual(cursor) && splitSeeks.SequenceEqual(cursor[..^1]),
            "sampled before the seek in one callback, the seek consumed on frame N is frame N's cursor (drained first, it was frame N-1's)");

        var ruler = RulerFile;
        var move = SourceProbe.Member(ruler, "protected override void OnMouseMove(");
        expect(move.Contains("_lastScrubMoveTicks", StringComparison.Ordinal) && !move.Contains("SeekRequested", StringComparison.Ordinal),
            "a seek drag's raw WM_MOUSEMOVE only stamps _lastScrubMoveTicks: no seek, no event per move");
        expect(SourceProbe.InOrder(SourceProbe.Member(ruler, "private void OnScrubFrame("), "GetCursorPos(", "PointFromScreen(", "SeekToSample(")
               && SourceProbe.Member(ruler, "private void BeginScrubSampling(").Contains("CompositionTarget.Rendering += OnScrubFrame", StringComparison.Ordinal)
               && SourceProbe.InOrder(SourceProbe.Member(ruler, "protected override void OnMouseLeftButtonUp("), "SeekToSample(", "EndScrubSampling();", "SeekEnded?.Invoke();"),
            "once per rendered frame (CompositionTarget.Rendering) the live cursor (GetCursorPos) is mapped straight to ruler x and seeks; the release lands where the button came up");

        var main = ScrubFile;
        var seek = SourceProbe.Member(main, "private void ScrubSeek(");
        expect(SourceProbe.InOrder(SourceProbe.Member(main, "private void OnScrubFrame("), "SeekOwed();", "Scrub.Tick(", "GhostDwellFrame(now);", "SlicePreviewFrame(now);")
               && SourceProbe.InOrder(SourceProbe.Member(main, "private void ScrubPlayheadTo("), "if (_scrubState.FrameHooked)", "Scrub.Move(positionMs);", "return;",
                   "ScrubSeek((long)positionMs);", "Scrub.Move(positionMs, sought: true);", "CompositionTarget.Rendering += OnScrubFrame;")
               && seek.Contains("engine.ScrubSeek(", StringComparison.Ordinal) && !seek.Contains("engine.Seek(", StringComparison.Ordinal)
               && SourceProbe.InOrder(SourceProbe.Member(main, "private void EndScrub("), "UnhookScrubFrame();", "SeekOwed();", "_scrub?.Release("),
            "MainWindow seeks the engine once per rendered frame through GpuPreviewEngine.ScrubSeek (repeats skipped), the last owed seek before the release commit");
        expect(SourceProbe.InOrder(SourceProbe.Member(main, "private void OnScrubFrame("), "SampleScrubRulers(e);", "if (!_scrubState.FrameHooked)", "SeekOwed();", "Scrub.Tick(")
               && SourceProbe.InOrder(SourceProbe.Member(main, "private void SampleScrubRulers("), "if (ruler.IsSampling)", "ruler.SampleScrubFrame(e);")
               && SourceProbe.InOrder(SourceProbe.Member(ruler, "private void OnScrubFrame("), "RenderingTime", "SamplesFrame(", "_sampledFrame = time;", "GetCursorPos(")
               && SourceProbe.Member(ruler, "internal void SampleScrubFrame(").Contains("if (_scrubHooked)", StringComparison.Ordinal),
            "one ordered frame callback: MainWindow samples the held ruler's live cursor before the frame's one engine seek; the ruler's own hook skips a frame already sampled (no capture drag held: nothing sampled)");
        expect(!main.Contains("new DispatcherTimer", StringComparison.Ordinal) && !main.Contains("GhostDwell?.", StringComparison.Ordinal)
               && !SourceProbe.Src("Shell", "ScrubState.cs").Contains("public DispatcherTimer", StringComparison.Ordinal),
            "no DispatcherTimer is stopped and restarted per move any more (ScrubState keeps stamps, not a timer)");

        var player = SourceProbe.Src("MainWindow.Player.cs");
        var freeze = SourceProbe.Member(player, "private void FreezeOverviewSlider(");
        expect(SourceProbe.InOrder(freeze, "ClearBinding(TimelineSlider", "TimelineSlider.Value = value;") && freeze.Contains("SetBinding(RangeBase.ValueProperty, binding)", StringComparison.Ordinal)
               && SourceProbe.Member(player, "private void TimelineSlider_ValueChanged(").Contains("_scrubState.OverviewSwapping", StringComparison.Ordinal)
               && SourceProbe.InOrder(SourceProbe.Member(main, "private void ScrubPlayheadTo("), "if (starting && !_isScrubbing)", "FreezeOverviewSlider(true);")
               && SourceProbe.InOrder(SourceProbe.Member(main, "private void ReleaseScrubGate("), "FreezeOverviewSlider(false);", "ScrubGate.Release()"),
            "a ruler drag takes the Overview Scrubber's binding off (thumb left in place, echoes ignored, thumb drags exempt) and puts it back as the gate opens");
    }

    // ----- scrub-and-slice-571-ghost-dwell -----

    private static void GhostDwell(Action<bool, string> expect)
    {
        var dwell = GpuPreviewEngine.GhostDwell;
        var moved = 5 * Frequency;
        expect(Math.Abs(dwell - 0.08) < 1e-9, "GhostDwell is 80 ms (user decision, CrispAfter)");
        expect(!ScrubState.DwellDue(moved + 79 * Frequency / 1000, moved, Frequency, dwell) && ScrubState.DwellDue(moved + 80 * Frequency / 1000, moved, Frequency, dwell)
               && ScrubState.DwellDue(moved + Frequency, moved, Frequency, dwell),
            "the rest is due from 80 ms after the last move, by timestamp");
        expect(!ScrubState.DwellDue(123_456_789, 0, Frequency, dwell) && !ScrubState.DwellDue(moved + Frequency, moved, 0, dwell), "never before a first move, nor without a clock");

        // A 1000 Hz drag for 300 ms, then a rest, evaluated each 60 Hz frame: due on the first frame 80 ms past the last move only.
        var lastMove = 0L;
        var dueFrames = new List<int>();
        for (var frame = 0; frame < 40; frame++)
        {
            var now = (long)(frame * Frequency / 60.0);
            if (now <= 0.3 * Frequency)
                lastMove = now;
            if (ScrubState.DwellDue(now, lastMove, Frequency, dwell))
                dueFrames.Add(frame);
        }

        expect(dueFrames.Count > 0 && dueFrames[0] == 23 && (dueFrames[0] * Frequency / 60.0 - 0.3 * Frequency) >= 0.08 * Frequency,
            $"a drag that stops at 300 ms rests from frame {(dueFrames.Count > 0 ? dueFrames[0] : -1)} (383 ms), not before");

        var main = ScrubFile;
        expect(SourceProbe.InOrder(SourceProbe.Member(main, "private void GhostDwellFrame("), "_scrubState.DwellResumed", "DwellDue(", "_scrubState.DwellResumed = true;",
                   "engine.PrepareAround(", "engine.GhostResume();")
               && SourceProbe.InOrder(SourceProbe.Member(main, "private void ScrubPlayheadTo("), "if (!positionMs.Equals(_scrubState.LastTargetMs))",
                   "_scrubState.LastMoveTicks = Stopwatch.GetTimestamp();", "_scrubState.DwellResumed = false;", "GhostPauseForScrub();"),
            "each frame of a held scrub plays on once per rest (after caption pre-roll); only a move to a new target restarts the rest and holds the transport again");
    }

    // ----- scrub-and-slice-571-cursor-lock -----

    private static void CursorLock(Action<bool, string> expect)
    {
        expect(!new AppSettings().CursorLockedScrubPointer, "the Cursor-Locked Scrub Pointer is off in a new install (user decision Q1)");
        expect(!TimelineRuler.HidesCursor(false, true, true) && TimelineRuler.HidesCursor(true, true, false) && TimelineRuler.HidesCursor(true, false, true)
               && !TimelineRuler.HidesCursor(true, false, false),
            "the OS cursor is hidden only for a locked drag while the pointer is drawn (over the ruler or the tracks); elsewhere it shows");

        EnsureApplication();
        var ruler = new TimelineRuler();
        var playhead = (DrawingVisual)VisualTreeHelper.GetChild(ruler, 0);
        ruler.PlayheadX = 40;
        ruler.PointerY = 3;
        var pointer = playhead.Children.Count == 1 ? playhead.Children[0] as DrawingVisual : null;
        expect(pointer is { Opacity: 1 } && pointer.Offset == new Vector(0.75, 3) && playhead.Offset == new Vector(40, 0),
            "the ruler draws the pointer as a child of its playhead visual, its tip on the line's centre");
        ruler.PlayheadX = 90;
        expect(playhead.Offset.X == 90 && pointer?.Offset.X == 0.75, "moving the playhead carries the pointer by the same translation (0 px apart)");
        ruler.PointerY = double.NaN;
        expect(pointer?.Opacity == 0 && playhead.Children.Count == 1, "NaN hides it (kept for the next drag)");

        var ended = 0;
        ruler.SeekEnded += () => ended++;
        ruler.CancelSeek();
        expect(ended == 0 && !ruler.PointerLocked && !ruler.IsSampling && ruler.ReadLocalValue(FrameworkElement.CursorProperty) == DependencyProperty.UnsetValue,
            "cancelling with no drag held ends nothing and leaves the cursor alone");

        var source = RulerFile;
        expect(SourceProbe.Member(source, "protected override void OnLostMouseCapture(").Contains("EndScrubSampling();", StringComparison.Ordinal)
               && SourceProbe.Member(source, "protected override void OnMouseLeftButtonUp(").Contains("EndScrubSampling();", StringComparison.Ordinal)
               && SourceProbe.InOrder(SourceProbe.Member(source, "private void OnScrubFrame("), "Keyboard.IsKeyDown(Key.Escape)", "CancelSeek();", "catch (Exception", "CancelSeek();")
               && SourceProbe.InOrder(SourceProbe.Member(source, "public TimelineRuler()"), "Unloaded +=", "CancelSeek();", "IsVisibleChanged +=", "CancelSeek();")
               && SourceProbe.Member(source, "private void EndScrubSampling(").Contains("ShowPointer(double.NaN", StringComparison.Ordinal),
            "the cursor is given back on release, capture loss (window deactivated, a dialog), Esc, hiding, unloading and a fault in the frame");
        expect(SourceProbe.InOrder(SourceProbe.Member(source, "private void ShowPointer("), "_cursorBefore = ReadLocalValue(CursorProperty);", "Cursor = Cursors.None;",
                   "Cursor = before;", "ClearValue(CursorProperty);", "Mouse.UpdateCursor();")
               && SourceProbe.Member(source, "private void BeginScrubSampling(").Contains("AppSettings.Current.CursorLockedScrubPointer", StringComparison.Ordinal),
            "a locked drag (read from Settings at the press) hides the cursor on the ruler itself and restores exactly what was there");

        expect(UnifiedTimelineControl.ScrubPointerOver(new Point(300, 40), 168, 900, 200) && !UnifiedTimelineControl.ScrubPointerOver(new Point(100, 40), 168, 900, 200)
               && !UnifiedTimelineControl.ScrubPointerOver(new Point(300, -2), 168, 900, 200) && !UnifiedTimelineControl.ScrubPointerOver(new Point(double.NaN, 4), 168, 900, 200),
            "the tracks draw the pointer only over their time area (not the headers, not above them)");
        var lockSource = SourceProbe.Src("Controls", "UnifiedTimelineControl.ScrubLock.cs");
        var pane = SourceProbe.Src("Views", "UnifiedTimelinePane.xaml.cs");
        expect(lockSource.Contains("_playhead.Children.Add(_scrubPointer);", StringComparison.Ordinal)
               && pane.Contains("Ruler.ScrubPointerElsewhere = screen => Surface.ShowScrubPointerAt(screen);", StringComparison.Ordinal)
               && pane.Contains("Ruler.SeekEnded += () => Surface.ShowScrubPointerAt(null);", StringComparison.Ordinal),
            "over the tracks the pointer is a child of the tracks' playhead visual too, and every drag end hides it");
    }

    // ----- scrub-and-slice-571-velocity-gate -----

    private static void VelocityGate(Action<bool, string> expect)
    {
        var gate = new SlicePreviewGate();
        expect(gate.Update(1200, 0, true) == SlicePreviewPhase.Dormant && gate.Update(301, 10, true) == SlicePreviewPhase.Dormant,
            "a fast whip (above 300 DIP/s) keeps it dormant");
        expect(gate.Update(200, 1000, true) == SlicePreviewPhase.Arming && gate.Update(200, 1119, true) == SlicePreviewPhase.Arming
               && gate.Update(200, 1120, true) == SlicePreviewPhase.Shown,
            "below 250 DIP/s it arms, and shows once that has held 120 ms");
        expect(gate.Update(280, 1200, true) == SlicePreviewPhase.Shown && gate.Update(299.9, 1300, true) == SlicePreviewPhase.Shown
               && gate.Update(0, 1400, true) == SlicePreviewPhase.Shown && gate.Update(300.1, 1500, true) == SlicePreviewPhase.Dormant,
            "once shown, 250-300 DIP/s keeps it (hysteresis) and a still pointer keeps it; above 300 it sleeps at once");
        expect(gate.Update(200, 2000, true) == SlicePreviewPhase.Arming && gate.Update(260, 2050, true) == SlicePreviewPhase.Dormant
               && gate.Update(200, 2060, true) == SlicePreviewPhase.Arming && gate.Update(200, 2179, true) == SlicePreviewPhase.Arming
               && gate.Update(200, 2180, true) == SlicePreviewPhase.Shown,
            "arming broken by 250 DIP/s or more starts over: 120 ms below 250 from the new start");
        expect(gate.Update(100, 2200, false) == SlicePreviewPhase.Dormant && gate.Update(double.NaN, 2300, true) == SlicePreviewPhase.Dormant
               && gate.Update(100, 2400, true) == SlicePreviewPhase.Arming && gate.Sleep() == SlicePreviewPhase.Dormant,
            "off a video clip, or with no speed, it sleeps at once; Sleep (release, playback) too");

        var velocity = 0.0;
        for (var frame = 0; frame < 60; frame++)
            velocity = ScrubState.NextVelocity(velocity, 200 / 60.0, 1 / 60.0);
        var steady = velocity;
        var whip = 2000.0;
        double After(double seconds)
        {
            var v = whip;
            for (var t = 0.0; t < seconds - 1e-9; t += 1 / 60.0)
                v = ScrubState.NextVelocity(v, 0, 1 / 60.0);
            return v;
        }

        expect(Math.Abs(steady - 200) < 0.5 && After(0.2) > SlicePreviewRequest.WakeBelow && After(0.3) < SlicePreviewRequest.WakeBelow
               && ScrubState.NextVelocity(123, 50, 0) == 123 && ScrubState.NextVelocity(123, double.NaN, 0.016) == 123,
            $"the playhead speed is a 120 ms average: a steady 200 DIP/s reads {steady:0.0}; after a 2000 DIP/s whip it falls below 250 between 0.2 and 0.3 s; no time passed keeps it");
    }

    // ----- scrub-and-slice-571-video-only -----

    private static TimelineClip Clip(LayerKind kind, double start, double end, string path = @"C:\v.mp4", double offset = 2, double speed = 1) =>
        new() { ContentKind = kind, Start = start, End = end, SourcePath = path, MediaOffset = offset, Speed = speed };

    private static TimelineDocument Document(params (TimelineTrackKind Kind, TimelineClip[] Clips)[] tracks) =>
        new() { Tracks = [.. tracks.Select(t => new TimelineTrackModel { Kind = t.Kind, Clips = [.. t.Clips] })] };

    private static void VideoOnly(Action<bool, string> expect)
    {
        var spacing = SlicePreviewFrameSpacing.OfFrames(1, 30);
        var video = Document((TimelineTrackKind.Video, [Clip(LayerKind.VideoFile, 10, 20)]));
        var request = SlicePreviewPlanner.Plan(video, 15, spacing, 7, 72);
        expect(request is { IsValid: true, Path: @"C:\v.mp4", Stream: 0, ShortSide: 72 } && request.Slots.Select(s => s.Offset).SequenceEqual([-3, -2, -1, 0, 1, 2, 3])
               && Math.Abs(request.Slots[3].MediaSeconds - 7) < 1e-9 && Math.Abs(request.Slots[4].MediaSeconds - (7 + 1 / 30.0)) < 1e-9
               && Math.Abs(request.Slots[0].SequenceSeconds - (15 - 3 / 30.0)) < 1e-9,
            "over a video clip: seven slots centred on the playhead (offset 0), a frame apart, each with its file time");

        var start = SlicePreviewPlanner.Plan(video, 10, spacing, 7, 72);
        var end = SlicePreviewPlanner.Plan(video, 20 - 1 / 60.0, spacing, 5, 72);
        expect(start is not null && start.Slots.Select(s => s.Offset).SequenceEqual([0, 1, 2, 3]) && end is not null && end.Slots.Select(s => s.Offset).SequenceEqual([-2, -1, 0]),
            "at the clip's edges only the slots inside the clip are asked for (one file, one run of offsets)");

        var fast = SlicePreviewPlanner.Plan(Document((TimelineTrackKind.Video, [Clip(LayerKind.VideoFile, 10, 20, speed: 2)])), 15, spacing, 5, 72);
        expect(fast is not null && Math.Abs(fast.Slots[2].MediaSeconds - 12) < 1e-9 && Math.Abs(fast.Slots[3].MediaSeconds - fast.Slots[2].MediaSeconds - 2 / 30.0) < 1e-9,
            "a 2x clip steps its file times twice the sequence spacing");

        // A keyframed speed ramp (Pass 5.6.5): 1x rising linearly to 3x over the clip, so 5 s in, 7.5 s of the file have played.
        var keys = new List<Keyframe> { new(0, 1, EasingType.Linear), new(10, 3) };
        var ramped = Clip(LayerKind.VideoFile, 10, 20);
        ramped.State.KeysSpeed = keys;
        var layer = new Layer();
        layer.SetSpeedKeys(keys);
        var rampPlan = SlicePreviewPlanner.Plan(Document((TimelineTrackKind.Video, [ramped])), 15, spacing, 7, 72);
        var rampSteps = rampPlan is null ? new List<double>() : rampPlan.Slots.Zip(rampPlan.Slots.Skip(1), (a, b) => b.MediaSeconds - a.MediaSeconds).ToList();
        expect(rampPlan is { Slots.Count: 7 } && Math.Abs(rampPlan.Slots[3].MediaSeconds - 9.5) < 1e-9
               && rampPlan.Slots.All(s => Math.Abs(s.MediaSeconds - (2 + layer.MediaSecondsAt(s.SequenceSeconds - 10))) < 1e-9)
               && rampSteps.Count == 6 && rampSteps.Zip(rampSteps.Skip(1)).All(p => p.Second > p.First),
            "on a keyframed speed ramp the playhead's slot and its neighbours read the file through the ramp (the player's mapping, Layer.MediaSecondsAt), not the constant speed");
        ramped.SourceDuration = 9;
        var heldPlan = SlicePreviewPlanner.Plan(Document((TimelineTrackKind.Video, [ramped])), 15, spacing, 7, 72);
        expect(heldPlan is { Slots.Count: 7 } && heldPlan.Slots[3].MediaSeconds == 9 && heldPlan.Slots.All(s => s.MediaSeconds <= 9),
            "a slot past the file's end reads its last moment (file times held to the file's length)");

        var image = Document((TimelineTrackKind.Video, [Clip(LayerKind.Image, 10, 20, @"C:\logo.png")]));
        var audio = Document((TimelineTrackKind.Audio, [Clip(LayerKind.Audio, 10, 20, @"C:\music.wav")]));
        expect(SlicePreviewPlanner.Plan(image, 15, spacing, 7, 72) is null && SlicePreviewPlanner.Plan(audio, 15, spacing, 7, 72) is null
               && SlicePreviewPlanner.VideoClipAt(image, 15) is null && SlicePreviewPlanner.VideoClipAt(audio, 15) is null,
            "over an image clip or an audio-only clip there is no ribbon (user decision Q3: video clips only)");

        var stacked = Document((TimelineTrackKind.Video, [Clip(LayerKind.VideoFile, 0, 30, @"C:\under.mp4")]), (TimelineTrackKind.Video, [Clip(LayerKind.Image, 10, 20, @"C:\logo.png")]));
        expect(SlicePreviewPlanner.Plan(stacked, 15, spacing, 5, 72) is { Path: @"C:\under.mp4" }, "an image over a video: the video under it is shown");

        var hiddenClip = Clip(LayerKind.VideoFile, 10, 20);
        hiddenClip.IsHidden = true;
        var hiddenTrack = Document((TimelineTrackKind.Video, [Clip(LayerKind.VideoFile, 10, 20)]));
        hiddenTrack.Tracks[0].IsHidden = true;
        expect(SlicePreviewPlanner.Plan(Document((TimelineTrackKind.Video, [hiddenClip])), 15, spacing, 5, 72) is null && SlicePreviewPlanner.Plan(hiddenTrack, 15, spacing, 5, 72) is null
               && SlicePreviewPlanner.Plan(video, 25, spacing, 5, 72) is null && SlicePreviewPlanner.Plan(video, 15, default, 5, 72) is null,
            "hidden clips and tracks, an empty moment and an unusable spacing ask for nothing");
        expect(SlicePreviewPlanner.Plan(video, 15, spacing, 9, 72)?.Slots.Count == 7 && SlicePreviewPlanner.Plan(video, 15, spacing, 4, 72)?.Slots.Count == 5
               && SlicePreviewPlanner.Plan(video, 15, spacing, 5, 4)?.ShortSide == 16,
            "slot counts are held to 5-7 and the thumbnail size to at least 16 px");
    }

    // ----- scrub-and-slice-571-spacing-readout -----

    private static void SpacingReadout(Action<bool, string> expect, System.Text.StringBuilder text)
    {
        var zooms = new[] { 1200.0, 480, 320, 100, 32, 16, 0.01 };
        var readouts = zooms.Select(pps => SlicePreviewPlanner.SpacingFor(pps, 60).Readout.Replace("\u00B1", "+-", StringComparison.Ordinal)).ToList();
        foreach (var (pps, readout) in zooms.Zip(readouts))
            text.AppendLine(System.FormattableString.Invariant($"  zoom {pps} DIP per Sequence second, 60 fps: {readout}"));
        expect(readouts.SequenceEqual(["+-1f", "+-2f", "+-3f", "+-0.25s", "+-0.5s", "+-1s", "+-60s"]),
            "zoomed in deep the slots are whole frames apart (+-1f to +-3f); further out a round part of a second (+-0.25s, +-0.5s, +-1s ...)");
        var steps = new[] { 2000.0, 1200, 800, 480, 320, 200, 100, 64, 32, 16, 8, 4, 1 }.Select(pps => SlicePreviewPlanner.SpacingFor(pps, 60).Seconds).ToList();
        expect(steps.Zip(steps.Skip(1)).All(p => p.Second >= p.First - 1e-12), "zooming out never narrows the spacing");
        expect(SlicePreviewPlanner.SpacingFor(1200, 0) is { InFrames: false, Seconds: 0.25 } && !SlicePreviewPlanner.SpacingFor(0, 60).IsUsable
               && !SlicePreviewPlanner.SpacingFor(double.NaN, 60).IsUsable && SlicePreviewPlanner.SpacingFor(0, 60).Readout == "",
            "with no frame rate the spacing is in seconds; with no zoom it is unusable and shows no readout");

        expect(SlicePreviewPlanner.SlotCountFor(900, 71) == 7 && SlicePreviewPlanner.SlotCountFor(500, 71) == 5 && SlicePreviewPlanner.SlotCountFor(900, 0) == 5,
            "seven slots when the time area holds nine cells, else five");
        expect(SlicePreviewPlanner.LeftFor(500, 200, 168, 1000) == 400 && SlicePreviewPlanner.LeftFor(170, 200, 168, 1000) == 168
               && SlicePreviewPlanner.LeftFor(990, 200, 168, 1000) == 800 && SlicePreviewPlanner.LeftFor(500, 900, 168, 1000) == 50,
            "the ribbon centres on the playhead, kept inside the time area when it fits");
        expect(GpuPreviewEngine.SliceCellSize(72, 16 / 9.0) == (128, 72) && GpuPreviewEngine.SliceCellSize(73, 16 / 9.0) == (128, 72)
               && GpuPreviewEngine.SliceCellSize(72, 9 / 16.0) == (72, 128) && GpuPreviewEngine.SliceCellSize(8, double.NaN) == (28, 16)
               && GpuPreviewEngine.SliceCellSize(1000, 10) == (1024, 256),
            "strip cells are even-sided, the short side 16-256 px, the shape of the Sequence (portrait too), at most 4:1");
        expect(SlicePreviewRibbon.RibbonHeight == 60, "the ribbon's strip is 60 DIP tall: the XAML element's fixed height, so it never resizes");
    }

    // ----- scrub-and-slice-571-burst-seam -----

    private static void BurstSeam(Action<bool, string> expect)
    {
        const long f = Frequency;
        expect(CompositorWorker.SliceAskDue(true, false, 0, 5 * f, 0, f) && !CompositorWorker.SliceAskDue(true, true, 0, 5 * f, 0, f)
               && !CompositorWorker.SliceAskDue(true, false, 1, 5 * f, 0, f) && !CompositorWorker.SliceAskDue(false, false, 0, 5 * f, 0, f)
               && !CompositorWorker.SliceAskDue(true, false, 0, 5 * f + 119 * f / 1000, 5 * f, f) && CompositorWorker.SliceAskDue(true, false, 0, 5 * f + 120 * f / 1000, 5 * f, f),
            "the worker asks for missing slices only while nothing plays and every decode lane is idle (behind the visible frame), at most every 120 ms");

        const int readback = FrameSceneHeader.FlagReadback;
        expect(CompositorWorker.ReadsBackInPlace(readback, 1920, 1080, 896, 72) && !CompositorWorker.ReadsBackInPlace(readback | FrameSceneHeader.FlagReadbackNv12, 1920, 1080, 896, 72)
               && !CompositorWorker.ReadsBackInPlace(readback | FrameSceneHeader.FlagReadbackP010, 1920, 1080, 896, 72) && !CompositorWorker.ReadsBackInPlace(0, 1920, 1080, 896, 72)
               && !CompositorWorker.ReadsBackInPlace(readback, 0, 0, 896, 72) && !CompositorWorker.ReadsBackInPlace(readback, 640, 360, 896, 72)
               && !CompositorWorker.ReadsBackInPlace(readback, 1920, 1080, 1920, 1080) && !CompositorWorker.ReadsBackInPlace(readback, 768, 512, 640, 360)
               && CompositorWorker.ReadsBackInPlace(readback, 1920, 1080, 960, 540),
            "a small BGRA readback (a quarter of the ring at most) is drawn in place in the player's ring (no ring recreated twice per strip); NV12/P010 and larger ones keep the exact ring");

        expect(GpuPreviewEngine.SliceStripDue(true, false, true, false, false, 10 * f, 0, f) && !GpuPreviewEngine.SliceStripDue(false, false, true, false, false, 10 * f, 0, f)
               && !GpuPreviewEngine.SliceStripDue(true, true, true, false, false, 10 * f, 0, f) && !GpuPreviewEngine.SliceStripDue(true, false, false, false, false, 10 * f, 0, f)
               && !GpuPreviewEngine.SliceStripDue(true, false, true, true, false, 10 * f, 0, f) && !GpuPreviewEngine.SliceStripDue(true, false, true, false, true, 10 * f, 0, f)
               && !GpuPreviewEngine.SliceStripDue(true, false, true, false, false, 10 * f + 59 * f / 1000, 10 * f, f)
               && GpuPreviewEngine.SliceStripDue(true, false, true, false, false, 10 * f + 60 * f / 1000, 10 * f, f),
            "the pump draws a strip once per answered burst, never with a seek pending or in the 3D view, at most every 60 ms");

        const long s = 40;
        expect(GpuPreviewEngine.SliceReadbackOf(s, s, s, false, 10 * f, 10 * f, f) == GpuPreviewEngine.SliceReadback.Ready
               && GpuPreviewEngine.SliceReadbackOf(s, s, s + 1, true, 11 * f, 10 * f, f) == GpuPreviewEngine.SliceReadback.Ready
               && GpuPreviewEngine.SliceReadbackOf(s, s - 1, s - 1, false, 10 * f + 59 * f / 1000, 10 * f, f) == GpuPreviewEngine.SliceReadback.Waiting
               && GpuPreviewEngine.SliceReadbackOf(s, s - 1, s - 1, false, 10 * f + 60 * f / 1000, 10 * f, f) == GpuPreviewEngine.SliceReadback.Overdue
               && GpuPreviewEngine.SliceReadbackOf(s, s + 1, s + 1, false, 10 * f, 10 * f, f) == GpuPreviewEngine.SliceReadback.Replaced
               && GpuPreviewEngine.SliceReadbackOf(s, s - 1, s + 1, false, 10 * f, 10 * f, f) == GpuPreviewEngine.SliceReadback.Replaced
               && GpuPreviewEngine.SliceReadbackOf(s, s - 1, s - 1, true, 10 * f, 10 * f, f) == GpuPreviewEngine.SliceReadback.Replaced,
            "the pump never waits for a strip's readback: polled on later turns, taken once it is the newest readback, let go for a later readback, a later scene or a seek arriving mid-readback (the player's picture first), given up 60 ms after publishing");

        var engine = SourceProbe.Src("Services", "GpuPreviewEngine.SlicePreview.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(engine, "partial void SliceBurstTurn(bool playing)"), "if (playing || IsExplodedForSlices)", "EndSliceReadback(retry: false);",
                   "WithdrawSlices();", "return;", "PollSliceStrip();", "PostSlices(ask);", "_sliceReadback == 0", "_client.SliceBurstDone(_sliceVersion)", "PlayerOwesPicture()",
                   "DrawSliceStrip(posted);")
               && SourceProbe.Member(engine, "private void PostSlices(").Contains("_client.PostSliceBurst(ask.SourceId, ask.PathHash", StringComparison.Ordinal)
               && SourceProbe.Member(engine, "public int RequestSlicePreview(").Contains("_broker.SourceFor(request.Path, request.Stream)", StringComparison.Ordinal),
            "the pump withdraws any burst (and drops a strip in flight) while playing or in 3D, polls a strip in flight first, posts the newest ask (resolved on the UI thread) through the W0 seam, and draws only an answered one with none in flight and no seek or picture owed");
        var draw = SourceProbe.Member(engine, "private void DrawSliceStrip(");
        expect(draw.Contains("scene.Flags = FrameSceneHeader.FlagReadback;", StringComparison.Ordinal) && !draw.Contains("FlagExactFrames", StringComparison.Ordinal)
               && draw.Contains("_sliceReadback = _client.Publish();", StringComparison.Ordinal) && !draw.Contains("TryReadback(", StringComparison.Ordinal)
               && SourceProbe.InOrder(SourceProbe.Member(engine, "private void PollSliceStrip("), "Volatile.Read(ref control.ReadbackSequence)", "SliceReadbackOf(",
                   "case SliceReadback.Ready:", "TakeSliceStrip(sequence);", "case SliceReadback.Replaced:", "EndSliceReadback(retry: true);", "case SliceReadback.Overdue:",
                   "EndSliceReadback(retry: false);")
               && SourceProbe.Member(engine, "private void TakeSliceStrip(").Contains("out var gotHeight, 0)", StringComparison.Ordinal)
               && SourceProbe.Member(engine, "private void EndSliceReadback(").Contains("InvalidateView();", StringComparison.Ordinal),
            "the strip is one read-back scene from cached frames (no exact-frame wait) published without waiting; its readback is copied with no wait once polled ready, and the player's scene is published again once it is read back, replaced or overdue");

        var worker = SourceProbe.Src("Compositor", "CompositorWorker.cs");
        expect(SourceProbe.InOrder(worker, "decoders.TakeHints();", "shared.TakeLatest(", "var sliceQuiet = !isNew && !redraw;", "RenderScene(shared, slot,",
                   "SliceBurstTurn(ref control, decoders, sliceQuiet && decoders.LastFrameExact);")
               && worker.Contains("SliceBurstOutstanding ? SliceBurstPollMs", StringComparison.Ordinal)
               && SourceProbe.InOrder(SourceProbe.Member(worker, "private static void ReadBack("), "output.Texture.Width != width", "CopySubresourceRegion(_readbackStaging"),
            "the worker takes the burst each turn after the turn's scene is drawn (visible demands first), asks for slices only on a quiet turn with an exact picture, polls every 8 ms while one is outstanding, and copies an in-place readback's region");
        var turn = SourceProbe.Member(SourceProbe.Src("Compositor", "CompositorWorker.SlicePreview.cs"), "private static void SliceBurstTurn(");
        expect(SourceProbe.InOrder(turn, "SliceBurstSlot.TryRead(", "decoders.SyncSources();", "decoders.HasExact(", "Volatile.Write(ref control.SliceBurstDone, _slice.Version);",
                   "!admit || !SliceAskDue(", "decoders.Request("),
            "a newer whole burst replaces the old one (answered never), the exact version is answered once every slice is in the frame cache, and missing slices are asked for only when admitted");

        var frame = SourceProbe.Member(ScrubFile, "private void SlicePreviewFrame(");
        expect(SourceProbe.InOrder(frame, "state.Velocity <= SlicePreviewRequest.DormantAbove", "SlicePreviewPlanner.VideoClipAt(", "state.SliceGate.Update(", "HideSlicePreview();",
                   "UnifiedTimeline.PlaceSlicePreview(seconds);", "engine.RequestSlicePreview(request, aspect)", "engine.TakeSlicePreview()"),
            "the ribbon looks no clip up during a whip (dormant, zero overhead), and once shown is placed by translation, asks for frames and takes strips from its frame hook");
    }
}
