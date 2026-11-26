using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Shell;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.7.1 lane L2, region-aware menus and live looping hover previews: which menu each region of a clip fills (Fade &amp; Ease, Seam
/// with its Equal Power / Linear curve, Audio &amp; Gain, the slim core menu), the menus themselves on a view model of their own, the loop
/// window and wrap rules of the engine, and the hover lifecycle (fades, seam crossfades, gain, Auto-Level, ducking): no Undo step, nothing
/// dirty, the clip and the parked playhead back on leave, close, choose or play. Every line says what it proved.
/// </summary>
public static class TrackMenuHover571Tests
{
    private const string Second = "D:/media/second.mkv";

    public static IEnumerable<GraphResult> All()
    {
        yield return GoldenKit.Case("track-menu-hover-571-routing", "# Each region of a clip picks its own menu (header and other rows keep the track menu); the loop window and its wrap.", Routing);
        yield return GoldenKit.Case("track-menu-hover-571-menus", "# Fade & Ease, Seam (Equal Power vs Linear), Audio & Gain and the slim core clip menu, built on a view model's clips.", Menus);
        yield return GoldenKit.Case("track-menu-hover-571-lifecycle", "# Hover previews loop round their target on the engine and leave nothing behind: no Undo step, nothing dirty, clip and playhead back.", Lifecycle);
        yield return GoldenKit.Case("track-menu-hover-571-audio", "# Auto-Level -14 LUFS and Auto-Duck preview live on the clip's sound and put its gain and ducking back on leave.", AudioHover);
    }

    // ----- track-menu-hover-571-routing -----

    private static void Routing(Action<bool, string> expect)
    {
        var row = new Layer();
        var sound = Activator.CreateInstance(typeof(MainWindow).GetNestedType("LayerSound", BindingFlags.NonPublic)!, row)!;
        MainWindow.RegionMenu Kind(ClipMenuRegion region, object? clip, bool header = false, bool seam = false, object? data = null) =>
            MainWindow.MenuKindOf(region, header, data ?? row, clip, seam);

        expect(Kind(ClipMenuRegion.FadeIn, row) == MainWindow.RegionMenu.FadeEase && Kind(ClipMenuRegion.FadeOut, sound) == MainWindow.RegionMenu.FadeEase,
            "a fade handle, wedge or curve (in or out, its own sound's too): the Fade & Ease menu");
        expect(Kind(ClipMenuRegion.Seam, row, seam: true) == MainWindow.RegionMenu.Seam && Kind(ClipMenuRegion.Seam, row) == MainWindow.RegionMenu.Core,
            "a cut seam: the Seam menu; a seam region with no seam found falls back to the core menu");
        expect(Kind(ClipMenuRegion.Gain, row) == MainWindow.RegionMenu.Gain && Kind(ClipMenuRegion.Gain, sound) == MainWindow.RegionMenu.Gain,
            "the gain line or the waveform: the Audio & Gain menu");
        expect(Kind(ClipMenuRegion.Body, row) == MainWindow.RegionMenu.Core, "the body: the slim core clip menu");
        expect(Kind(ClipMenuRegion.FadeIn, row, header: true) == MainWindow.RegionMenu.Track && Kind(ClipMenuRegion.Body, null) == MainWindow.RegionMenu.Track
               && Kind(ClipMenuRegion.Body, "effect", data: "lane") == MainWindow.RegionMenu.Track,
            "a header, an empty spot and an effect lane keep the track menu (FillTrackMenu)");
        expect(new TimelineMenuRequest(null, null, false, true, new ContextMenu()).Region == ClipMenuRegion.FadeIn
               && new TimelineMenuRequest(null, null, false, null, new ContextMenu()).Region == ClipMenuRegion.Body,
            "a request made the old way (fade flag only) routes by its flag");

        expect(MainWindow.EaseLabel(TransitionStyles.Easing(TransitionStyle.Dissolve)) == "Linear" && MainWindow.EaseLabel(TransitionStyles.Easing(TransitionStyle.WipeLeft)) == "Ease In-Out"
               && MainWindow.EaseLabel(TransitionStyles.Easing(TransitionStyle.SlideUp)) == "Ease Out" && MainWindow.EaseLabel(TransitionStyles.Easing(TransitionStyle.Zoom)) == "Overshoot",
            "each style shows its ease: Dissolve Linear, wipes Ease In-Out, slides Ease Out, Zoom Overshoot");

        var fade = GpuPreviewEngine.LoopWindowOf(10, 10.5, 60);
        expect(fade is var (a, b) && Near(a, 9.75) && Near(b, 10.75), F($"a 0.5 s fade at 10 s loops 1 s round its middle ({fade})"));
        var cut = GpuPreviewEngine.LoopWindowOf(20, 22, 60);
        expect(cut is var (c, d) && Near(d - c, GpuPreviewEngine.LoopPreviewMostSeconds) && Near((c + d) / 2, 21), F($"a 2 s crossfade loops at most 2 s, centred ({cut})"));
        var start = GpuPreviewEngine.LoopWindowOf(0, 0.25, 60);
        var end = GpuPreviewEngine.LoopWindowOf(59.8, 60, 60, 30);
        expect(start is var (e, f) && Near(e, 0) && f > 0.9 && end is var (g, h) && h <= 60 - 2 / 30.0 + 1e-9 && h - g >= 0.99,
            F($"held inside the Sequence: from 0 at its start ({start}), two frames clear of its end ({end?.From:0.000}-{end?.To:0.000}) so play never pauses there"));
        expect(GpuPreviewEngine.LoopWindowOf(0, 1, 0.05) is null && GpuPreviewEngine.LoopWindowOf(double.NaN, 1, 60) is null, "nothing to loop in a Sequence too short, or round no time");

        expect(double.IsNaN(GpuPreviewEngine.LoopWrapTarget(10.2, 10, 11, 1 / 60.0)) && Near(GpuPreviewEngine.LoopWrapTarget(10.99, 10, 11, 1 / 60.0), 10)
               && Near(GpuPreviewEngine.LoopWrapTarget(11.4, 10, 11), 10) && Near(GpuPreviewEngine.LoopWrapTarget(9, 10, 11), 10),
            "the pump wraps to the window's start a refresh before its end (or once past it, or run before it), else plays on");
        expect(SourceProbe.InOrder(SourceProbe.Member(Engine(), "partial void LoopPreviewTurn()"), "SeekMailSlot.IsPending(ref _pendingSeekBits)", "LoopWrapTarget(", "SeekMailSlot.TryPostIfEmpty(ref _pendingSeekBits, target)") && StaleWrapYields()
               && !SourceProbe.Member(Engine(), "partial void LoopPreviewTurn()").Contains("Seek(target)", StringComparison.Ordinal),
            "the wrap is posted to the seek mail-slot from the pump (one per pass, never over a pending seek), not through the UI thread's Seek");
        expect(SourceProbe.InOrder(SourceProbe.Member(Engine(), "partial void AddLoopPreviewHints("), "_loopWarm", "GpuSeekHintKind.TabRoll", "added++"),
            "the menu's window is pre-decoded with idle-priority hints (at most two clips) while the menu is open");
    }

    // ----- track-menu-hover-571-menus -----

    private static void Menus(Action<bool, string> expect)
    {
        var lane = DogfoodTests.Lane(Sound(DogfoodTests.FileClip(Second, 10, 12, 2)), Sound(DogfoodTests.FileClip(Second, 12, 16, 4)));
        var vm = DogfoodTests.Open(lane);
        var track = vm.Document.Tracks[1];
        var (left, right) = (vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!);
        var window = Window(vm);
        var row = new TimelineRow { Key = "t", Data = left };
        var (leftBlock, rightBlock) = (new TimelineBlock { Clip = left, Name = "L" }, new TimelineBlock { Clip = right, Name = "R" });
        var history = vm.HistoryPosition;

        // Fade & Ease.
        var fade = Fill(window, new TimelineMenuRequest(row, rightBlock, false, false, new ContextMenu()) { Region = ClipMenuRegion.FadeOut });
        var lengths = Sub(fade, "Length");
        var styles = Items(fade).Where(i => i.Icon is System.Windows.Shapes.Path).ToList();
        expect(fade is { Filled: true } && Title(fade) == "No Fade Out" && lengths is not null && Headers(lengths).SequenceEqual(["0.25 s", "0.5 s", "1 s", "2 s"]),
            $"Fade & Ease on a fade out: titled \"{Title(fade)}\", a Length submenu of {Headers(lengths)?.Count ?? 0} lengths");
        expect(styles.Count == TransitionStyles.All.Count && styles.All(s => s.InputGestureText.Length > 0) && styles[0].InputGestureText == "Linear",
            F($"every style ({styles.Count}) with its ease beside it (Dissolve: {(styles.Count > 0 ? styles[0].InputGestureText : "")})"));
        expect(Headers(fade).Contains("Add Fade Out (" + AppSettings.Current.DefaultTransitionSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s)"),
            "and Add Fade Out at the default length");

        // Seam: crossfade, its length, Equal Power vs Linear, roll, heal.
        var seam = new TimelineSeamTarget(row, leftBlock, rightBlock, 12) { IsThroughEdit = true };
        var seamMenu = Fill(window, new TimelineMenuRequest(row, rightBlock, false, null, new ContextMenu()) { Region = ClipMenuRegion.Seam, Seam = seam, Seconds = 12 });
        var curves = Sub(seamMenu, "Audio Curve");
        expect(seamMenu is { Filled: true } && Title(seamMenu) == "Cut: Through-Edit" && Headers(seamMenu).Any(h => h.StartsWith("Add Crossfade (", StringComparison.Ordinal))
               && Sub(seamMenu, "Crossfade Length") is { Items.Count: 4 },
            $"the Seam menu on a through-edit: \"{Title(seamMenu)}\", Add Crossfade [Ctrl+T] and four lengths");
        expect(curves is not null && Headers(curves).SequenceEqual(["Equal Power", "Linear"]) && ((MenuItem)curves.Items[0]).IsChecked && !((MenuItem)curves.Items[1]).IsChecked,
            "Audio Curve offers Equal Power (ticked, the default) and Linear");
        var heal = Items(seamMenu).FirstOrDefault(i => (string)i.Header == "Heal Through-Edit");
        expect(Headers(seamMenu).Contains("Roll Cut 1 Frame Earlier") && Headers(seamMenu).Contains("Roll Cut 1 Frame Later") && Headers(seamMenu).Contains("Roll Cut to Playhead")
               && heal is { IsEnabled: true, InputGestureText: "Delete" },
            "Roll a frame either way or to the playhead, and Heal Through-Edit [Delete], enabled on a through-edit");

        window.SetSeamCurve(seam, CrossfadeCurve.Linear);
        track = vm.Document.Tracks[1];
        expect(track.Clips[1].State.CrossfadeInCurve == CrossfadeCurve.Linear && track.Clips[0].State.CrossfadeOutCurve == CrossfadeCurve.Linear
               && vm.HistoryPosition.Undo == history.Undo + 1,
            "Linear chosen (as its item does) sets the gain law on both sides of the cut, one step of Undo");

        // Audio & Gain.
        (left, right) = (vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!);
        var gain = Fill(window, new TimelineMenuRequest(row, new TimelineBlock { Clip = left, Name = "L" }, false, null, new ContextMenu()) { Region = ClipMenuRegion.Gain });
        expect(gain is { Filled: true } && Title(gain) == "Gain: 0 dB" && Headers(gain).Contains("+6 dB") && Headers(gain).Contains("0 dB  (As Recorded)")
               && Headers(gain).Contains("Auto-Level Speech (-14 LUFS)") && Headers(gain).Contains("Toggle A/V Link") && !Headers(gain).Contains("Auto-Duck against Voice"),
            $"the Audio & Gain menu on a video clip with sound: \"{Title(gain)}\", gain steps, Auto-Level -14 LUFS and its A/V link (ducking is on its sound lane)");

        // The slim core menu.
        var body = Fill(window, new TimelineMenuRequest(row, new TimelineBlock { Clip = left, Name = "L" }, false, null, new ContextMenu()) { Region = ClipMenuRegion.Body });
        var top = Items(body).ToList();
        var count = body.Menu.Items.OfType<MenuItem>().Count();
        expect(body is { Filled: true } && count is >= 8 and <= 12, F($"the core clip menu: {count} items ({string.Join(", ", top.Select(i => i.Header))})"));
        expect(top.Take(3).Select(i => (string)i.Header).SequenceEqual(["Split at Playhead", "Ripple Delete", "Delete"]) && Sub(body, "Edit") is { Items.Count: > 0 }
               && Sub(body, "Tools & Track") is { } tools && Headers(tools).Contains("Track"),
            "Split, Ripple Delete and Delete first; the rest in Edit and Tools & Track (the Track submenu inside)");
        expect(Sub(body, "Edit") is { } edit && Headers(edit).Contains("Duplicate") && Headers(edit).Contains("Paste Attributes..."),
            "Duplicate and Paste Attributes... live in Edit");

        var header = Fill(window, new TimelineMenuRequest(row, null, true, null, new ContextMenu()));
        expect(header is { Filled: false } && header.Menu.Items.Count == 0, "a header right-click is left to the track menu (nothing filled)");
        expect(vm.HistoryPosition.Undo == history.Undo + 1, "building the menus took no Undo step (only the chosen Linear did)");
    }

    // ----- track-menu-hover-571-lifecycle -----

    private static void Lifecycle(Action<bool, string> expect)
    {
        var lane = DogfoodTests.Lane(Sound(DogfoodTests.FileClip(Second, 10, 12, 2)), Sound(DogfoodTests.FileClip(Second, 12, 16, 4)));
        var vm = DogfoodTests.Open(lane);
        var track = vm.Document.Tracks[1];
        var (left, right) = (vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!);
        vm.PositionMs = 3000;
        var host = new Recorder();
        using var hover = new PausedHoverPreview(vm) { Loop = host };
        var (history, steps, dirty) = (vm.HistoryPosition, vm.UndoStepsTaken, vm.MayHaveUnsavedChanges);
        bool Untouched() => vm.HistoryPosition == history && vm.UndoStepsTaken == steps && vm.MayHaveUnsavedChanges == dirty;

        // A fade style: on the view only, looping round the fade; the playhead stays parked.
        hover.HoverFade([right], fadeIn: true, TransitionStyle.WipeLeft);
        expect(!hover.IsShowing, "nothing shows before the dwell (sweeping across a menu previews nothing)");
        hover.ShowPendingNow();
        var standard = AppSettings.Current.DefaultTransitionSeconds;
        expect(hover.IsShowing && right.FadeInStyle == TransitionStyle.WipeLeft && Near(right.FadeInSeconds, Math.Round(standard, 2)) && Near(track.Clips[1].FadeInSeconds, 0),
            F($"after the dwell the view shows a {right.FadeInSeconds:0.##} s Wipe Left fade in; the clip in the document has none"));
        expect(host.Started is var (from, to) && from <= 12 + 1e-9 && to >= 12 + standard - 1e-9 && hover.Looping == host.Started && Near(vm.PositionMs, 3000),
            F($"the engine loops {host.Started} round the fade; the playhead stays parked at 3 s"));
        expect(Untouched(), "no Undo step, no gesture step, nothing dirty");
        hover.Leave();
        expect(!hover.IsShowing && right.FadeInStyle == TransitionStyle.Dissolve && Near(right.FadeInSeconds, 0) && host.Stops.SequenceEqual([(3.0, false)]) && hover.Looping is null && Untouched(),
            "leaving: the view as the document holds it, the loop stopped and the engine parked back at 3 s, still nothing in Undo");

        // A fade length.
        hover.HoverFadeLength([left], fadeIn: false, 1);
        hover.ShowPendingNow();
        expect(Near(left.FadeOutSeconds, 1) && Near(track.Clips[0].FadeOutSeconds, 0) && host.Starts == 2, "a fade length (1 s out) shows on the view and loops");
        hover.MenuClosed();
        expect(Near(left.FadeOutSeconds, 0) && host.WarmEnded && Untouched(), "closing the menu puts it back and drops the pre-warm");

        // A seam crossfade by Linear.
        hover.HoverSeam(left, right, 0.5, CrossfadeCurve.Linear);
        hover.ShowPendingNow();
        expect(Near(right.TransitionIn, 0.5) && right.CrossfadeInCurve == CrossfadeCurve.Linear && left.CrossfadeOutCurve == CrossfadeCurve.Linear && Near(track.Clips[1].TransitionIn, 0)
               && host.Started is var (cutFrom, cutTo) && Near((cutFrom + cutTo) / 2, 12),
            F($"a 0.5 s Linear crossfade across the cut shows on both views, looping {host.Started} centred on the cut; the document has a plain cut"));
        hover.Leave();
        expect(Near(right.TransitionIn, 0) && right.CrossfadeInCurve == CrossfadeCurve.EqualPower && left.CrossfadeOutCurve == CrossfadeCurve.EqualPower && Untouched(),
            "leaving: a plain cut, equal power on both views, nothing in Undo");

        // Choosing the item: the preview goes first, then the item's own step from the restored state.
        hover.HoverFade([right], fadeIn: true, TransitionStyle.SlideUp);
        hover.ShowPendingNow();
        hover.Leave();
        vm.SetClipFadeStyle(right, true, TransitionStyle.SlideUp);
        track = vm.Document.Tracks[1];
        expect(vm.HistoryPosition.Undo == history.Undo + 1 && vm.ViewOf(track.Clips[1])!.FadeInStyle == TransitionStyle.SlideUp,
            "a chosen style is one step of Undo of its own, taken after the preview was put back");
        vm.Undo();
        track = vm.Document.Tracks[1];
        (left, right) = (vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!);
        (history, steps, dirty) = (vm.HistoryPosition, vm.UndoStepsTaken, vm.MayHaveUnsavedChanges);

        // Play pressed while a preview loops: it is put back and the engine keeps playing from the parked playhead.
        var stops = host.Stops.Count;
        hover.HoverGain(left, 6);
        hover.ShowPendingNow();
        expect(Near(left.GainDb, 6) && Near(track.Clips[0].GainDb, 0), "a +6 dB gain shows on the view's sound only");
        vm.IsPlaying = true;
        expect(!hover.IsShowing && Near(left.GainDb, 0) && host.Stops.Count == stops + 1 && host.Stops[^1] == (3.0, true) && Untouched(),
            "play pressed: the gain back, the loop ended without pausing the user's play, from the parked 3 s");
        hover.HoverGain(left, 6);
        hover.ShowPendingNow();
        expect(!hover.IsShowing, "while playing nothing previews");
        vm.IsPlaying = false;

        // No engine: the playhead moves into the fade and back.
        using var plain = new PausedHoverPreview(vm);
        plain.HoverFade([right], fadeIn: true, TransitionStyle.Dissolve);
        plain.ShowPendingNow();
        var moved = vm.PositionMs;
        plain.Leave();
        expect(moved is > 12000 and < 13000 && Near(vm.PositionMs, 3000), F($"with no loop host the playhead is moved into the fade ({moved / 1000:0.###} s) and back to 3 s"));
    }

    // ----- track-menu-hover-571-audio -----

    private static void AudioHover(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(Sound(DogfoodTests.FileClip(Second, 10, 14, 2))));
        var track = vm.Document.Tracks[1];
        var clip = vm.ViewOf(track.Clips[0])!;
        vm.PositionMs = 11000;
        var host = new Recorder();
        using var hover = new PausedHoverPreview(vm) { Loop = host };
        var (history, steps, dirty) = (vm.HistoryPosition, vm.UndoStepsTaken, vm.MayHaveUnsavedChanges);
        bool Untouched() => vm.HistoryPosition == history && vm.UndoStepsTaken == steps && vm.MayHaveUnsavedChanges == dirty;

        var measured = 0;
        Task<double?> Measure()
        {
            measured++;
            return Task.FromResult<double?>(-7.5);
        }

        hover.HoverAutoLevel(clip, Measure);
        hover.ShowPendingNow();
        expect(measured == 1 && Near(clip.GainDb, -7.5) && Near(track.Clips[0].GainDb, 0) && host.Started is var (from, to) && Near(from, 11) && Near(to, 13),
            F($"Auto-Level -14 LUFS: measured once, the clip heard at {clip.GainDb:+0.0;-0.0} dB on its view, looping {host.Started} from the playhead"));
        hover.Leave();
        expect(Near(clip.GainDb, 0) && Untouched(), "leaving: its gain back to 0 dB, nothing in Undo, nothing dirty");
        hover.HoverAutoLevel(clip, Measure);
        hover.ShowPendingNow();
        expect(measured == 1 && Near(clip.GainDb, -7.5), "hovering again uses the measured gain (no second measurement)");
        hover.Leave();

        var slow = new TaskCompletionSource<double?>();
        using var late = new PausedHoverPreview(vm) { Loop = host };
        late.HoverAutoLevel(clip, () => slow.Task);
        late.ShowPendingNow();
        late.Leave();
        slow.SetResult(-2);
        expect(Near(clip.GainDb, 0) && !late.IsShowing && Untouched(), "a measurement finishing after its item was left shows nothing");

        hover.HoverDucking(clip, clip, true);
        hover.ShowPendingNow();
        expect(clip.AutoDuck && hover.IsShowing, "Auto-Duck: the sound heard ducked under the voice while hovered");
        hover.Leave();
        expect(!clip.AutoDuck && Untouched(), "leaving: ducking off again, nothing in Undo, nothing dirty");
    }

    // ----- helpers -----

    private sealed class Recorder : ILoopPreviewHost
    {
        public (double From, double To)? Started;
        public int Starts;
        public List<(double ParkAt, bool KeepPlaying)> Stops = [];
        public bool WarmEnded;

        public void Warm(double from, double to) => WarmEnded = false;

        public void EndWarm() => WarmEnded = true;

        public bool Start(double from, double to)
        {
            Started = (from, to);
            Starts++;
            return true;
        }

        public void Stop(double parkAt, bool keepPlaying) => Stops.Add((Math.Round(parkAt, 6), keepPlaying));
    }

    private sealed record MenuFill(ContextMenu Menu, bool Filled);

    // A clip with sound (its first audio stream).
    private static TimelineClip Sound(TimelineClip clip)
    {
        clip.AudioStreamIndex = 0;
        return clip;
    }

    // The window as far as the menus reach: its view model and its hover preview's holder (none shown, nothing made).
    private static MainWindow Window(MainViewModel vm) => MainWindow.MenuShellForTests(vm);

    private static MenuFill Fill(MainWindow window, TimelineMenuRequest request) => new(request.Menu, window.FillRegionMenuForTests(request));

    // Review F29, deterministic: the UI's park seek posted between the pump's pending check and its wrap stays; the stale wrap yields.
    private static bool StaleWrapYields()
    {
        var slot = SeekMailSlot.Empty;
        var first = SeekMailSlot.TryPostIfEmpty(ref slot, 10) && SeekMailSlot.TryTake(ref slot, out var wrapped) && wrapped == 10;
        SeekMailSlot.Post(ref slot, 42);
        return first && !SeekMailSlot.TryPostIfEmpty(ref slot, 10) && SeekMailSlot.TryTake(ref slot, out var parked) && parked == 42;
    }

    private static IEnumerable<MenuItem> Items(MenuFill? menu) => menu?.Menu.Items.OfType<MenuItem>() ?? [];

    private static List<string> Headers(MenuFill? menu) => [.. Items(menu).Select(i => i.Header as string ?? "")];

    private static List<string> Headers(MenuItem? menu) => [.. (menu?.Items.OfType<MenuItem>() ?? []).Select(i => i.Header as string ?? "")];

    private static string Title(MenuFill? menu) => Items(menu).FirstOrDefault() is { IsEnabled: false, Header: string title } ? title : "";

    private static MenuItem? Sub(MenuFill? menu, string header) => Items(menu).FirstOrDefault(i => i.Header as string == header);

    private static MenuItem? Sub(MenuItem menu, string header) => menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == header);

    private static string Engine() => File.ReadAllText(Path.Combine(SourceProbe.Root, "src", "Services", "GpuPreviewEngine.LoopPreview.cs"));

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
