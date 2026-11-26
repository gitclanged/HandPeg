using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.7.1 lane L4: Speed Ramp auto-duration. A ramp keeps the clip's source span, so shaping it resizes the clip and its speed family
/// (stream lanes, Cutouts) to the exact integrated length, the keys re-timed with it, in one Undo step: a fast ramp shrinks (what follows
/// moves up with Ripple Timeline on), a slow ramp lengthens (what follows moves on), and with Ripple Timeline off a growing clip stops at the
/// next clip with a warning (user decision Q2). The scene plan holds a ramped clip's last file frame over its tail frame instead of black.
/// </summary>
public static class SpeedRampDuration571Tests
{
    private const string Second = "D:/media/second.mkv", Third = "D:/media/third.mkv", Fourth = "D:/media/fourth.mkv";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("speed-ramp-duration-571-fit", "# SpeedRamp.Fit (5.7.1): the clip keeps its file span; keys re-timed (speeds kept), the exact length, the average speed, the room's clamp.", Fit);
        yield return Case("speed-ramp-duration-571-ripple", "# Ramp auto-duration with Ripple Timeline on (5.7.1): fast shrinks, slow lengthens, the family follows, what follows moves; one Undo step.", Ripple);
        yield return Case("speed-ramp-duration-571-clamp", "# Ripple Timeline off (5.7.1, Q2): a growing ramp stops at the next clip and warns; a shrinking one leaves a gap; nothing else moves.", Clamp);
        yield return Case("speed-ramp-duration-571-eof", "# The scene plan at a ramped clip's tail (5.7.1): the last file frame held over its last frame, never a black past-EOF frame.", Eof);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static int UndoSteps(MainViewModel vm) =>
        ((System.Collections.ICollection)typeof(MainViewModel).GetField("_undo", Reflect.Hidden)!.GetValue(vm)!).Count;

    private static Keyframe Key(double time, double speed) => new(time, speed, EasingType.Linear);

    private static string Keys(IReadOnlyList<Keyframe> keys) => string.Join(" ", keys.Select(k => F($"{k.Time:0.###}:{k.Value:0.###}x")));

    private static TimelineClip SoundClip(string path, double start, double end, Guid parent) => new()
    {
        ContentKind = LayerKind.Audio, SourcePath = path, AudioStreamIndex = 0, Start = start, End = end, SourceDuration = 60, ParentClipId = parent,
        State = new LayerState { Kind = LayerKind.Audio, Name = Path.GetFileNameWithoutExtension(path), ImagePath = path, MediaDuration = 60, HasAudio = true },
    };

    private static string Spans(MainViewModel vm, params Guid[] ids) =>
        string.Join(", ", ids.Select(id => vm.Document.FindClip(id) is { } c ? F($"{c.Start:0.###}-{c.End:0.###}") : "gone"));

    // ----- speed-ramp-duration-571-fit -----

    private static void Fit(Action<bool, string> expect, StringBuilder text)
    {
        // Slow: 1x easing linearly to 0.5x over 4 s plays 3 s of the file; to keep 4 s it re-times by 4/3 and runs 5.333 s at 0.75x average.
        var slow = SpeedRamp.Fit([Key(0, 1), Key(4, 0.5)], 1, 4, 4);
        text.Append("   slow 1x->0.5x over 4 s, span 4 s: ").Append(Keys(slow.Keys)).Append(F($", {slow.Duration:0.###} s at {slow.Speed:0.###}x")).Append('\n');
        expect(Math.Abs(slow.Duration - 16 / 3.0) < 1e-6 && Math.Abs(slow.Speed - 0.75) < 1e-6 && !slow.Clamped
               && Math.Abs(SpeedRamp.MediaSeconds(slow.Keys, slow.Speed, slow.Duration) - 4) < 1e-6,
            "a slow ramp lengthens the clip to the exact length that plays the same 4 s of its file (16/3 s), its average speed 0.75x");
        expect(slow.Keys.Count == 2 && slow.Keys[0].Value == 1 && slow.Keys[1].Value == 0.5 && Math.Abs(slow.Keys[1].Time - 16 / 3.0) < 1e-6,
            "its keys re-time with it (times x 4/3) and keep their speeds: the curve's shape over the clip is the one drawn");

        // Fast: 1x to 3x plays 8 s; to keep 4 s it halves to 2 s.
        var fast = SpeedRamp.Fit([Key(0, 1), Key(4, 3)], 1, 4, 4);
        text.Append("   fast 1x->3x over 4 s, span 4 s: ").Append(Keys(fast.Keys)).Append(F($", {fast.Duration:0.###} s at {fast.Speed:0.###}x")).Append('\n');
        expect(Math.Abs(fast.Duration - 2) < 1e-6 && Math.Abs(fast.Speed - 2) < 1e-6 && Math.Abs(fast.Stretch - 0.5) < 1e-9,
            "a fast ramp shrinks it to 2 s (never past the file's end), 2x average");

        // Eased segments (Simpson) too: the fitted length plays the span to the bisection's precision.
        var eased = SpeedRamp.Fit([new Keyframe(0, 0.25), new Keyframe(1.5, 4), new Keyframe(3, 1, EasingType.EaseOut)], 1, 3, 2.5);
        expect(Math.Abs(SpeedRamp.MediaSeconds(eased.Keys, eased.Speed, eased.Duration) - 2.5) < 1e-6,
            F($"an eased ramp's fitted length ({eased.Duration:0.###} s) plays exactly its 2.5 s span"));

        // No keys (Clear): span / speed; at the ramp's average speed that is the clip's length again.
        var cleared = SpeedRamp.Fit([], 0.75, 16 / 3.0, 4);
        expect(cleared.Keys.Count == 0 && Math.Abs(cleared.Duration - 16 / 3.0) < 1e-6 && cleared.Speed == 0.75,
            "cleared, a clip at its ramp's average speed keeps its length and span");

        // The room: a length past it is clamped (the tail trimmed), Length still the whole span's.
        var room = SpeedRamp.Fit([Key(0, 1), Key(4, 0.5)], 1, 4, 4, room: 5);
        expect(room.Clamped && room.Duration == 5 && Math.Abs(room.Length - 16 / 3.0) < 1e-6 && Math.Abs(room.Speed - 0.75) < 1e-6,
            "with 5 s of room the slow ramp is clamped at 5 s (Clamped), its unclamped length still reported");
        expect(!SpeedRamp.Fit([Key(0, 1), Key(4, 3)], 1, 4, 4, room: 4).Clamped, "a shrinking ramp is never clamped");
        var bad = SpeedRamp.Fit([Key(0, 1)], 1, 4, double.NaN);
        expect(bad.Duration == 4 && !bad.Clamped && bad.Stretch == 1, "a span that is not a number changes nothing");
    }

    // ----- speed-ramp-duration-571-ripple -----

    // Track 1 the file opened; track 2 the ramped clip (0-4) and the next (4-8); track 3 a clip at 5-7; a stream lane and a Cutout of the
    // ramped clip (same file, parented) on tracks of their own.
    private static MainViewModel Fixture(double nextStart, out Guid ramped, out Guid next, out Guid away, out Guid lane, out Guid cutout)
    {
        var first = DogfoodTests.FileClip(Second, 0, 4);
        var after = DogfoodTests.FileClip(Third, nextStart, nextStart + 4);
        var other = DogfoodTests.FileClip(Fourth, 5, 7);
        var vm = DogfoodTests.Open(DogfoodTests.Lane(first, after), DogfoodTests.Lane(other));
        var document = vm.Document.Clone();
        var parent = document.Tracks[1];
        var stream = new TimelineTrackModel { Kind = TimelineTrackKind.Audio, ParentTrackId = parent.Id, TrackNumber = 1 };
        var sound = SoundClip(Second, 0, 4, first.Id);
        stream.Clips.Add(sound);
        document.Tracks.Insert(2, stream);
        var piece = DogfoodTests.FileClip(Second, 0, 4);
        piece.ParentClipId = first.Id;
        var cutouts = DogfoodTests.Lane(piece);
        cutouts.TrackNumber = document.Tracks.Count(t => t.Kind == TimelineTrackKind.Video) + 1;
        document.Tracks.Add(cutouts);
        vm.InstallDocument(document);
        SpeedTimeTests.Fresh(vm);
        foreach (var problem in vm.Document.FindProblems())
            Console.WriteLine("        the fixture: " + problem);
        (ramped, next, away, lane, cutout) = (first.Id, after.Id, other.Id, sound.Id, piece.Id);
        return vm;
    }

    private static void Ripple(Action<bool, string> expect, StringBuilder text)
    {
        var settings = AppSettings.Current;
        var kept = (settings.RippleAllTracks, settings.SpeedRipplesTimeline);
        try
        {
            settings.RippleAllTracks = true;

            // Fast (1x -> 3x): 2 s; everything after its old end moves up 2 s on every track; the family runs with it.
            var vm = Fixture(4, out var ramped, out var next, out var away, out var lane, out var cutout);
            var steps = UndoSteps(vm);
            var view = vm.ViewOf(vm.Document.FindClip(ramped)!)!;
            var outcome = vm.SetSpeedRamp(view, [Key(0, 1), Key(4, 3)], ripple: true);
            var shrunk = Spans(vm, ramped, next, away, lane, cutout);
            text.Append("   fast, ripple on: ").Append(shrunk).Append('\n');
            expect(outcome is { Fitted: true, Clamped: false } && shrunk == "0-2, 2-6, 3-5, 0-2, 0-2",
                "a fast ramp shrinks the clip to 2 s, what follows moves up as far on every track, its stream lane and Cutout shrink with it");
            var model = vm.Document.FindClip(ramped)!;
            var family = new[] { lane, cutout }.Select(id => vm.ViewOf(vm.Document.FindClip(id)!)!).ToList();
            expect(Keys(vm.ViewOf(model)!.SpeedKeys) == "0:1x 2:3x" && family.All(f => Keys(f.SpeedKeys) == "0:1x 2:3x") && model.Speed == 2,
                F($"its keys re-time to the new length (0:1x 2:3x) on the clip and its family; its base speed is the average ({model.Speed:0.###}x)"));
            expect(Math.Abs(SpeedRamp.MediaSeconds(vm.ViewOf(model)!.SpeedKeys, model.Speed, model.Duration) - 4) < 1e-6,
                "it still plays the same 4 s of its file");
            expect(UndoSteps(vm) == steps + 1, "one undo step for the ramp, the fit, the family and the ripple");
            SpeedTimeTests.Fresh(vm);
            vm.Undo();
            var back = vm.Document.FindClip(ramped)!;
            expect(Spans(vm, ramped, next, away, lane, cutout) == "0-4, 4-8, 5-7, 0-4, 0-4" && back.Speed == 1 && !vm.ViewOf(back)!.HasSpeedRamp,
                "Undo puts every clip, the speed and the constant speed (no ramp) back at once");
            SpeedTimeTests.Fresh(vm);
            vm.Redo();
            var again = vm.Document.FindClip(ramped)!;
            expect(Spans(vm, ramped, next, away, lane, cutout) == shrunk && Keys(vm.ViewOf(again)!.SpeedKeys) == "0:1x 2:3x",
                "Redo makes the fitted ramp again");

            // Slow (1x -> 0.5x): 16/3 s; what follows moves on 4/3 s.
            vm = Fixture(4, out ramped, out next, out away, out lane, out cutout);
            outcome = vm.SetSpeedRamp(vm.ViewOf(vm.Document.FindClip(ramped)!)!, [Key(0, 1), Key(4, 0.5)], ripple: true);
            var grown = Spans(vm, ramped, next, away, lane, cutout);
            text.Append("   slow, ripple on: ").Append(grown).Append('\n');
            expect(outcome is { Fitted: true, Clamped: false } && grown == "0-5.333, 5.333-9.333, 6.333-8.333, 0-5.333, 0-5.333",
                "a slow ramp lengthens the clip to 5.333 s, what follows moves on 1.333 s, nothing moved twice, the family grows with it");
            model = vm.Document.FindClip(ramped)!;
            expect(Keys(vm.ViewOf(model)!.SpeedKeys) == "0:1x 5.333:0.5x" && model.Speed == 0.75,
                F($"its keys stretch with it ({Keys(vm.ViewOf(model)!.SpeedKeys)}) at the 0.75x average ({model.Speed:0.###}x)"));

            // Cleared: back to the constant (average) speed, same length, same span.
            outcome = vm.SetSpeedRamp(vm.ViewOf(model)!, [], ripple: true);
            expect(outcome.Fitted && Spans(vm, ramped, next) == "0-5.333, 5.333-9.333" && !vm.ViewOf(vm.Document.FindClip(ramped)!)!.HasSpeedRamp,
                "Clear keeps the clip's length (it runs at the ramp's average speed)");

            // The two-argument fit follows the setting.
            settings.SpeedRipplesTimeline = true;
            vm = Fixture(4, out ramped, out next, out _, out _, out _);
            var ramp = vm.ViewOf(vm.Document.FindClip(ramped)!)!;
            var span = vm.RampSourceSpan(ramp);
            vm.Checkpoint("ramp");
            ramp.SetSpeedKeys([Key(0, 1), Key(4, 3)]);
            vm.FitClipToRamp(ramp, ramp.SpeedKeys, span);
            expect(span == 4 && Spans(vm, ramped, next) == "0-2, 2-6", "the card's fit (Ripple Timeline on) closes up after a shrink");
        }
        finally
        {
            (settings.RippleAllTracks, settings.SpeedRipplesTimeline) = kept;
        }
    }

    // ----- speed-ramp-duration-571-clamp -----

    private static void Clamp(Action<bool, string> expect, StringBuilder text)
    {
        var settings = AppSettings.Current;
        var kept = (settings.RippleAllTracks, settings.SpeedRipplesTimeline);
        try
        {
            settings.RippleAllTracks = true;

            // Slow, the next clip at 5 s: 16/3 s would run onto it, so the clip stops at 5 s; nothing else moves; the status bar says so.
            var vm = Fixture(5, out var ramped, out var next, out var away, out var lane, out var cutout);
            var steps = UndoSteps(vm);
            var view = vm.ViewOf(vm.Document.FindClip(ramped)!)!;
            settings.SpeedRipplesTimeline = false;
            var preview = vm.PreviewRampFit(view, [Key(0, 1), Key(4, 0.5)], 4);
            expect(preview.Clamped && preview.Duration == 5, "the card's live fit while shaping already shows the clamp (Ripple Timeline off)");
            var outcome = vm.SetSpeedRamp(view, [Key(0, 1), Key(4, 0.5)], ripple: false);
            var clamped = Spans(vm, ramped, next, away, lane, cutout);
            text.Append("   slow, ripple off, next clip at 5 s: ").Append(clamped).Append(F($", {outcome.LostSeconds:0.###} s of the file lost")).Append('\n');
            expect(outcome is { Fitted: true, Clamped: true } && clamped == "0-5, 5-9, 5-7, 0-5, 0-5",
                "a slow ramp with Ripple Timeline off stops at the next clip (5 s), which stays; its family stops with it; other tracks stay");
            expect(vm.StatusText.Contains("stops at the next clip", StringComparison.Ordinal) && Math.Abs(outcome.LostSeconds - 0.172) < 0.002,
                F($"and warns in the status bar: \"{vm.StatusText}\""));
            var model = vm.Document.FindClip(ramped)!;
            expect(Keys(vm.ViewOf(model)!.SpeedKeys) == "0:1x 5.333:0.5x", "its keys keep the whole ramp's timing (the tail past 5 s trimmed, as a trim would)");
            expect(UndoSteps(vm) == steps + 1, "one undo step");
            SpeedTimeTests.Fresh(vm);
            vm.Undo();
            expect(Spans(vm, ramped, next, away, lane, cutout) == "0-4, 5-9, 5-7, 0-4, 0-4", "Undo puts it back");

            // Abutting: no room at all, the length stays.
            vm = Fixture(4, out ramped, out next, out _, out lane, out _);
            outcome = vm.SetSpeedRamp(vm.ViewOf(vm.Document.FindClip(ramped)!)!, [Key(0, 1), Key(4, 0.5)], ripple: false);
            expect(outcome.Clamped && Spans(vm, ramped, next, lane) == "0-4, 4-8, 0-4", "abutting the next clip it keeps its length and warns");

            // Fast, not rippling: a gap after it, nothing moves.
            vm = Fixture(4, out ramped, out next, out away, out lane, out cutout);
            outcome = vm.SetSpeedRamp(vm.ViewOf(vm.Document.FindClip(ramped)!)!, [Key(0, 1), Key(4, 3)], ripple: false);
            expect(outcome is { Fitted: true, Clamped: false } && Spans(vm, ramped, next, away, lane, cutout) == "0-2, 4-8, 5-7, 0-2, 0-2",
                "a fast ramp with Ripple Timeline off leaves a gap; its family shrinks with it");
        }
        finally
        {
            (settings.RippleAllTracks, settings.SpeedRipplesTimeline) = kept;
        }
    }

    // ----- speed-ramp-duration-571-eof -----

    private static GpuLayerDescriptor? Drawn(MainViewModel vm, Guid clip, double t)
    {
        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        var plan = GpuScenePlan.Create(vm.LivePreviewState(), broker);
        var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var header = default(FrameSceneHeader);
        var count = plan.Evaluate(t, ref header, layers);
        var key = GpuHash.Of(clip);
        for (var k = 0; k < count; k++)
        {
            if (layers[k].ClipKey == key && (layers[k].Flags & GpuLayerDescriptor.FlagBlurFillOnly) == 0)
                return layers[k];
        }

        return null;
    }

    private static void Eof(Action<bool, string> expect, StringBuilder text)
    {
        // A file 5.98 s long read from 2 s: a 1x ramp reaches its end at 3.98 s, inside the clip's last frame (4.0-4.033 at 30 fps).
        MainViewModel Tail(double end, bool ramped, out Guid id)
        {
            var clip = DogfoodTests.FileClip(Second, 0, end, offset: 2);
            (clip.SourceDuration, clip.State.MediaDuration) = (5.98, 5.98);
            if (ramped)
                clip.State.KeysSpeed = [Key(0, 1), Key(1, 1)];
            id = clip.Id;
            return DogfoodTests.Open(DogfoodTests.Lane(clip));
        }

        var vm = Tail(121 / 30.0, ramped: true, out var id);
        var before = Drawn(vm, id, 3.9 + 2 / 30.0);
        var tail = Drawn(vm, id, 4.0);
        text.Append(F($"   1x ramp, file end at 3.98 s: frame 4.0 s reads {(tail is { } l ? l.MediaSeconds.ToString("0.###", CultureInfo.InvariantCulture) : "nothing (black)")}")).Append('\n');
        expect(before is { MediaSeconds: < 5.98 } && tail is { } held && held.MediaSeconds < 5.98 && held.MediaSeconds >= 5.98 - GpuScenePlan.RampEofHold - 1e-9,
            "a ramped clip's last frame, a hair past the file's end, holds the file's last frame instead of going black");
        var constant = Tail(121 / 30.0, ramped: false, out var plain);
        expect(Drawn(constant, plain, 4.0) is null, "a constant-speed clip is unchanged (its end is bounded by the file already)");
        var past = Tail(5, ramped: true, out var over);
        expect(Drawn(past, over, 4.5) is null && Drawn(past, over, 3.9) is not null,
            "a ramp that genuinely runs past the file (a project from before auto-duration) still ends where the file does, after one frame");

        // Fitted: a whole 4 s file ramped 1x -> 3x becomes 2 s; every frame of it draws, the last one inside the file.
        var whole = DogfoodTests.FileClip(Second, 0, 4);
        (whole.SourceDuration, whole.State.MediaDuration) = (4, 4);
        var model = DogfoodTests.Open(DogfoodTests.Lane(whole));
        SpeedTimeTests.Fresh(model);
        model.SetSpeedRamp(model.ViewOf(model.Document.FindClip(whole.Id)!)!, [Key(0, 1), Key(4, 3)], ripple: true);
        var fitted = model.Document.FindClip(whole.Id)!;
        var missing = 0;
        var last = 0.0;
        for (var frame = 0; frame / 30.0 < fitted.End - 1e-9; frame++)
        {
            if (Drawn(model, whole.Id, frame / 30.0) is { } layer && layer.MediaSeconds < 4)
                last = layer.MediaSeconds;
            else
                missing++;
        }

        text.Append(F($"   fitted 1x->3x over a 4 s file: {fitted.Start:0.###}-{fitted.End:0.###}, last frame reads {last:0.###} s, {missing} black")).Append('\n');
        expect(Math.Abs(fitted.End - 2) < 1e-6 && missing == 0 && last > 3.8, "a fitted ramp to the file's end draws every frame, the last near the file's end, none black");
    }
}
