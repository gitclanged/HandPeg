using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.Services.Gpu;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.6A lane C: the Speed &amp; Time card, Smooth Slow-Mo, the phase vocoder, keep pitch, the Motion Blur boost. Here: a speed that
/// ripples (both ways, every track or its own, one undo step, its family with it), the speed a Target Duration gives, the 16x ceiling, the
/// plan's interpolation flag, the options saved only when not the default and kept through the lowering; and on the WPF Application, the card
/// itself (which clip shows what, a Cutout's speed its source clip's, when the Opacity card shows). The sound's side is AudioEngineTests'
/// (audio-engine-spectral, audio-engine-pitch-timing), the picture's CompositorParityTests' and GpuFxTests' (gpufx-shutter-lanes).
/// </summary>
public static class SpeedTimeTests
{
    private const string Second = "D:/media/second.mkv", Third = "D:/media/third.mkv", Fourth = "D:/media/fourth.mkv", Song = "D:/media/song.wav";

    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("speed-ripple", "# A speed that ripples (5.6A): what follows moves by as much as the clip grew or shrank, on every track or its own, in one undo step with its family.", Ripple);
        yield return Case("speed-target-duration", "# Target Duration (5.6A): the speed is the clip's file stretch over the length typed; the card's slider, box and pills.", TargetDuration);
        yield return Case("speed-16x-validation", "# Up to 16x (5.6A): the Layer's ceiling, the document's check, the edit's clamp, the Inspector's boxes and the clip menu.", Ceiling);
        yield return Case("interp-plan-flag", "# Smooth Slow-Mo (5.6A): the plan asks for in-betweens only for a clip slower than 1x that has it on.", PlanFlag);
        yield return Case("interp-next-frame", "# Smooth Slow-Mo's next frame (5.6A review): a companion of the layer's frame, with no layer of its own in the decoders.", NextFrame);
        yield return Case("layerstate-speed-options", "# The Speed & Time card's options (5.6A): written only when not the default, read back, taken as a look, kept over a track's look.", SavedOptions);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("speed-card-visibility", "# The Speed & Time card (5.6A): a video's, a sound's, a Cutout's (its source clip's speed), none for a picture; when the Opacity card shows.", CardVisibility);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static void Note(FormattableString text) => Console.WriteLine("        " + text.ToString(CultureInfo.InvariantCulture));

    private static int UndoSteps(MainViewModel vm) => ((System.Collections.ICollection)typeof(MainViewModel).GetField("_undo", Hidden)!.GetValue(vm)!).Count;

    // As if the last step were long ago (a test's edits come within the half second and second and a half that merge a user's into one step).
    private static void Fresh(MainViewModel vm)
    {
        vm.ForgetUndoRuns();

        // (A document put in place gives the bindings until the dispatcher's next idle moment to settle: no edit is taken before. Nothing pumps here.)
        typeof(MainViewModel).GetField("_settleEndsAt", Hidden)!.SetValue(vm, 0L);
    }

    private static void Install(MainViewModel vm, TimelineDocument document) => vm.InstallDocument(document);

    private static TimelineClip SoundClip(string path, double start, double end) => new()
    {
        ContentKind = LayerKind.Audio, SourcePath = path, AudioStreamIndex = 0, Start = start, End = end, SourceDuration = 60,
        State = new LayerState { Kind = LayerKind.Audio, Name = Path.GetFileNameWithoutExtension(path), ImagePath = path, MediaDuration = 60, HasAudio = true },
    };

    // ----- speed-ripple -----

    private static void Ripple(Action<bool, string> expect, StringBuilder text)
    {
        var settings = AppSettings.Current;
        var kept = (settings.RippleAllTracks, settings.SpeedRipplesTimeline);
        try
        {
            // Track 1 the file opened (0-8 s); track 2 two clips (0-4, 4-8); track 3 one at 5-7 s; a stream lane of track 2's first clip under it.
            MainViewModel Fixture(out TimelineClip slowed, out Guid next, out Guid later, out Guid lane)
            {
                var first = DogfoodTests.FileClip(Second, 0, 4);
                var after = DogfoodTests.FileClip(Third, 4, 8);
                var away = DogfoodTests.FileClip(Fourth, 5, 7);
                var vm = DogfoodTests.Open(DogfoodTests.Lane(first, after), DogfoodTests.Lane(away));
                var document = vm.Document.Clone();
                var parent = document.Tracks[1];
                var stream = new TimelineTrackModel { Kind = TimelineTrackKind.Audio, ParentTrackId = parent.Id, TrackNumber = 1 };
                var sound = SoundClip(Second, 0, 4);
                sound.ParentClipId = first.Id;
                stream.Clips.Add(sound);
                document.Tracks.Insert(2, stream);
                Install(vm, document);
                Fresh(vm);
                foreach (var problem in vm.Document.FindProblems())
                    Note($"the fixture: {problem}");
                (slowed, next, later, lane) = (vm.Document.FindClip(first.Id)!, after.Id, away.Id, sound.Id);
                return vm;
            }

            string Spans(MainViewModel vm, params Guid[] ids) =>
                string.Join(", ", ids.Select(id => vm.Document.FindClip(id) is { } c ? F($"{c.Start:0.###}-{c.End:0.###}") : "gone"));

            // Faster (2x), every track: the clip ends at 2 s; what started at 4 s and after moves 2 s earlier, on its track and on track 3.
            settings.RippleAllTracks = true;
            var vm = Fixture(out var clip, out var next, out var later, out var lane);
            var steps = UndoSteps(vm);
            vm.SetClipSpeed(vm.ViewOf(clip)!, 2, ripple: true);
            var shrunk = Spans(vm, clip.Id, next, later, lane);
            text.Append("   2x, every track: ").Append(shrunk).Append('\n');
            expect(shrunk == "0-2, 2-6, 3-5, 0-2", "faster: it ends sooner and everything after its old end moves up as far, on every track; its stream lane runs at its speed");
            expect(UndoSteps(vm) == steps + 1, "one undo step for the speed, its family and the ripple");
            Fresh(vm);
            vm.Undo();
            expect(Spans(vm, clip.Id, next, later, lane) == "0-4, 4-8, 5-7, 0-4" && vm.Document.FindClip(clip.Id)!.Speed == 1, "Undo puts every clip back at once");
            Holds(vm, expect);

            // Slower (0.5x), every track: the clip ends at 8 s; what followed moves on by 4 s.
            vm = Fixture(out clip, out next, out later, out lane);
            vm.SetClipSpeed(vm.ViewOf(clip)!, 0.5, ripple: true);
            var grown = Spans(vm, clip.Id, next, later, lane);
            text.Append("   0.5x, every track: ").Append(grown).Append('\n');
            expect(grown == "0-8, 8-12, 9-11, 0-8", "slower: it ends later and everything after its old end moves on as far, on every track, nothing moved twice");
            Holds(vm, expect);

            // Its own track (Ripple All Tracks off): track 3 stays.
            settings.RippleAllTracks = false;
            vm = Fixture(out clip, out next, out later, out lane);
            vm.SetClipSpeed(vm.ViewOf(clip)!, 2, ripple: true);
            expect(Spans(vm, clip.Id, next, later, lane) == "0-2, 2-6, 5-7, 0-2", "with Ripple All Tracks off only its own track (and its Sub-Lanes) ripples");
            vm = Fixture(out clip, out next, out later, out lane);
            vm.SetClipSpeed(vm.ViewOf(clip)!, 0.5, ripple: true);
            expect(Spans(vm, clip.Id, next, later, lane) == "0-8, 8-12, 5-7, 0-8", "and grown, the same");

            // Not rippling (the card's Ripple Timeline off): a gap where it shrinks, its track's next clip moved out of its way where it grows.
            settings.RippleAllTracks = true;
            vm = Fixture(out clip, out next, out later, out lane);
            vm.SetClipSpeed(vm.ViewOf(clip)!, 2, ripple: false);
            expect(Spans(vm, clip.Id, next, later, lane) == "0-2, 4-8, 5-7, 0-2", "not rippling, a faster clip leaves a gap (as before 5.6A)");
            vm = Fixture(out clip, out next, out later, out lane);
            vm.SetClipSpeed(vm.ViewOf(clip)!, 0.5, ripple: false);
            expect(Spans(vm, clip.Id, next, later, lane) == "0-8, 8-12, 5-7, 0-8", "and a slower one pushes only what it runs into on its track");

            // The setting is what the two-argument call (the Inspector's box, the clip menu, the card) takes.
            settings.SpeedRipplesTimeline = true;
            vm = Fixture(out clip, out next, out later, out lane);
            vm.SetClipSpeed(vm.ViewOf(clip)!, 2);
            expect(Spans(vm, clip.Id, next, later) == "0-2, 2-6, 3-5", "every speed's way in ripples as Ripple Timeline says (on)");
            settings.SpeedRipplesTimeline = false;
            vm = Fixture(out clip, out next, out later, out lane);
            vm.SetClipSpeed(vm.ViewOf(clip)!, 2);
            expect(Spans(vm, clip.Id, next, later) == "0-2, 4-8, 5-7", "and (off) leaves the gap");

            // 5.6A review: a crossfade into the clip after it (on its own track, and on its stream lane, its family's) is kept where the
            // ripple brings the two together again, held to what fits; Undo puts it back as it was.
            settings.RippleAllTracks = true;
            MainViewModel Crossfaded(out Guid slowed, out Guid after, out Guid stream, out Guid follows)
            {
                var model = Fixture(out var first, out after, out _, out stream);
                var crossfaded = model.Document.Clone();
                crossfaded.Tracks[1].Clips[1].TransitionIn = 3;
                var follower = SoundClip(Third, 4, 8);
                (follower.ParentClipId, follower.TransitionIn) = (after, 1);
                crossfaded.Tracks[2].Clips.Add(follower);
                Install(model, crossfaded);
                Fresh(model);
                (slowed, follows) = (first.Id, follower.Id);
                return model;
            }

            string Fades(MainViewModel model, params Guid[] ids) =>
                string.Join(", ", ids.Select(id => model.Document.FindClip(id) is { } c ? F($"{c.Start:0.###}-{c.End:0.###} x{c.TransitionIn:0.###}") : "gone"));
            vm = Crossfaded(out var faded, out next, out lane, out var follows);
            vm.SetClipSpeed(vm.ViewOf(vm.Document.FindClip(faded)!)!, 2, ripple: true);
            var fades = Fades(vm, next, follows);
            text.Append("   2x, crossfades after it: ").Append(fades).Append('\n');
            expect(fades == "2-6 x2, 2-6 x1", "faster: the crossfade into the next clip stays (3 s held to its 2 s), and its stream lane's (1 s) too");
            Holds(vm, expect);
            Fresh(vm);
            vm.Undo();
            expect(Fades(vm, next, follows) == "4-8 x3, 4-8 x1", "Undo puts the clips and their crossfades back");

            // 5.6A review 2: slower (0.5x), its family grows after the ripple moved what follows, so its stream lane's clip meets the one
            // after it again and keeps its crossfade too (it was lost on the grow path); one undo step, and Undo puts it all back.
            vm = Crossfaded(out faded, out next, out lane, out follows);
            steps = UndoSteps(vm);
            vm.SetClipSpeed(vm.ViewOf(vm.Document.FindClip(faded)!)!, 0.5, ripple: true);
            fades = Fades(vm, next, follows);
            text.Append("   0.5x, crossfades after it: ").Append(fades).Append(", its stream lane ").Append(Spans(vm, lane)).Append('\n');
            expect(fades == "8-12 x3, 8-12 x1" && Spans(vm, lane) == "0-8",
                "slower: the crossfade into the next clip stays, and its stream lane's clip grows up to the one after it and keeps its crossfade (1 s)");
            expect(UndoSteps(vm) == steps + 1, "one undo step for the speed, its family, the ripple and the crossfades");
            Holds(vm, expect);
            Fresh(vm);
            vm.Undo();
            expect(Fades(vm, next, follows) == "4-8 x3, 4-8 x1" && Spans(vm, lane) == "0-4", "Undo puts the clips and their crossfades back");

            // 5.6A review: what rides it (a title of its own on a Sub-Lane, as a Game Preset's pieces are) is cut back or grown with it, as a
            // ripple trim's riders are, and the next clip's title on that Sub-Lane moves with the ripple: none of them holds it back.
            MainViewModel Titled(out Guid slowed, out Guid after, out Guid own, out Guid theirs, out Guid away)
            {
                var first = DogfoodTests.FileClip(Second, 0, 4);
                var second = DogfoodTests.FileClip(Third, 4, 8);
                var far = DogfoodTests.FileClip(Fourth, 5, 7);
                var model = DogfoodTests.Open(DogfoodTests.Lane(first, second), DogfoodTests.Lane(far));
                var document = model.Document.Clone();
                var titles = new TimelineTrackModel { Kind = TimelineTrackKind.Video, ParentTrackId = document.Tracks[1].Id };
                TimelineClip Title(string words, double start, double end, Guid parent) => new()
                {
                    ContentKind = LayerKind.Text, Start = start, End = end, ParentClipId = parent,
                    State = new LayerState { Kind = LayerKind.Text, Name = words, TextContent = words, SizeWidth = 0.3 },
                };
                var (one, two) = (Title("One", 0, 4, first.Id), Title("Two", 4, 8, second.Id));
                titles.Clips.AddRange([one, two]);
                document.Tracks.Insert(2, titles);
                var number = 0;
                foreach (var track in document.Tracks.Where(t => t.Kind == TimelineTrackKind.Video))
                    track.TrackNumber = ++number;
                Install(model, document);
                Fresh(model);
                foreach (var problem in model.Document.FindProblems())
                    Note($"the titled fixture: {problem}");
                (slowed, after, own, theirs, away) = (first.Id, second.Id, one.Id, two.Id, far.Id);
                return model;
            }

            vm = Titled(out var titled, out var titledNext, out var ownTitle, out var nextTitle, out var titledAway);
            steps = UndoSteps(vm);
            vm.SetClipSpeed(vm.ViewOf(vm.Document.FindClip(titled)!)!, 2, ripple: true);
            var rode = Spans(vm, titled, titledNext, ownTitle, nextTitle, titledAway);
            text.Append("   2x, a title on it and on the next: ").Append(rode).Append('\n');
            expect(rode == "0-2, 2-6, 0-2, 2-6, 3-5", "faster: its own title is cut back to its new end, and the next clip's title moves up with the rest (it holds nothing back)");
            expect(UndoSteps(vm) == steps + 1, "one undo step for the speed, its title and the ripple");
            Holds(vm, expect);
            Fresh(vm);
            vm.Undo();
            expect(Spans(vm, titled, titledNext, ownTitle, nextTitle, titledAway) == "0-4, 4-8, 0-4, 4-8, 5-7", "Undo puts them all back");
            vm = Titled(out titled, out titledNext, out ownTitle, out nextTitle, out titledAway);
            vm.SetClipSpeed(vm.ViewOf(vm.Document.FindClip(titled)!)!, 0.5, ripple: true);
            rode = Spans(vm, titled, titledNext, ownTitle, nextTitle, titledAway);
            text.Append("   0.5x, a title on it and on the next: ").Append(rode).Append('\n');
            expect(rode == "0-8, 8-12, 0-8, 8-12, 9-11", "slower: its own title grows with its end, and the rest moves on as far");
            Holds(vm, expect);

            // 5.6A review 2: a ripple held back (a music track's clips meet across its old end) brings nothing back into the place of its
            // own outro title that the speed leaves too short: the title is not taken away but stays where it was, unlinked. Left there, it
            // holds back the next clip's title on its Sub-Lane (which would slide into it), so a ripple held back part of the way moves
            // nothing either; with nothing after it there, the rest moves up as far as the music lets it.
            MainViewModel Held(double songEnds, double songStarts, bool nextTitled, out Guid slowed, out Guid after, out Guid outro, out Guid theirs, out Guid song, out Guid nextSong)
            {
                var first = DogfoodTests.FileClip(Second, 0, 4);
                var second = DogfoodTests.FileClip(Third, 4, 8);
                var model = DogfoodTests.Open(DogfoodTests.Lane(first, second));
                var document = model.Document.Clone();
                var titles = new TimelineTrackModel { Kind = TimelineTrackKind.Video, ParentTrackId = document.Tracks[1].Id, TrackNumber = 3 };
                TimelineClip Title(string words, double start, double end, Guid parent) => new()
                {
                    ContentKind = LayerKind.Text, Start = start, End = end, ParentClipId = parent,
                    State = new LayerState { Kind = LayerKind.Text, Name = words, TextContent = words, SizeWidth = 0.3 },
                };
                var (ending, coming) = (Title("Outro", 3, 4, first.Id), Title("Two", 4, 8, second.Id));
                titles.Clips.Add(ending);
                if (nextTitled)
                    titles.Clips.Add(coming);
                document.Tracks.Insert(2, titles);
                var music = new TimelineTrackModel { Kind = TimelineTrackKind.Audio, TrackNumber = 1 };
                var (one, two) = (SoundClip(Song, 0, songEnds), SoundClip(Song, songStarts, 10));
                music.Clips.AddRange([one, two]);
                document.Tracks.Add(music);
                Install(model, document);
                Fresh(model);
                foreach (var problem in model.Document.FindProblems())
                    Note($"the held fixture: {problem}");
                (slowed, after, outro, theirs, song, nextSong) = (first.Id, second.Id, ending.Id, coming.Id, one.Id, two.Id);
                return model;
            }

            bool LeftUnlinked(MainViewModel model, Guid id) => model.Document.FindClip(id) is { ParentClipId: null, Tethered: false };
            vm = Held(5, 5, true, out var heldClip, out var heldNext, out var outro, out var nextOutro, out var song, out var nextSong);
            steps = UndoSteps(vm);
            vm.SetClipSpeed(vm.ViewOf(vm.Document.FindClip(heldClip)!)!, 2, ripple: true);
            var held = Spans(vm, heldClip, heldNext, outro, nextOutro, song, nextSong);
            text.Append("   2x, held back by meeting clips: ").Append(held).Append('\n');
            expect(held == "0-2, 4-8, 3-4, 4-8, 0-5, 5-10" && LeftUnlinked(vm, outro),
                "held back: nothing moves, and its outro title left too short stays where it was, unlinked (nothing comes back into its place)");
            expect(UndoSteps(vm) == steps + 1, "one undo step for the speed and its title");
            Holds(vm, expect);
            Fresh(vm);
            vm.Undo();
            expect(Spans(vm, heldClip, heldNext, outro, nextOutro, song, nextSong) == "0-4, 4-8, 3-4, 4-8, 0-5, 5-10" && vm.Document.FindClip(outro)?.ParentClipId == heldClip,
                "Undo puts the clip back and its title is its own again");

            vm = Held(3, 4, true, out heldClip, out heldNext, out outro, out nextOutro, out song, out nextSong);
            steps = UndoSteps(vm);
            vm.SetClipSpeed(vm.ViewOf(vm.Document.FindClip(heldClip)!)!, 2, ripple: true);
            held = Spans(vm, heldClip, heldNext, outro, nextOutro, song, nextSong);
            text.Append("   2x, held back part of the way, a title after its own: ").Append(held).Append('\n');
            expect(held == "0-2, 4-8, 3-4, 4-8, 0-3, 4-10" && LeftUnlinked(vm, outro),
                "held back part of the way: its outro title stays where it was, unlinked, and the next clip's title does not slide into it (nor the rest)");
            expect(UndoSteps(vm) == steps + 1, "one undo step");
            Holds(vm, expect);

            vm = Held(3, 4, false, out heldClip, out heldNext, out outro, out _, out song, out nextSong);
            vm.SetClipSpeed(vm.ViewOf(vm.Document.FindClip(heldClip)!)!, 2, ripple: true);
            held = Spans(vm, heldClip, heldNext, outro, song, nextSong);
            text.Append("   2x, held back part of the way, nothing after its title: ").Append(held).Append('\n');
            expect(held == "0-2, 3-7, 3-4, 0-3, 3-9" && LeftUnlinked(vm, outro),
                "with nothing after its title, the rest moves up as far as the music lets it (1 s), and the title stays where it was, unlinked");
            Holds(vm, expect);
        }
        finally
        {
            (settings.RippleAllTracks, settings.SpeedRipplesTimeline) = kept;
        }
    }

    private static void Holds(MainViewModel vm, Action<bool, string> expect)
    {
        var problems = vm.Document.FindProblems();
        foreach (var problem in problems)
            Note($"{problem}");
        expect(problems.Count == 0, "the document holds together");
    }

    // ----- speed-target-duration -----

    private static void TargetDuration(Action<bool, string> expect, StringBuilder text)
    {
        expect(EffectsPane.SpeedForDuration(8, 4) == 2 && EffectsPane.SpeedForDuration(8, 16) == 0.5 && EffectsPane.SpeedForDuration(10, 3) == 3.333,
            "the speed is the file stretch over the length (8 s in 4 s: 2x; in 16 s: 0.5x; 10 s in 3 s: 3.333x)");
        expect(EffectsPane.SpeedForDuration(8, 100) == 0.1 && EffectsPane.SpeedForDuration(8, 0.1) == 16, "held to 0.1x and 16x");
        expect(double.IsNaN(EffectsPane.SpeedForDuration(0, 5)) && double.IsNaN(EffectsPane.SpeedForDuration(8, 0)) && double.IsNaN(EffectsPane.SpeedForDuration(8, double.NaN)),
            "no speed for no length");

        var frames = TimeDisplay.UseFrames;
        try
        {
            TimeDisplay.UseFrames = false;
            expect(EffectsPane.TryReadDuration("00:00:05.500", out var typed) && typed == 5.5, "a length typed as a time (HH:MM:SS.mmm)");
            expect(EffectsPane.TryReadDuration("12", out var seconds) && seconds == 12 && EffectsPane.TryReadDuration("2.5s", out var withUnit) && withUnit == 2.5,
                "or as seconds");
            expect(!EffectsPane.TryReadDuration("soon", out _) && !EffectsPane.TryReadDuration("-3", out _) && !EffectsPane.TryReadDuration("00:00:00.000", out _), "nothing else, and no nought");
        }
        finally
        {
            TimeDisplay.UseFrames = frames;
        }

        // The slider: log2 of the speed, resting on a usual speed near it; the box: a number, an x allowed, held to the range.
        expect(EffectsPane.SpeedOfSlider(0) == 1 && EffectsPane.SpeedOfSlider(Math.Log2(0.5) + 0.03) == 0.5 && EffectsPane.SpeedOfSlider(Math.Log2(16)) == 16
               && EffectsPane.SpeedOfSlider(Math.Log2(0.1)) == 0.1 && EffectsPane.SpeedOfSlider(Math.Log2(3)) == 3,
            "the slider's place is log2 of the speed (1x in the middle of 0.1x and 16x on a log scale), resting on the pills near them");
        var middle = (0 - Math.Log2(0.1)) / (Math.Log2(16) - Math.Log2(0.1));
        Note($"1x sits at {middle:P0} of the slider");
        expect(middle is > 0.4 and < 0.5, "1x sits near the middle of the slider");
        expect(EffectsPane.TryReadSpeed("2x", out var two) && two == 2 && EffectsPane.TryReadSpeed(" 0.25 ", out var quarter) && quarter == 0.25
               && EffectsPane.TryReadSpeed("40", out var top) && top == 16 && !EffectsPane.TryReadSpeed("0", out _) && !EffectsPane.TryReadSpeed("fast", out _),
            "the box reads 2x and 0.25, holds 40 to 16x, refuses 0 and words");
        expect(EffectsPane.SpeedPills.SequenceEqual([0.25, 0.5, 1, 2, 4]) && string.Join(" ", EffectsPane.SpeedPills.Select(EffectsPane.SpeedLabel)) == "0.25x 0.5x 1x 2x 4x",
            "the pills: 0.25x 0.5x 1x 2x 4x");

        // 5.6A review: let go, the slider shows what was written (the detent taken, or the old speed when the edit was refused) once the thumb
        // (or the track's button) has let go of the pointer: ShowSpeed in SpeedHeld itself runs while it still holds it.
        var card = File.ReadAllText(Path.Combine(SourceProbe.Root, "src", "Views", "EffectsPane.Speed.cs")).ReplaceLineEndings("\n");
        expect(SourceProbe.InOrder(SourceProbe.Member(card, "private void SpeedHeld(bool held)"), "_speedHeld = false;", "CommitSpeed(owed);", "Dispatcher.BeginInvoke(", "if (!_speedHeld)", "ShowSpeed();",
                "DispatcherPriority.Input"),
            "a drag let go commits, then puts the slider on the speed written once the pointer is let go (the dispatcher's next input turn)");

        // Through the edit: a 4 s clip given 10 s runs at 0.4x and lasts 10 s.
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 4)));
        Fresh(vm);
        var clip = vm.Document.Tracks[1].Clips[0];
        var speed = EffectsPane.SpeedForDuration(clip.MediaEnd - clip.MediaOffset, 10);
        vm.SetClipSpeed(vm.ViewOf(clip)!, speed, ripple: false);
        clip = vm.Document.Tracks[1].Clips[0];
        expect(speed == 0.4 && Math.Abs(clip.Duration - 10) < 1e-6 && clip.Speed == 0.4, F($"a 4 s clip given 10 s runs at 0.4x and lasts {clip.Duration:0.###} s"));
    }

    // ----- speed-16x-validation -----

    private static void Ceiling(Action<bool, string> expect, StringBuilder text)
    {
        expect(Layer.FastestSpeed == 16 && Layer.SlowestSpeed == 0.1, "a clip runs from 0.1x to 16x");

        // 3.5 s of a 60 s file at 16x is 56 s of it: a clip the document holds; past 16x it is not one.
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 56)));
        Fresh(vm);
        var view = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        vm.SetClipSpeed(view, 16, ripple: false);
        var clip = vm.Document.Tracks[1].Clips[0];
        expect(clip.Speed == 16 && Math.Abs(clip.Duration - 3.5) < 1e-6 && vm.Document.FindProblems().Count == 0, F($"16x: 56 s of the file in {clip.Duration:0.###} s, the document holds together"));
        Fresh(vm);
        vm.SetClipSpeed(view, 40, ripple: false);
        expect(vm.Document.Tracks[1].Clips[0].Speed == 16, "asked for 40x, the edit holds it to 16x");
        var faster = vm.Document.Clone();
        faster.Tracks[1].Clips[0].Speed = 16.5;
        expect(faster.FindProblems().Any(p => p.Contains("outside 0.1 to 16", StringComparison.Ordinal)), "the document's check names a speed past 16x");
        expect(Layer.FromState(new LayerState { Kind = LayerKind.VideoFile, Speed = 20 }, 1920, 1080, 1920, 1080).Speed == 16, "a saved 20x reads as 16x");

        // The Inspector's boxes and the clip menu say 16 (one home each: they set the speed the card sets).
        var root = SourceProbe.Root;
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Views", "UnifiedTimelinePane.xaml"));
        var boxes = xaml.Split('\n').Where(l => l.Contains("Binding ClipSpeed", StringComparison.Ordinal)).Select((l, i) => i).Count();
        expect(boxes == 2 && CountOf(xaml, "local:NumericInput.Minimum=\"0.1\" local:NumericInput.Maximum=\"16\"") == 2 && !xaml.Contains("from 0.1 to 10", StringComparison.Ordinal),
            "the Inspector's two Speed boxes go to 16 and say so");
        var menu = File.ReadAllText(Path.Combine(root, "src", "MainWindow.TrackMenus.cs"));
        expect(menu.Contains("{ 0.1, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 4, 10, 16 }", StringComparison.Ordinal), "the clip menu's Clip Speed offers 16x");
    }

    private static int CountOf(string text, string what)
    {
        var (count, at) = (0, 0);
        while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0)
            (count, at) = (count + 1, at + what.Length);
        return count;
    }

    // ----- interp-plan-flag -----

    private static void PlanFlag(Action<bool, string> expect, StringBuilder text)
    {
        GpuLayerDescriptor? Drawn(double speed, bool smooth)
        {
            var clip = DogfoodTests.FileClip(Second, 0, 4);
            clip.Speed = speed;
            clip.State.SmoothSlowMo = smooth ? true : null;
            var vm = DogfoodTests.Open(DogfoodTests.Lane(clip));
            using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
            var plan = GpuScenePlan.Create(vm.LivePreviewState(), broker);
            var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
            var header = default(FrameSceneHeader);
            var count = plan.Evaluate(1, ref header, layers);
            var key = GpuHash.Of(clip.Id);
            for (var k = 0; k < count; k++)
            {
                if (layers[k].ClipKey == key)
                    return layers[k];
            }

            return null;
        }

        bool Flagged(GpuLayerDescriptor? layer) => layer is { } l && (l.Flags & GpuLayerDescriptor.FlagInterpolate) != 0;
        expect(GpuLayerDescriptor.FlagInterpolate == 512, "the flag is bit 512 of the layer's");
        expect(Flagged(Drawn(0.5, true)) && Flagged(Drawn(0.1, true)), "Smooth Slow-Mo at 0.5x (and 0.1x): the layer asks for in-betweens");
        expect(Drawn(1, true) is { } a && !Flagged(a) && Drawn(2, true) is { } b && !Flagged(b), "at 1x or faster it does not, even with it on");
        expect(Drawn(0.5, false) is { } c && !Flagged(c), "nor slower than 1x with it off: the frame is held, as before");
    }

    // ----- interp-next-frame -----

    private static void NextFrame(Action<bool, string> expect, StringBuilder text)
    {
        // A real pool with one file registered (nothing is decoded: its frames are put in the cache by hand).
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.InterpNext");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "next.bin");
        File.WriteAllBytes(file, new byte[4096]);
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        using var gpu = GpuDevice.Create(-1);
        using var shared = CompositorSharedMemory.Create(F($"HandPeg.GoldenTests.InterpNext.{Guid.NewGuid():N}"));
        using var pool = new DecoderPool(gpu, shared);
        pool.Cache.BigCardForTests(false);
        ref var entry = ref shared.Source(0);
        (entry.Kind, entry.StreamIndex, entry.IFrameCount, entry.PathHash) = ((int)GpuSourceKind.Media, 0, 0, CompositorSharedMemory.PathHash(file));
        CompositorSharedMemory.WriteString(entry.Path, file);
        Volatile.Write(ref entry.Generation, 1);
        Interlocked.Increment(ref shared.Control.SourcesVersion);
        pool.SyncSources();
        expect(pool.StampsBackForTests(10_000), "the file is registered");

        var serial = pool.SerialOf(0);
        CachedFrame Cached(double time)
        {
            var frame = pool.Cache.Rent(64, 64, tenBit: false, software: true);
            (frame.Source, frame.Time, frame.Until, frame.Closed) = (serial, time, time + 1 / 30.0, true);
            pool.Cache.Insert(frame);
            return frame;
        }

        var (shown, after) = (Cached(10), Cached(10 + 1 / 30.0));
        const long key = 0x1234, salted = key ^ 0x5A3C_9E17_6B2D_41F3;
        var gate = typeof(DecoderPool).GetField("_gate", Hidden)!.GetValue(pool)!;
        var layers = (System.Collections.IDictionary)typeof(DecoderPool).GetField("_layers", Hidden)!.GetValue(pool)!;
        bool Kept(long clipKey)
        {
            lock (gate)
                return layers.Contains(clipKey);
        }

        // Paused (the in-between is drawn paused too): the layer's frame, then its next one as Interpolate asks for it.
        pool.BeginFrame(1, playing: false, 10.01);
        var frame = pool.Resolve(0, 10.01, 0, key);
        var next = pool.ResolveNext(0, shown.Until + 1e-4, 0, salted);
        expect(ReferenceEquals(frame, shown) && ReferenceEquals(next, after) && pool.LastFrameExact, "both decoded: the layer's frame and the next one are found as Resolve finds them, the picture exact");
        expect(Kept(key) && !Kept(salted),
            "the layer keeps its memory; the next frame's salted key none (no second play rate, PlayAhead or pre-roll stretch, no second decoder for the file)");
        _ = pool.ResolveNext(0, 12.5, 0, salted);
        expect(!pool.LastFrameExact && !Kept(salted), "a next frame not decoded yet: the picture is not exact (an export waits for it), and still no memory of its own");
        pool.EndFrame();

        var renderer = File.ReadAllText(Path.Combine(SourceProbe.Root, "src", "Compositor", "SceneRenderer.Interpolate.cs"));
        var lookup = SourceProbe.Member(File.ReadAllText(Path.Combine(SourceProbe.Root, "src", "Compositor", "DecoderPool.cs")).ReplaceLineEndings("\n"),
            "public CachedFrame? ResolveNext(int id, double seconds, long pathHash, long clipKey)");
        expect(renderer.Contains("_decoders.ResolveNext(layer.SourceId, frame.Until + 1e-4,", StringComparison.Ordinal) && !renderer.Contains("_decoders.Resolve(", StringComparison.Ordinal),
            "Interpolate asks for the next frame through ResolveNext");
        expect(lookup.Contains("Ask(source, seconds, DemandKind.Visible, clipKey,", StringComparison.Ordinal) && !lookup.Contains("PlayAhead", StringComparison.Ordinal)
               && !lookup.Contains("_layers", StringComparison.Ordinal) && lookup.Contains("_pinned.Add(exact);", StringComparison.Ordinal),
            "ResolveNext asks only for the frame itself (Visible), never a PlayAhead, keeps no layer and pins what it gives until EndFrame");
    }

    // ----- layerstate-speed-options -----

    private static void SavedOptions(Action<bool, string> expect, StringBuilder text)
    {
        var plain = new Layer { Kind = LayerKind.VideoFile, Name = "clip" };
        var json = JsonSerializer.Serialize(plain.ToState());
        expect(plain is { SmoothSlowMo: false, KeepPitch: true, SpectralAudio: false }, "a clip starts with Smooth Slow-Mo off, Keep Pitch on, Spectral Audio off");
        expect(!json.Contains("SmoothSlowMo", StringComparison.Ordinal) && !json.Contains("KeepPitch", StringComparison.Ordinal) && !json.Contains("SpectralAudio", StringComparison.Ordinal),
            "at the defaults nothing of them is written (older projects read the same)");

        var set = new Layer { Kind = LayerKind.VideoFile, Name = "clip", SmoothSlowMo = true, KeepPitch = false, SpectralAudio = true };
        var written = JsonSerializer.Serialize(set.ToState());
        expect(written.Contains("\"SmoothSlowMo\":true", StringComparison.Ordinal) && written.Contains("\"KeepPitch\":false", StringComparison.Ordinal)
               && written.Contains("\"SpectralAudio\":true", StringComparison.Ordinal), "set, each is written");
        var read = Layer.FromState(JsonSerializer.Deserialize<LayerState>(written)!, 1920, 1080, 1920, 1080);
        expect(read is { SmoothSlowMo: true, KeepPitch: false, SpectralAudio: true }, "and read back");
        var older = Layer.FromState(JsonSerializer.Deserialize<LayerState>("{\"Kind\":7,\"Name\":\"old\"}")!, 1920, 1080, 1920, 1080);
        expect(older is { SmoothSlowMo: false, KeepPitch: true, SpectralAudio: false }, "a layer saved before 5.6A reads with the defaults");

        // Taken as a look (Undo puts a clip's State back this way), and to the defaults from none.
        var view = new Layer { Kind = LayerKind.VideoFile };
        view.ApplyLook(set.ToState());
        var first = view is { SmoothSlowMo: true, KeepPitch: false, SpectralAudio: true };
        view.ApplyLook(null);
        expect(first && view is { SmoothSlowMo: false, KeepPitch: true, SpectralAudio: false }, "a look puts them on a clip, and no look puts back the defaults");

        // Lowered: the sound engine hears them (Keep Pitch off: the pitch moves with the speed; Spectral: the vocoder), and a track's look does not take
        // them away (TimelineLowering.Merged keeps them the clip's own, as its speed is).
        var document = new TimelineDocument { Width = 1920, Height = 1080, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Audio, TrackNumber = 1 };
        var sound = SoundClip(Song, 0, 4);
        sound.Speed = 0.5;
        (sound.State.KeepPitch, sound.State.SpectralAudio) = (false, true);
        track.Clips.Add(sound);
        document.Tracks.Add(track);
        var state = new SequenceExportState
        {
            Timeline = document, SourceFrameRate = 30, RequestedSize = (1920, 1080), OutputSize = (1920, 1080),
            CaptionLayer = Layer.FromState(new LayerState { Kind = LayerKind.Captions, Name = "Subtitles", SizeWidth = 0.9, SizeHeight = 0.2 }, 1920, 1080, 1920, 1080),
        };
        var voice = AudioProgram.Build(state).Voices.Single();
        expect(voice is { Spectral: true, Rate: 0.5, Pitch: 0.5 }, F($"the engine hears Spectral and the tape pitch of 0.5x (rate {voice.Rate:0.###}, pitch {voice.Pitch:0.###})"));
        track.TrackLook = new LayerState { Kind = LayerKind.Audio, Name = "track look" };
        var lowered = TimelineLowering.Lowering(state with { Timeline = document.Clone() }).LayerAudio.Single();
        expect(lowered is { KeepPitch: false, SpectralAudio: true }, "over a track's look the clip keeps its own options");

        // 5.6A review: a Preset's look (a template, a track's template: MainViewModel.TakeLook) neither resets nor copies them; they are the
        // clip's own, as its speed is.
        var presets = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 4)));
        Fresh(presets);
        var taking = presets.ViewOf(presets.Document.Tracks[1].Clips[0])!;
        (taking.SmoothSlowMo, taking.KeepPitch, taking.SpectralAudio) = (true, false, false);
        void Take(LayerState look) =>
            typeof(MainViewModel).GetMethod("TakeLook", Hidden)!.Invoke(presets, [taking, presets.Document.Tracks[1].Clips[0], presets.Document.Tracks[1], look]);
        Take(new LayerState { Kind = LayerKind.VideoFile, Name = "older preset" });
        var olderKept = taking is { SmoothSlowMo: true, KeepPitch: false, SpectralAudio: false };
        Take(new LayerState { Kind = LayerKind.VideoFile, Name = "newer preset", SmoothSlowMo = null, KeepPitch = null, SpectralAudio = true });
        typeof(MainViewModel).GetMethod("FlushLooks", Hidden)!.Invoke(presets, []);
        var saved = presets.Document.Tracks[1].Clips[0].State;
        expect(olderKept && taking is { SmoothSlowMo: true, KeepPitch: false, SpectralAudio: false } && saved is { SmoothSlowMo: true, KeepPitch: false, SpectralAudio: null },
            "a Preset's look, saved before 5.6A or with options of its own, leaves the clip's Smooth Slow-Mo, Keep Pitch and Spectral Audio as they were");
    }

    // ----- speed-card-visibility (Live) -----

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var below in Tree(child))
                yield return below;
        }
    }

    private static List<string> Names(Panel panel) =>
        [.. Tree(panel).Select(AutomationProperties.GetName).Where(n => !string.IsNullOrEmpty(n))];

    private static void CardVisibility(Action<bool, string> expect, StringBuilder text)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var addedChrome = !app.Resources.Contains("FlyoutChrome");
        if (addedChrome)
            app.Resources["FlyoutChrome"] = new Style(typeof(ButtonChrome));
        var slot = PaneTitleSlot.Of("Effects");
        var frames = TimeDisplay.UseFrames;
        TimeDisplay.UseFrames = false;
        try
        {
            // The file opened (with its sound) and a Cutout of it on a Sub-Lane; a sound clip; a text clip; two more videos (one at 50 %).
            var vm = DogfoodTests.Open();
            var document = vm.Document.Clone();
            var main = document.Tracks[0].Clips[0];
            main.AudioStreamIndex = 0;
            main.State.HasAudio = true;
            var cutoutLane = new TimelineTrackModel { Kind = TimelineTrackKind.Video, ParentTrackId = document.Tracks[0].Id, TrackNumber = 2 };
            var cutoutLook = TimelineTransaction.CopyOf(main.State);
            (cutoutLook.Name, cutoutLook.CropLeft, cutoutLook.CropRight) = ("HUD", 0.6, 0.1);
            var cutout = new TimelineClip { ContentKind = LayerKind.VideoFile, SourcePath = main.SourcePath, Start = 0, End = 8, SourceDuration = 8, ParentClipId = main.Id, State = cutoutLook };
            cutoutLane.Clips.Add(cutout);
            var others = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 3 };
            var faded = DogfoodTests.FileClip(Second, 0, 4);
            faded.State.Opacity = 50;
            var plain = DogfoodTests.FileClip(Third, 4, 8);
            others.Clips.AddRange([faded, plain]);
            var words = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 4 };
            words.Clips.Add(new TimelineClip { ContentKind = LayerKind.Text, Start = 0, End = 4, State = new LayerState { Kind = LayerKind.Text, Name = "Title", TextContent = "Hi", SizeWidth = 0.3 } });
            var sounds = new TimelineTrackModel { Kind = TimelineTrackKind.Audio, TrackNumber = 1 };
            var song = SoundClip(Song, 0, 6);
            sounds.Clips.Add(song);
            document.Tracks.InsertRange(1, [cutoutLane, others, words]);
            document.Tracks.Add(sounds);
            Install(vm, document);

            // (The view model takes the times' format from the settings; the card's lengths are read here as HH:MM:SS.mmm.)
            TimeDisplay.UseFrames = false;
            var pane = new EffectsPane();
            pane.Attach(vm);
            Layer View(TimelineClip clip) => vm.ViewOf(vm.Document.FindClip(clip.Id)!)!;
            void Select(Layer view)
            {
                vm.KeyLayer = view;
                Pump();
            }

            T Call<T>(string method, params object[] arguments) => (T)typeof(EffectsPane).GetMethod(method, Hidden)!.Invoke(pane, arguments)!;
            void Do(string method, params object[] arguments) => typeof(EffectsPane).GetMethod(method, Hidden)!.Invoke(pane, arguments);
            var opacity = HandPegApp.Models.Effects.EffectRegistry.Find(HandPegApp.Models.Effects.EffectRegistry.Opacity)!;

            // A video that carries sound: Speed & Time, its sound's rows, no Opacity card at 100 % with no keys; Add Effect offers it, and it then shows.
            var video = View(main);
            Select(video);
            var names = Names(pane.SpeedPanel);
            text.Append("   video: ").Append(string.Join(", ", names)).Append('\n');
            string[] speedRows = ["Clip Speed Slider", "Clip Speed", "Speed 0.25x", "Speed 0.5x", "Speed 1x", "Speed 2x", "Speed 4x", "Target Duration", "Ripple Timeline"];
            expect(names.FirstOrDefault() == "Speed & Time" && speedRows.All(names.Contains) && names.Contains("Smooth Slow-Mo") && names.Contains("Interpolation"),
                "a video clip: the Speed & Time card (speed slider and box, five pills, Target Duration, Ripple Timeline, Smooth Slow-Mo, Interpolation)");
            expect(new[] { "Keep Pitch", "Pitch Shift", "Spectral Audio" }.All(names.Contains), "with its sound's rows: it carries sound");
            TextBox Box(string name) => Tree(pane.SpeedPanel).OfType<TextBox>().First(b => AutomationProperties.GetName(b) == name);
            expect(Box("Target Duration").Text == "00:00:08.000", F($"its Target Duration shows its length ({Box("Target Duration").Text})"));
            expect(pane.PinnedPanel.Children.Count == 0 && Call<bool>("Fits", opacity, false), "at 100 % with no keys there is no Opacity card, and Add Effect offers Opacity");
            Do("AddPinned", opacity);
            expect(pane.PinnedPanel.Children.Count == 1 && !Call<bool>("Fits", opacity, false), "added, its card shows (and Add Effect no longer offers it)");
            var vendor = Tree(pane.SpeedPanel).OfType<ComboBoxItem>().Single(i => (string)i.Content == "Vendor Hardware");
            Note($"Vendor Hardware here: {vendor.ToolTip}");
            expect(!vendor.IsEnabled && vendor.ToolTip is "Not available on this GPU" or "Not available in this build", "Vendor Hardware is shown, not offered, with why (this GPU, or this build)");

            // Microcopy (CLAUDE.md 10.5): labels of four words at most, tooltips of twelve.
            int Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var labels = Tree(pane.SpeedPanel).OfType<ContentControl>().Select(c => c.Content).OfType<string>()
                .Concat(Tree(pane.SpeedPanel).OfType<TextBlock>().Select(t => t.Text)).Where(s => s.Length > 0).ToList();
            var tips = Tree(pane.SpeedPanel).OfType<FrameworkElement>().Select(e => e.ToolTip).OfType<string>().ToList();
            expect(labels.All(l => Words(l) <= 4) && tips.All(t => Words(t) <= 12), F($"its {labels.Count} labels are four words or fewer, its {tips.Count} tooltips twelve or fewer"));

            // Smooth Slow-Mo on: the clip and its Cutout (one moving picture), one undo step.
            var steps = UndoSteps(vm);
            var smooth = Tree(pane.SpeedPanel).OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "Smooth Slow-Mo");
            smooth.IsChecked = true;
            smooth.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            expect(View(main).SmoothSlowMo && View(cutout).SmoothSlowMo && UndoSteps(vm) == steps + 1, "Smooth Slow-Mo goes on the clip and its Cutout in one undo step");

            // The speed: written as the clip's own edit (one step, no checkpoint before it), its Cutout with it.
            Fresh(vm);
            steps = UndoSteps(vm);
            Do("CommitSpeed", 2.0);
            Pump();
            expect(vm.Document.FindClip(main.Id)!.Speed == 2 && vm.Document.FindClip(cutout.Id)!.Speed == 2 && UndoSteps(vm) == steps + 1,
                "a speed from the card: the clip and its Cutout at 2x, one undo step");
            expect(Box("Clip Speed").Text == "2.00" && Box("Target Duration").Text == "00:00:04.000", F($"the card shows it: {Box("Clip Speed").Text}x, {Box("Target Duration").Text}"));
            Fresh(vm);
            vm.Undo();
            Pump();
            expect(vm.Document.FindClip(main.Id)!.Speed == 1 && Box("Clip Speed").Text == "1.00", "Undo: back to 1x, and the card says so");

            // A Cutout: its card sets its source clip's speed.
            Select(View(cutout));
            names = Names(pane.SpeedPanel);
            var note = Tree(pane.SpeedPanel).OfType<TextBlock>().Any(t => t.Text == "Follows source clip");
            expect(names.FirstOrDefault() == "Speed & Time" && note, "a Cutout: Speed & Time, following its source clip");
            Do("CommitSpeed", 0.5);
            Pump();
            expect(vm.Document.FindClip(main.Id)!.Speed == 0.5 && vm.Document.FindClip(cutout.Id)!.Speed == 0.5, "its speed is set on its source clip, and it runs at it");

            // Another video at 50 %: its Opacity card shows; one at 100 % shows it once its opacity is set elsewhere (a rebuild then, and only then).
            Select(View(faded));
            expect(pane.PinnedPanel.Children.Count == 1, "a clip at 50 %: its Opacity card shows");
            var other = View(plain);
            Select(other);
            expect(pane.PinnedPanel.Children.Count == 0, "one at 100 % with no keys: none");
            other.Opacity = 60;
            Pump();
            var card = pane.SpeedPanel.Children.Count > 0 ? pane.SpeedPanel.Children[0] : null;
            expect(pane.PinnedPanel.Children.Count == 1 && card is not null, "set to 60 % elsewhere, its card comes");
            other.Opacity = 70;
            Pump();
            expect(pane.SpeedPanel.Children.Count > 0 && ReferenceEquals(pane.SpeedPanel.Children[0], card), "moved again, nothing is rebuilt");
            other.Opacity = 100;
            Pump();
            expect(pane.PinnedPanel.Children.Count == 1, "back at 100 %, it stays for the session (never taken away under the pointer)");

            // A sound clip selected on the timeline (5.6A review: MainWindow.SelectClip tells the pane, SelectSound, here called as it calls it;
            // its source and the tab attach's are checked below): the pane's target, Speed & Pitch, its sound's rows, no picture rows; the key
            // layer (the Keyframes pane's, the text tools', the crop's) stays the picture it was.
            var sound = View(song);
            var keyBefore = vm.KeyLayer;
            pane.SelectSound(sound);
            Pump();
            Layer? Target() => (Layer?)typeof(EffectsPane).GetField("_layer", Hidden)!.GetValue(pane);
            expect(ReferenceEquals(Target(), sound) && ReferenceEquals(vm.KeyLayer, keyBefore) && ReferenceEquals(vm.ActiveKeyLayer, keyBefore),
                "a sound clip selected is the pane's target, and the key layer stays the picture clip it was");

            // 5.6A review 2: the window tells the pane. A clip selected on the timeline (SelectClip) is passed on as it is (a sound clip, or
            // null for anything else); a tab kept in memory brought back (AttachHydrated) passes its selection on once, only after a
            // successful attach has put its own views back in Layers (before it, SelectSound finds none and can only clear the target).
            var tracksSource = File.ReadAllText(Path.Combine(SourceProbe.Root, "src", "MainWindow.Tracks.cs")).ReplaceLineEndings("\n");
            expect(SourceProbe.Member(tracksSource, "private void SelectClip(object? clip, bool keepGroup = false)").Contains("EffectsView.SelectSound(clip as Layer);", StringComparison.Ordinal),
                "selecting a clip on the timeline tells the Effects pane (SelectClip calls SelectSound with it)");
            var attach = SourceProbe.Member(File.ReadAllText(Path.Combine(SourceProbe.Root, "src", "MainWindow.TabCache.cs")).ReplaceLineEndings("\n"),
                "private bool AttachHydrated(ProjectTabHeader tab, TabViewContext live)");
            expect(CountOf(attach, "EffectsView.SelectSound(") == 1
                   && SourceProbe.InOrder(attach, "if (!_viewModel.AttachTab(project))", "return false;", "_viewModel.NewProject();", "return true;", "RestoreTabView(tab);",
                              "EffectsView.SelectSound(_selectedClip as Layer);", "UpdateTimelineInspector();"),
                "a tab brought back tells the Effects pane its sound clip once, after a successful AttachTab (nothing on the failed attach's paths)");
            names = Names(pane.SpeedPanel);
            text.Append("   sound: ").Append(string.Join(", ", names)).Append('\n');
            expect(names.FirstOrDefault() == "Speed & Pitch" && new[] { "Keep Pitch", "Pitch Shift", "Spectral Audio", "Clip Speed", "Target Duration" }.All(names.Contains)
                   && !names.Contains("Smooth Slow-Mo") && !names.Contains("Interpolation"), "a sound clip: Speed & Pitch (Keep Pitch, Pitch Shift, Spectral Audio), no Smooth Slow-Mo");
            expect(!Tree(pane.PinnedPanel).OfType<TextBlock>().Any(t => t.Text == "Opacity") && Names(pane.PinnedPanel).Contains("Studio Voice On"),
                "and no Opacity card (its own sound cards follow)");

            // Pitch Shift: the Pitch and Tempo filter's semitones, the tempo left alone, on the clip.
            var pitch = Tree(pane.SpeedPanel).OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "Pitch Shift");
            pitch.Value = 5;
            pane.FlushWrites();
            expect(sound.AudioFilters is { PitchTempo: true, PitchSemitones: 5, Tempo: 1 }, "Pitch Shift +5: the sound's Pitch and Tempo filter at +5 semitones, its tempo 1");
            pitch.Value = 0;
            pane.FlushWrites();
            expect(sound.AudioFilters is { PitchTempo: false, PitchSemitones: 0 }, "back at 0 the filter is off");

            // 5.6A review: a sound clip takes none of a stack's picture cards. Bypass All and Paste are off for it, and a paste onto clips (a
            // group's, a track's) gives each only the cards it can take.
            var look = HandPegApp.Models.Effects.EffectRegistry.All.First(d => d.Zone == HandPegApp.Models.Effects.EffectZone.Look && !d.Pinned && d.ForWholePicture);
            HandPegApp.Models.Effects.EffectState[] cards = [new() { Type = look.Type }, new() { Type = HandPegApp.Models.Effects.EffectRegistry.ChromaKey }];
            expect(!pane.BypassButton.IsEnabled, "Bypass All is off for a sound clip (no sound path reads it)");
            var clipboard = typeof(EffectsPane).GetField("_clipboard", BindingFlags.NonPublic | BindingFlags.Static)!;
            var copied = clipboard.GetValue(null);
            try
            {
                clipboard.SetValue(null, cards.ToList());
                Do("PasteEffects", false);
                Do("PasteEffects", true);
            }
            finally
            {
                clipboard.SetValue(null, copied);
            }

            expect(sound.Effects.Count == 0, "Paste Effects (and with Shift, to its track) puts nothing on it");
            var pictureClip = View(plain);
            vm.PasteEffectsToClips([pictureClip, sound], cards);
            vm.PasteEffectsToTrack(sound, cards);
            expect(sound.Effects.Count == 0 && !sound.ChromaKey && pictureClip.Effects.Count == 2 && pictureClip.ChromaKey,
                F($"pasted onto a picture and a sound together, and onto the sound's track: the picture takes both cards, the sound none ({look.Name}, Chroma Key)"));
            var paneSource = File.ReadAllText(Path.Combine(SourceProbe.Root, "src", "Views", "EffectsPane.xaml.cs"));
            expect(paneSource.Contains("var canPaste = _clipboard is { Count: > 0 } && _stack is not null && _layer is not { IsAudio: true };", StringComparison.Ordinal),
                "the More menu's Paste Effects and Paste Some Effects are off for a sound clip");

            // Its speed set and undone: Undo makes every view anew, and the pane follows the sound by its clip (as KeyLayer is followed).
            Fresh(vm);
            Do("CommitSpeed", 2.0);
            Pump();
            var sped = vm.Document.FindClip(song.Id)!.Speed;
            Fresh(vm);
            vm.Undo();
            Pump();
            var again = View(song);
            expect(sped == 2 && vm.Document.FindClip(song.Id)!.Speed == 1 && !ReferenceEquals(again, sound) && ReferenceEquals(Target(), again)
                   && Names(pane.SpeedPanel).FirstOrDefault() == "Speed & Pitch", "its speed from the card, undone: the pane still shows the sound clip (its view made anew)");
            pane.SelectSound(null);
            Pump();
            expect(Target() is { IsAudio: false }, "anything else selected gives the target back to the key layer");

            // A picture with no running time of its own (a text clip): no card.
            Select(vm.ViewOf(vm.Document.Tracks.First(t => t.Clips.Any(c => c.ContentKind == LayerKind.Text)).Clips[0])!);
            expect(pane.SpeedPanel.Children.Count == 0, "a text clip: no Speed & Time card");
        }
        finally
        {
            TimeDisplay.UseFrames = frames;
            PaneTitleSlot.Restore("Effects", slot);
            if (addedChrome)
                app.Resources.Remove("FlyoutChrome");
        }
    }
}
