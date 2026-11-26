using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.7.1 lane L1, timeline ergonomics: the true pointer-anchored 4x Spring-Zoom (a real view, the drag re-seated on rows relaid at its
/// scale), region-aware right-clicks, selectable cut seams (heal, nudge, crossfade, curve), the drag-over crossfade and its equal-power or
/// linear gain law, Top &amp; Tail, live trim readouts, the Ctrl+drag clone, and mini-stack thumbnails gated by visible height.
/// <para>Control cases drive the point-based seams with explicit clocks on a surface in no window; model cases drive a view model on a document
/// of their own. Every line says what it proved.</para>
/// </summary>
public static class TimelineErgonomics571Tests
{
    private const string Second = "D:/media/second.mkv", Other = "D:/media/other.mkv";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("timeline-ergonomics-571-spring-zoom", "# Spring-Zoom is a real view: ViewSpan / 4 anchored at the pointer, rows relaid at 4x and the drag re-seated on them (1/4 travel), the exact view back on release or Z.", SpringZoom);
        yield return Case("timeline-ergonomics-571-menu-regions", "# A right-click names the region it lands on: fade handle or wedge (Fade & Ease), cut seam (Seam), gain line or waveform (Gain), else the body.", MenuRegions);
        yield return Case("timeline-ergonomics-571-seam-select", "# A click on a cut selects the seam (through-edit or not), a rebuild finds it again by its clips; its keys: Ctrl+T, Delete, comma, period, Esc.", SeamSelect);
        yield return Case("timeline-ergonomics-571-seam-model", "# A through-edit heals into one clip, a cut rolls a frame, Ctrl+T crossfades, the gain law is set on both sides; each one step of Undo.", SeamModel);
        yield return Case("timeline-ergonomics-571-drag-crossfade", "# A move over its neighbour on the same track shows the crossfade it makes (a frame to 2 s, half of either), and the document abuts and crossfades on release.", DragCrossfade);
        yield return Case("timeline-ergonomics-571-crossfade-curve", "# The crossfade's gain law: equal power (out^2 + in^2 = 1) for two sounds, linear (out + in = 1) for one cut in two or when chosen, in the mixer's fades.", CrossfadeCurve);
        yield return Case("timeline-ergonomics-571-top-tail", "# Top & Tail ripple-trims the clip's start or end to the playhead and what follows moves up, one step of Undo; Shift+Q / Shift+E and [ / ] on WASD, Q / W elsewhere.", TopTail);
        yield return Case("timeline-ergonomics-571-trim-clone", "# Trims read out their change and length live; Ctrl+drag on a body clones the clip where it is let go (the original stays), a Ctrl click without travel stays a click.", TrimClone);
        yield return Case("timeline-ergonomics-571-mini-stack", "# A mini-stack lane shows its thumbnails and label once its visible height reaches 22 DIPs, while it opens or closes, never solid bars until fully open.", MiniStack);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // ----- The control: a surface 1000 x 400 DIPs (168 of headers, 832 of time area), 10 s across, no window -----

    private static UnifiedTimelineControl Surface()
    {
        var surface = new UnifiedTimelineControl();
        surface.Measure(new Size(1000, 400));
        surface.Arrange(new Rect(0, 0, 1000, 400));
        surface.SetView(0, 10, 10);
        return surface;
    }

    private static void Show(UnifiedTimelineControl surface, params TimelineRow[] rows)
    {
        surface.SetRows(rows);
        surface.UpdateLayout();
    }

    private static TimelineRow Row(string key, TimelineRowKind kind = TimelineRowKind.Video, double height = 56) =>
        new() { Key = key, Data = key, Name = key, Kind = kind, Height = height };

    private static (double Top, double Bottom) Lane(UnifiedTimelineControl surface, string key)
    {
        var lane = surface.LaidLanes.First(l => Equals(l.Key, key));
        return (lane.Top, lane.Top + lane.Height);
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static bool Spans(TimelineClip clip, double start, double end) => Near(clip.Start, start) && Near(clip.End, end);

    private static Point At(UnifiedTimelineControl surface, double seconds, double y) => new(surface.HeaderWidth + (seconds * surface.PixelsPerSecond) - surface.ViewOffsetX, y);

    // ----- timeline-ergonomics-571-spring-zoom -----

    private static void SpringZoom(Action<bool, string> expect, StringBuilder text)
    {
        var surface = Surface();
        var home = surface.PixelsPerSecond;
        var spans = new Dictionary<string, (double Start, double End)> { ["a"] = (1, 3), ["b"] = (5, 7) };
        var commits = new List<string>();
        var builds = 0;

        // The host: rows built from the model at the surface's scale, each block committing through its own scale (as MainWindow's do).
        TimelineRow Build()
        {
            builds++;
            var scale = surface.PixelsPerSecond;
            var row = Row("v");
            foreach (var (key, (start, end)) in spans)
            {
                row.Blocks.Add(new TimelineBlock
                {
                    Left = start * scale, Width = (end - start) * scale, Name = key, Clip = key, CanDrag = () => true, Resizable = true,
                    Commit = (left, width) => commits.Add(F($"{key}: {left / scale:0.000} to {(left + width) / scale:0.000} s")),
                });
            }

            return row;
        }

        // As MainWindow: the view applied at once, rows rebuilt only when its scale changed.
        surface.SpringViewRequested += view =>
        {
            var was = surface.PixelsPerSecond;
            surface.SetView(view.Start, view.Span, 10);
            if (surface.PixelsPerSecond != was)
                surface.SetRows([Build()]);
        };
        Show(surface, Build());
        var mid = (Lane(surface, "v").Top + Lane(surface, "v").Bottom) / 2;
        TimelineBlock BlockOf(string key) => surface.Rows[0].Blocks.First(b => Equals(b.Clip, key));

        var view = UnifiedTimelineControl.SpringViewOf(0, 10, 0.3 * 832, 832, UnifiedTimelineControl.SpringFactor);
        expect(Math.Abs(view.Start - 2.25) < 1e-9 && Math.Abs(view.Span - 2.5) < 1e-9, F($"the spring's view from 0-10 s at 30% of the width: {view.Start:0.00} s + {view.Span:0.00} s (a quarter, 3 s held under the pointer)"));

        expect(surface.RowsTransform is TranslateTransform,"the rows are only ever shifted, never stretched (no ScaleTransform on the time area)");

        surface.PressBlockAt(surface.Rows[0], BlockOf("a"), At(surface, 2, mid), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(surface, 3, mid), 1000, ModifierKeys.None);
        expect(Math.Abs(BlockOf("a").Left / home - 2) < 1e-6, "a pressed at 2 s and moved 1 s on: it starts at 2.00 s");

        var anchorX = At(surface, 3, mid).X;
        builds = 0;
        expect(surface.SpringZoomAt(anchorX), "Z (the dwell's spring) during the move springs");
        surface.FinishSpring();
        var zoomed = surface.PixelsPerSecond;
        expect(Math.Abs(zoomed / home - 4) < 1e-6 && builds > 0, F($"the view is 4x ({zoomed:0.0} px/s against {home:0.0}), rows rebuilt at that scale"));
        expect(surface.SpringHome is { } kept && kept.Start == 0 && kept.Span == 10, "the view it left is kept: 0 s + 10 s");
        expect(Math.Abs(surface.TimeAt(anchorX) - 3) < 1e-6, F($"the pointer's time is held: {surface.TimeAt(anchorX):0.000} s under it, as before"));
        expect(surface.IsDragging && Math.Abs(surface.DragScale - zoomed) < 1e-9, "the drag carried on, re-seated on the rebuilt rows at their scale");
        expect(Math.Abs(BlockOf("a").Left / zoomed - 2) < 1e-6, F($"the rebuilt a is drawn where the drag had it: {BlockOf("a").Left / zoomed:0.000} s"));

        surface.MoveBlockDrag(new Point(anchorX + 8.32, mid), 2000, ModifierKeys.None);
        expect(Math.Abs(BlockOf("a").Left / zoomed - 2.025) < 1e-6, F($"8.32 DIPs more: {BlockOf("a").Left / zoomed - 2:0.000} s on (a quarter of the 0.1 s it would be unzoomed)"));
        expect(surface.ReleaseBlockDrag() && commits.SequenceEqual(["a: 2.025 to 4.025 s"]), F($"let go: committed through the rebuilt block at its own scale ({string.Join("; ", commits)})"));
        surface.FinishSpring();
        expect(surface.SpringHome is null && Math.Abs(surface.PixelsPerSecond - home) < 1e-9 && surface.ViewOffsetX == 0, "released, it springs back to exactly 0 s + 10 s");

        // Z out mid-drag: back to the view it left while still dragging, re-seated again; a move made while zoomed keeps its time (F14).
        commits.Clear();
        surface.PressBlockAt(surface.Rows[0], BlockOf("b"), At(surface, 6, mid), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(surface, 6.5, mid), 1000, ModifierKeys.None);
        surface.SpringZoomAt(At(surface, 6.5, mid).X);
        surface.FinishSpring();
        var inScale = surface.PixelsPerSecond;
        var zoomedX = At(surface, 6.5, mid).X + 8.32;
        surface.MoveBlockDrag(new Point(zoomedX, mid), 2000, ModifierKeys.None);
        surface.SpringZoomAt(zoomedX);
        surface.FinishSpring();
        expect(Math.Abs(inScale / home - 4) < 1e-6 && surface.SpringHome is null && Math.Abs(surface.PixelsPerSecond - home) < 1e-9 && surface.IsDragging
               && Math.Abs(surface.DragScale - home) < 1e-9, "Z in then Z out mid-drag: 4x, then the exact view back, the drag still held at its scale");
        expect(Math.Abs(BlockOf("b").Left / home - 5.525) < 1e-6,
            F($"8.32 DIPs moved while zoomed, then Z out with the pointer still: b {BlockOf("b").Left / home - 5:0.000} s on, the zoomed edit kept"));
        expect(surface.ReleaseBlockDrag() && commits.SequenceEqual(["b: 5.525 to 7.525 s"]), F($"let go: {string.Join("; ", commits)}"));
        text.Append(F($"home {home:0.0} px/s, sprung {zoomed:0.0} px/s\n"));
    }

    // ----- timeline-ergonomics-571-menu-regions -----

    private static void MenuRegions(Action<bool, string> expect)
    {
        var surface = Surface();
        var scale = surface.PixelsPerSecond;
        var (row, sound) = (Row("v"), Row("s", TimelineRowKind.Audio));
        var a = new TimelineBlock
        {
            Left = 1 * scale, Width = 3 * scale, Name = "A", Clip = "A", Fades = (0.5, 0.5, TransitionStyle.Dissolve, TransitionStyle.Dissolve), FadeEdit = (_, _) => { },
            Gain = (0, 0.5, 0.5), GainEdit = _ => { },
        };
        var b = new TimelineBlock { Left = 4 * scale, Width = 3 * scale, Name = "B", Clip = "B" };
        var voice = new TimelineBlock { Left = 1 * scale, Width = 4 * scale, Name = "V", Clip = "V", Gain = (-6, 0, 0), GainEdit = _ => { } };
        row.Blocks.AddRange([a, b]);
        sound.Blocks.Add(voice);
        Show(surface, row, sound);
        var (top, bottom) = Lane(surface, "v");
        var (rectTop, rectHeight) = (top + 2, bottom - top - 4);
        ClipMenuRegion? RegionAt(double seconds, double y) => surface.RegionAtPoint(At(surface, seconds, y))?.Region;

        expect(RegionAt(1.5, rectTop + 3) == ClipMenuRegion.FadeIn, "on the fade-in handle (top band, at the fade's end): Fade In");
        expect(RegionAt(1.1, rectTop + rectHeight * 0.3) == ClipMenuRegion.FadeIn, "inside the shaded fade-in wedge, below the handle band: Fade In");
        expect(RegionAt(3.85, rectTop + rectHeight * 0.3) == ClipMenuRegion.FadeOut, "inside the fade-out wedge: Fade Out");
        var seam = surface.RegionAtPoint(At(surface, 4.03, (top + bottom) / 2));
        expect(seam is { Region: ClipMenuRegion.Seam, Seam: { } cut } && Equals(cut.Left.Clip, "A") && Equals(cut.Right.Clip, "B") && Math.Abs(cut.Seconds - 4) < 1e-9,
            "within the edge reach of the cut where A meets B: Seam, A on its left, B on its right, at 4.00 s");
        var gainY = rectTop + 3 + (1 - (0 - TimelineMetrics.LowestGain) / (TimelineMetrics.HighestGain - TimelineMetrics.LowestGain)) * (rectHeight - 6);
        expect(RegionAt(2.5, gainY) == ClipMenuRegion.Gain, "on A's gain line (0 dB): Gain");
        expect(RegionAt(2.5, bottom - 6) == ClipMenuRegion.Body && RegionAt(5.5, (top + bottom) / 2) == ClipMenuRegion.Body, "A's lower body and B's middle: the body (the core menu)");
        var (soundTop, soundBottom) = Lane(surface, "s");
        expect(RegionAt(3, (soundTop + soundBottom) / 2) == ClipMenuRegion.Gain && RegionAt(3, soundTop + 3) == ClipMenuRegion.Body,
            "an audio clip's waveform middle band: Gain; its top edge: the body");
        expect(surface.RegionAtPoint(At(surface, 8.5, (top + bottom) / 2)) is { Region: ClipMenuRegion.Body, Seam: null }, "an empty stretch of a track: no block, the body region (track menu)");
        expect(new TimelineMenuRequest(null, null, false, true, new System.Windows.Controls.ContextMenu()).Region == ClipMenuRegion.FadeIn,
            "a request made the old way (fade flag) still routes to the fade's region");
    }

    // ----- timeline-ergonomics-571-seam-select -----

    private static void SeamSelect(Action<bool, string> expect)
    {
        var surface = Surface();
        var scale = surface.PixelsPerSecond;
        var through = true;
        surface.ThroughEdit = (_, _) => through;
        var told = new List<TimelineSeamTarget?>();
        surface.SeamSelected += told.Add;
        var clicked = 0;
        surface.BlockClicked += (_, _) => clicked++;

        TimelineRow Build(double split)
        {
            var row = Row("v");
            foreach (var (key, start, end) in new[] { ("L", 1.0, split), ("R", split, 7.0) })
            {
                row.Blocks.Add(new TimelineBlock
                {
                    Left = start * scale, Width = (end - start) * scale, Name = key, Clip = key, CanDrag = () => true, Resizable = true, TrimmableStart = true,
                    Commit = (_, _) => { }, Edit = _ => { },
                });
            }

            return row;
        }

        Show(surface, Build(4));
        var mid = (Lane(surface, "v").Top + Lane(surface, "v").Bottom) / 2;
        var row = surface.Rows[0];
        surface.PressBlockAt(row, row.Blocks[0], At(surface, 4 - 0.02, mid), ModifierKeys.None, 0);
        var released = surface.ReleaseBlockDrag();
        var seam = surface.SelectedSeam;
        expect(released && seam is not null && Equals(seam.Left.Clip, "L") && Equals(seam.Right.Clip, "R") && seam.IsThroughEdit
               && Math.Abs(seam.Seconds - 4) < 1e-9 && clicked == 0 && told.Count == 1, "pressed on the cut (the roll grip) and let go in place: the seam is selected (L | R at 4.00 s, a through-edit), no clip click");

        Show(surface, Build(4));
        var again = surface.SelectedSeam;
        expect(again is not null && seam is not null && !ReferenceEquals(again.Right, seam.Right) && again.IsSame(seam), "the rows rebuilt: the same seam found again on the new blocks");
        through = false;
        Show(surface, Build(4));
        expect(surface.SelectedSeam is { IsThroughEdit: false }, "the host says it is no longer a through-edit: the seam says so too");
        Show(surface, Build(4.5));
        expect(surface.SelectedSeam is { } moved && Math.Abs(moved.Seconds - 4.5) < 1e-9, "the cut rolled to 4.5 s: the seam follows it");

        var gap = Row("v");
        gap.Blocks.Add(new TimelineBlock { Left = 1 * scale, Width = 2 * scale, Name = "L", Clip = "L" });
        gap.Blocks.Add(new TimelineBlock { Left = 4 * scale, Width = 2 * scale, Name = "R", Clip = "R" });
        Show(surface, gap);
        expect(surface.SelectedSeam is null && told[^1] is null, "the clips no longer meet: the seam is let go");

        Show(surface, Build(4));
        surface.PressBlockAt(surface.Rows[0], surface.Rows[0].Blocks[0], At(surface, 2.5, mid), ModifierKeys.None, 0);
        expect(surface.ReleaseBlockDrag() && surface.SelectedSeam is null && clicked == 1, "a click on a clip's body is a clip click, no seam");
        expect(surface.SeamAtPoint(At(surface, 4.05, mid)) is not null && surface.SeamAtPoint(At(surface, 4.2, mid)) is null,
            F($"the seam's reach is the trim edge's ({TimelineMetrics.EdgePixels} DIPs either side of the cut)"));

        expect(MainWindow.SeamKeyAction(Key.T, ModifierKeys.Control).Action == MainWindow.SeamAction.Crossfade, "Ctrl+T on a selected seam: its crossfade (ahead of New Tab)");
        expect(MainWindow.SeamKeyAction(Key.Delete, ModifierKeys.None).Action == MainWindow.SeamAction.Heal && MainWindow.SeamKeyAction(Key.Back, ModifierKeys.None).Action == MainWindow.SeamAction.Heal,
            "Delete or Backspace: heal (ahead of Delete Clip)");
        expect(MainWindow.SeamKeyAction(Key.OemComma, ModifierKeys.None) == (MainWindow.SeamAction.NudgeEarlier, true)
               && MainWindow.SeamKeyAction(Key.OemPeriod, ModifierKeys.None) == (MainWindow.SeamAction.NudgeLater, true), "comma / period: a frame earlier / later, repeating while held");
        expect(MainWindow.SeamKeyAction(Key.T, ModifierKeys.None).Action is null && MainWindow.SeamKeyAction(Key.Delete, ModifierKeys.Shift).Action is null,
            "T alone and Shift+Delete (Ripple Delete) are left to their shortcuts");
    }

    // ----- timeline-ergonomics-571-seam-model -----

    private static void SeamModel(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 10, 14, 2)));
        var track = vm.Document.Tracks[1];
        var id = track.Clips[0].Id;
        expect(vm.SplitLayer(vm.ViewOf(track.Clips[0])!, 12) && track.Clips.Count == 2, "a clip of 10-14 s cut at 12 s: two halves");
        var (left, right) = (vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!);
        expect(vm.IsThroughEdit(left, right), "the halves are a through-edit (one take, its file time running on across the cut)");
        expect(AudioProgramBuilder.IsThroughEdit(track.Clips[0], track.Clips[1]) && AudioProgramBuilder.SeamCurve(track.Clips[0], track.Clips[1]) == HandPegApp.Models.CrossfadeCurve.Linear,
            "so a crossfade across it keeps the level: linear while left at the default");

        var fps = vm.SequenceFps;
        vm.RollCut(left, right, 12 + 1 / fps);
        expect(track.Clips.Count == 2 && Math.Abs(track.Clips[1].Start - (12 + 1 / fps)) < 1e-6 && vm.IsThroughEdit(vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!),
            F($"period: the cut rolled one frame later ({track.Clips[1].Start:0.0000} s), still a through-edit"));
        vm.Undo();
        track = vm.Document.Tracks[1];
        expect(Math.Abs(track.Clips[1].Start - 12) < 1e-6, "Undo puts the cut back at 12 s (an undo step restores a document of its own)");

        (left, right) = (vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!);
        expect(vm.HealThroughEdit(left, right) && track.Clips.Count == 1 && Spans(track.Clips[0], 10, 14) && Near(track.Clips[0].MediaOffset, 2),
            "Delete heals it: one clip again, 10-14 s from 2 s into its file");
        vm.Undo();
        track = vm.Document.Tracks[1];
        expect(track.Clips.Count == 2, "Undo cuts it again (one step)");

        (left, right) = (vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!);
        vm.SetCrossfade(right, 0.5);
        expect(Math.Abs(track.Clips[1].TransitionIn - 0.5) < 1e-6 && !vm.IsThroughEdit(vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!),
            "Ctrl+T: a 0.5 s crossfade across the cut, which is then no through-edit (nothing to heal)");
        (left, right) = (vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!);
        expect(!vm.HealThroughEdit(left, right) && track.Clips.Count == 2, "a heal is refused there, nothing changed");

        expect(vm.SetCrossfadeCurve(left, right, HandPegApp.Models.CrossfadeCurve.Linear)
               && track.Clips[1].State.CrossfadeInCurve == HandPegApp.Models.CrossfadeCurve.Linear && track.Clips[0].State.CrossfadeOutCurve == HandPegApp.Models.CrossfadeCurve.Linear,
            "the Seam menu's Linear: the later clip fades in and the earlier out by it (both looks)");
        vm.Undo();
        track = vm.Document.Tracks[1];
        expect(track.Clips[1].State.CrossfadeInCurve is null or HandPegApp.Models.CrossfadeCurve.EqualPower, "Undo: equal power again (the default, saved as nothing)");
        expect(vm.Document.FindClip(id) is not null, "the first half keeps the clip's id throughout");
    }

    // ----- timeline-ergonomics-571-drag-crossfade -----

    private static void DragCrossfade(Action<bool, string> expect)
    {
        var surface = Surface();
        var scale = surface.PixelsPerSecond;
        surface.FrameRate = () => 30;
        var (row, other) = (Row("v"), Row("w"));
        var a = new TimelineBlock { Left = 0, Width = 4 * scale, Name = "A", Clip = "A" };
        var b = new TimelineBlock { Left = 5 * scale, Width = 4 * scale, Name = "B", Clip = "B", CanDrag = () => true, Commit = (_, _) => { }, CanMoveTo = (_, _) => true, MoveTo = (_, _) => { } };
        row.Blocks.AddRange([a, b]);
        Show(surface, row, other);
        var mid = (Lane(surface, "v").Top + Lane(surface, "v").Bottom) / 2;

        surface.PressBlockAt(row, b, At(surface, 6, mid), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(surface, 4.5, mid), 1000, ModifierKeys.None);
        expect(surface.LiveCrossfade is { } live && Equals(live.Clip, "A") && Math.Abs(live.Seconds - 0.5) < 1e-6, "B moved 1.5 s left over A's last 0.5 s: a live 0.50 s crossfade with A");
        surface.MoveBlockDrag(At(surface, 4.0, mid), 2000, ModifierKeys.None);
        expect(surface.LiveCrossfade is { Seconds: var deeper } && Math.Abs(deeper - 1) < 1e-6, "on 0.5 s more: it resizes to 1.00 s");
        surface.MoveBlockDrag(At(surface, 2.9, mid), 3000, ModifierKeys.None);
        expect(surface.LiveCrossfade is null, "2.1 s over A (past 2 s and half of A): none (the document refuses so deep an overlap)");
        surface.MoveBlockDrag(At(surface, 4.5, (Lane(surface, "w").Top + Lane(surface, "w").Bottom) / 2), 4000, ModifierKeys.None);
        expect(surface.LiveCrossfade is null, "over another track: none");
        surface.AbortDrag();

        // The model: the same move made by the view model abuts B to A and crossfades by the overlap.
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 10, 14, 0), DogfoodTests.FileClip(Other, 15, 19, 5)));
        var track = vm.Document.Tracks[1];
        var moving = vm.ViewOf(track.Clips[1])!;
        vm.CommitClipMove(moving, 13.5, 4);
        expect(track.Clips.Count == 2 && Math.Abs(track.Clips[1].Start - 14) < 1e-6 && Math.Abs(track.Clips[1].TransitionIn - 0.5) < 1e-6,
            F($"let go 0.5 s over the clip before: it abuts at {track.Clips[1].Start:0.00} s with a {track.Clips[1].TransitionIn:0.00} s crossfade"));
        expect(AudioProgramBuilder.SeamCurve(track.Clips[0], track.Clips[1]) == HandPegApp.Models.CrossfadeCurve.EqualPower,
            "two different sounds: their crossfade is equal power (the default)");
    }

    // ----- timeline-ergonomics-571-crossfade-curve -----

    private static void CrossfadeCurve(Action<bool, string> expect)
    {
        var worstPower = 0.0;
        var worstLevel = 0.0;
        for (var i = 0; i <= 20; i++)
        {
            var u = i / 20.0;
            // A crossfade side's fades as the mixer reads them: the fade in at u, the fade out at 1 - u (its time runs back from the end).
            var (inGain, outGain) = (AudioMixer.Curve(u, AudioProgramBuilder.EasingOf(HandPegApp.Models.CrossfadeCurve.EqualPower)),
                AudioMixer.Curve(1 - u, AudioProgramBuilder.EasingOf(HandPegApp.Models.CrossfadeCurve.EqualPower)));
            worstPower = Math.Max(worstPower, Math.Abs(inGain * inGain + outGain * outGain - 1));
            var (linearIn, linearOut) = (AudioMixer.Curve(u, AudioProgramBuilder.EasingOf(HandPegApp.Models.CrossfadeCurve.Linear)),
                AudioMixer.Curve(1 - u, AudioProgramBuilder.EasingOf(HandPegApp.Models.CrossfadeCurve.Linear)));
            worstLevel = Math.Max(worstLevel, Math.Abs(linearIn + linearOut - 1));
            var (contractOut, contractIn) = CrossfadeCurves.Gains(HandPegApp.Models.CrossfadeCurve.EqualPower, u);
            worstPower = Math.Max(worstPower, Math.Abs(contractOut - outGain) + Math.Abs(contractIn - inGain));
        }

        expect(worstPower < 1e-9, F($"equal power: out^2 + in^2 = 1 at every step, as CrossfadeCurves.Gains has it (worst {worstPower:E1})"));
        expect(worstLevel < 1e-9, F($"linear: out + in = 1 at every step (worst {worstLevel:E1})"));
        expect(AudioProgramBuilder.EasingOf(null) == EasingType.Linear, "a fade that is no crossfade stays linear (afade)");

        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Audio };
        var first = new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = Second, Start = 0, End = 4, SourceDuration = 60 };
        var second = new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = Other, Start = 4, End = 8, MediaOffset = 9, SourceDuration = 60, TransitionIn = 1 };
        var third = new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = Other, Start = 9, End = 12, SourceDuration = 60 };
        track.Clips.AddRange([first, second, third]);
        var layer = new Layer();
        var (into, outOf) = AudioProgramBuilder.CrossfadeSides(new HeardSound(layer, Other, second, track, -1));
        expect(into == HandPegApp.Models.CrossfadeCurve.EqualPower && outOf is null, "the later clip of a crossfade: its fade in is the crossfade's (equal power), its fade out none (a gap follows)");
        var (firstIn, firstOut) = AudioProgramBuilder.CrossfadeSides(new HeardSound(layer, Second, first, track, -1));
        expect(firstIn is null && firstOut == HandPegApp.Models.CrossfadeCurve.EqualPower, "the earlier clip: its fade out is the crossfade's");
        second.State = new LayerState { CrossfadeInCurve = HandPegApp.Models.CrossfadeCurve.Linear };
        expect(AudioProgramBuilder.CrossfadeSides(new HeardSound(layer, Second, first, track, -1)).Out == HandPegApp.Models.CrossfadeCurve.Linear,
            "Linear chosen on the seam (the later clip's look): both sides follow it");
    }

    // ----- timeline-ergonomics-571-top-tail -----

    private static void TopTail(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 10, 14, 0), DogfoodTests.FileClip(Other, 14, 18, 0)));
        var track = vm.Document.Tracks[1];
        var (first, second) = (track.Clips[0].Id, track.Clips[1].Id);
        TimelineClip Clip(Guid id) => vm.Document.FindClip(id)!;

        expect(vm.RippleTrimToPlayhead(vm.ViewOf(Clip(first))!, start: true, 11), "Top at 11 s");
        expect(Spans(Clip(first), 10, 13) && Near(Clip(first).MediaOffset, 1) && Spans(Clip(second), 13, 17),
            F($"the first clip lost its first second (10-13 s, from 1 s into its file), the next moved up ({Clip(second).Start:0.00}-{Clip(second).End:0.00} s)"));
        vm.Undo();
        expect(Spans(Clip(first), 10, 14) && Near(Clip(first).MediaOffset, 0) && Spans(Clip(second), 14, 18), "one Undo puts both back");

        expect(vm.RippleTrimToPlayhead(vm.ViewOf(Clip(first))!, start: false, 12.5), "Tail at 12.5 s");
        expect(Spans(Clip(first), 10, 12.5) && Spans(Clip(second), 12.5, 16.5), "the first ends at 12.5 s and the next follows its end");
        vm.Undo();

        expect(!vm.RippleTrimToPlayhead(vm.ViewOf(Clip(first))!, start: true, 16) && Near(Clip(first).End, 14) && vm.StatusText.Contains("playhead", StringComparison.Ordinal),
            "the playhead outside the clip: nothing changes, the status says to put it inside");

        var wasd = KeybindManager.Action(KeybindManager.TrimStartToPlayhead)!;
        expect(wasd.Defaults.SequenceEqual(["Shift+Q", "["]) && KeybindManager.Action(KeybindManager.TrimEndToPlayhead)!.Defaults.SequenceEqual(["Shift+E", "]"]),
            "WASD (Default): Top on Shift+Q and [, Tail on Shift+E and ] (Q / E stay frame steps)");
    }

    // ----- timeline-ergonomics-571-trim-clone -----

    private static void TrimClone(Action<bool, string> expect)
    {
        var surface = Surface();
        var scale = surface.PixelsPerSecond;
        var calls = new List<string>();
        var clones = new List<string>();
        surface.CloneRequested += (_, block, _, seconds) => clones.Add(F($"{block.Clip} at {seconds:0.00} s"));
        surface.BlockClicked += (_, block) => calls.Add(F($"click {block.Clip}"));
        var row = Row("v");
        var block = new TimelineBlock
        {
            Left = 1 * scale, Width = 2 * scale, Name = "C", Clip = "C", CanDrag = () => true, Resizable = true, TrimmableStart = true,
            Commit = (left, width) => calls.Add(F($"commit {left / scale:0.00}+{width / scale:0.00}")), Edit = done => calls.Add(F($"edit {done.Kind}")),
        };
        row.Blocks.Add(block);
        Show(surface, row);
        var mid = (Lane(surface, "v").Top + Lane(surface, "v").Bottom) / 2;

        surface.PressBlockAt(row, block, At(surface, 3 - 0.02, mid), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(surface, 3.48, mid), 1000, ModifierKeys.None);
        expect(surface.TrimReadout == "+0.50 s \u00B7 2.50 s", F($"an end trim 0.5 s out reads \"{surface.TrimReadout}\""));
        surface.AbortDrag();
        surface.PressBlockAt(row, block, At(surface, 1 + 0.02, mid), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(surface, 1.77, mid), 1000, ModifierKeys.None);
        expect(surface.TrimReadout == "-0.75 s \u00B7 1.25 s", F($"a start trim 0.75 s in reads \"{surface.TrimReadout}\" (the change in length)"));
        surface.AbortDrag();
        expect(UnifiedTimelineControl.TrimText(0.001, 3) == "0.00 s \u00B7 3.00 s" && UnifiedTimelineControl.TrimText(-1.234, 0.5) == "-1.23 s \u00B7 0.50 s",
            "under 5 ms reads as no change; seconds to the hundredth");

        calls.Clear();
        surface.PressBlockAt(row, block, At(surface, 2, mid), ModifierKeys.Control, 0);
        surface.MoveBlockDrag(At(surface, 4, mid), 1000, ModifierKeys.Control);
        expect(surface.LiveCrossfade is null && surface.TrimReadout is null, "Ctrl+drag on the body: a clone in flight, no crossfade or trim readout");
        expect(surface.ReleaseBlockDrag() && clones.SequenceEqual(["C at 3.00 s"]) && calls.Count == 0 && Math.Abs(block.Left - 1 * scale) < 1e-6,
            "let go 2 s on: the host is asked for a clone at 3.00 s, nothing committed, the original back at 1.00 s");
        surface.PressBlockAt(row, block, At(surface, 2, mid), ModifierKeys.Control, 0);
        expect(surface.ReleaseBlockDrag() && clones.Count == 1 && calls.SequenceEqual(["click C"]), "a Ctrl click without travel: a click (the selection toggles), no clone");

        // The model: one step, the original untouched.
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 10, 14, 0)));
        var track = vm.Document.Tracks[1];
        var original = track.Clips[0];
        expect(vm.CloneClipTo(vm.ViewOf(original)!, 20) && track.Clips.Count == 2 && Spans(track.Clips[1], 20, 24) && Spans(track.Clips[0], 10, 14)
               && track.Clips[1].Id != original.Id, "cloned to 20 s: a new clip 20-24 s on the same track, the original still 10-14 s");
        vm.Undo();
        track = vm.Document.Tracks[1];
        expect(track.Clips.Count == 1 && track.Clips[0].Id == original.Id, "one Undo takes the clone away");
    }

    // ----- timeline-ergonomics-571-mini-stack -----

    private static void MiniStack(Action<bool, string> expect)
    {
        var surface = Surface();
        var scale = surface.PixelsPerSecond;
        var red = new WriteableBitmap(4, 4, 96, 96, PixelFormats.Bgra32, null);
        var pixels = Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 16).SelectMany(p => p).ToArray();
        red.WritePixels(new Int32Rect(0, 0, 4, 4), pixels, 16, 0);
        red.Freeze();
        var purple = new SolidColorBrush(Color.FromRgb(0x70, 0x30, 0xA0));
        purple.Freeze();
        var parent = Row("p");
        parent.Blocks.Add(new TimelineBlock { Left = 1 * scale, Width = 6 * scale, Name = "P", Clip = "P", Fill = purple });
        var child = Row("c");
        child.Blocks.Add(new TimelineBlock { Left = 2 * scale, Width = 3 * scale, Name = "Cutout", Clip = "Cutout", Fill = purple, Frames = [(red, 0, 3 * scale)] });
        parent.Children.Add(child);
        Show(surface, parent);

        expect(Math.Abs(surface.LaneHeightOf("c") - 11.2) < 1e-6 && surface.LaneShowsPictures("c") == false, "collapsed: the Cutout's lane is a 11.2 DIP bar, too short for pictures");
        SetOpenness(surface, parent, 0.25);
        expect(surface.LaneHeightOf("c") >= 22 && surface.LaneShowsPictures("c") == true && surface.LaidLanes.First(l => Equals(l.Key, "c")).Mini,
            F($"a quarter open ({surface.LaneHeightOf("c"):0.0} DIPs, still a mini-stack lane): its pictures already show"));
        var (top, bottom) = Lane(surface, "c");
        var shot = Shot(surface);
        var at = ((int)((bottom + top) / 2) * 1000 + (int)(surface.HeaderWidth + 3.5 * scale)) * 4;
        expect(shot[at + 2] > 200 && shot[at] < 60, F($"drawn: the lane shows its thumbnail (red {shot[at + 2]}, blue {shot[at]}) where the old rule drew a solid bar"));
        SetOpenness(surface, parent, 0.2);
        expect(surface.LaneShowsPictures("c") == false, F($"a fifth open ({surface.LaneHeightOf("c"):0.0} DIPs): under 22, the plain bar again"));
        SetOpenness(surface, parent, 1);
        expect(surface.LaneShowsPictures("c") == true && !surface.LaidLanes.First(l => Equals(l.Key, "c")).Mini, "fully open: a full lane with its pictures");

        var tall = Row("t", height: 120);
        tall.Blocks.Add(new TimelineBlock { Left = 0, Width = 2 * scale, Name = "T", Clip = "T" });
        var tallParent = Row("q");
        tallParent.Children.Add(tall);
        Show(surface, tallParent);
        expect(surface.LaneShowsPictures("t") == true, "a tall track's collapsed bar (24 DIPs) is tall enough for pictures too");
    }

    // The parent's visual stack openness set to a value mid-animation (Current), laid out at once (as a frame of the animation would).
    private static void SetOpenness(UnifiedTimelineControl surface, TimelineRow row, double openness)
    {
        surface.SetOpennessForTests(row.Key, ChildStack.Visual, openness);
        surface.UpdateLayout();
    }

    private static byte[] Shot(UnifiedTimelineControl surface)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1000, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var taken = new byte[1000 * 400 * 4];
        bitmap.CopyPixels(taken, 1000 * 4, 0);
        return taken;
    }
}
