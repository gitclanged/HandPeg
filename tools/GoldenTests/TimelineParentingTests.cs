using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.5A lane A: the Timeline's velocity-gated drags (Transit Mode, the settle, the vertical deadzone, Shift's time lock) and child parenting (a track header nested or reordered, a clip tethered to a clip).
/// <para>Every control case drives the point-based seams with explicit clocks (ms), never synthetic input and never a mouse capture, in no
/// window (the dwell's timer is never made there: SettleTick stands for it). The model case drives the view model on a document of its own.
/// Every line says what it proved.</para>
/// </summary>
public static class TimelineParentingTests
{
    private const string Second = "D:/media/second.mkv", Other = "D:/media/other.mkv";
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("timeline-drag-settle-gate", "# The drags' settle (DragSettle): Transit Mode over 160 DIP/s, settled after 75 ms under 60 DIP/s or still, a zone awake stays awake until Transit.", SettleGate);
        yield return Case("timeline-drag-transit-seam", "# A clip swept past a + New Track seam never wakes it; resting on it 75 ms (or arriving slowly) does; let go mid-sweep, it goes onto the row under it.", TransitSeam);
        yield return Case("timeline-drag-vertical-deadzone", "# A move changes rows only 6 DIPs past the row it is on; Shift keeps a move's time exactly once it leaves its lane, while a slide along its lane steps by frames.", VerticalDeadzone);
        yield return Case("timeline-clip-tether", "# A clip let go on the bottom edge of a clip it overlaps (settled, or Alt pressed during the move) is tethered to it as its child, a curve drawn to it.", ClipTether);
        yield return Case("timeline-drag-rest-keys", "# A move at rest looks at the keys every 75 ms: Alt pressed then tethers at once, Shift keeps its time once it is off its lane; it looks again only when they change.", RestKeys);
        yield return Case("timeline-header-drag-nest", "# A track's header dragged: to a seam between the tracks at once, nested under another track's lower half once settled, a Sub-Lane taken out at a seam; over its own stack nothing, and Esc cancels.", HeaderDragNest);
        yield return Case("track-nest-reorder-attach-model", "# The model of it: a track nested and moved with its Sub-Lanes, a Sub-Lane taken out of its stack, a clip attached as a child; each one step of Undo.", NestReorderAttachModel);
        yield return Case("track-tether-not-a-cutout", "# A clip tethered from another track is its parent's child, never one of its template's pieces; a video of the parent's own file and a sound on an audio track are never tethered.", TetherNotACutout);
        yield return Case("track-nest-tether-pieces", "# A tether is a tether wherever it lies (the Tethered mark): nested under its parent's track either way round, moved onto a lane under it, its parent moved, its lane following; each time it survives a preset replace and is copied under no new clip. A Cutout moved off its lane and a clip its Link switch locks stay pieces.", NestTetherPieces);
        yield return Case("track-tether-mark-model", "# The tether mark goes with the link: Undo and Redo, a split, copy and paste, the Link switch; a project saved and read keeps it, and one written before the mark (an undo step too) has its tethers found as it is read, its pieces left pieces.", TetherMarkModel);
        yield return Case("timeline-drag-scroll-keys", "# A drag placed again while the view moves under a still pointer (the auto-scroll, a wheel scroll or zoom) takes the keys as they are now (a source check: no key is pressed without a window).", ScrollKeys);
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

    // ----- The control: a surface 1000 x 400 DIPs, 10 s across its time area, no window -----

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

    private static TimelineRow Row(string key, TimelineRowKind kind = TimelineRowKind.Video) => new() { Key = key, Data = key, Name = key, Kind = kind, Height = 56 };

    private static (double Top, double Bottom) Lane(UnifiedTimelineControl surface, string key)
    {
        var lane = surface.LaidLanes.First(l => Equals(l.Key, key));
        return (lane.Top, lane.Top + lane.Height);
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

    // Whether two shots differ anywhere in the 3 x 3 pixels round a point.
    private static bool Differs(byte[] a, byte[] b, double x, double y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var (px, py) = ((int)Math.Floor(x) + dx, (int)Math.Floor(y) + dy);
                if (px < 0 || py < 0 || px >= 1000 || py >= 400)
                    continue;
                var at = (py * 1000 + px) * 4;
                if (a[at] != b[at] || a[at + 1] != b[at + 1] || a[at + 2] != b[at + 2])
                    return true;
            }
        }

        return false;
    }

    // ----- timeline-drag-settle-gate -----

    private static void SettleGate(Action<bool, string> expect, StringBuilder text)
    {
        expect(DragSettle.TransitSpeed == 160 && DragSettle.SettleSpeed == 60 && DragSettle.SettleMs == 75 && DragSettle.WindowMs == 50 && DragSettle.StillDip == 0.5,
            "the pins: Transit over 160 DIP/s, settled under 60 DIP/s for 75 ms, the speed over the newest 50 ms, under 0.5 DIP is still");

        var settle = new DragSettle();
        settle.Begin(new Point(0, 0), 0);
        settle.Sample(new Point(300, 0), 20);
        expect(settle.InTransit(20) && Math.Abs(settle.Speed - 15000) < 1, F($"a sweep of 300 DIPs in 20 ms ({settle.Speed:0} DIP/s): Transit Mode"));
        expect(!settle.IsSettled(60) && !settle.Awake(false, 60) && !settle.Awake(true, 30), "40 ms after it stops: not settled, no zone wakes; 10 ms after it, even a zone awake before sleeps (still in Transit)");
        expect(!settle.InTransit(75) && settle.Awake(true, 75) && !settle.Awake(false, 75), "55 ms after the last move Transit is over: a zone already awake stays so, a new one does not wake yet");
        expect(settle.IsSettled(95) && settle.Awake(false, 95), "75 ms after the last move: settled, a new zone wakes (the dwell)");

        // A drift at 50 DIP/s from the press: settled 75 ms on, while it still moves.
        settle.Begin(new Point(0, 0), 0);
        var settledAt = double.NaN;
        for (var t = 10; t <= 150; t += 10)
        {
            settle.Sample(new Point(t / 20.0, 0), t);
            if (double.IsNaN(settledAt) && settle.IsSettled(t))
                settledAt = t;
        }

        expect(settledAt == 80 && Math.Abs(settle.Speed - 50) < 0.01, F($"a drift at {settle.Speed:0} DIP/s from the press: settled at {settledAt} ms (the first move 75 ms or more on), still moving"));

        // At 100 DIP/s: between the two speeds, the hysteresis.
        settle.Begin(new Point(0, 0), 0);
        for (var t = 10; t <= 200; t += 10)
            settle.Sample(new Point(t / 10.0, 0), t);
        expect(!settle.InTransit(200) && !settle.IsSettled(200) && settle.Awake(true, 200) && !settle.Awake(false, 200),
            F($"a drift at {settle.Speed:0} DIP/s (between 60 and 160): a zone awake stays awake, a new one never wakes"));

        // A hand's jitter (0.3 DIP back and forth every 5 ms) is the pointer still.
        settle.Begin(new Point(100, 100), 0);
        for (var t = 5; t <= 100; t += 5)
            settle.Sample(new Point(100 + (t / 5 % 2 == 1 ? 0.3 : 0), 100), t);
        expect(settle.IsSettled(80) && !settle.InTransit(100), "a jitter of 0.3 DIP every 5 ms for 100 ms: still, settled 75 ms after the press");

        // After a rest, the first move's speed is over the time since the sample before it; the clock never goes back.
        settle.Begin(new Point(0, 0), 0);
        settle.Sample(new Point(40, 0), 1000);
        expect(Math.Abs(settle.Speed - 40) < 0.01 && !settle.InTransit(1000), F($"after a rest of 1 s, a move of 40 DIPs: {settle.Speed:0} DIP/s (over the time since the move before it), never Transit"));
        settle.Sample(new Point(41, 0), 500);
        expect(settle.LastSampleMs == 1000, "a sample stamped earlier than the last is taken as at the last (the clock never goes back)");
    }

    // ----- timeline-drag-transit-seam -----

    private static void TransitSeam(Action<bool, string> expect, StringBuilder text)
    {
        var surface = Surface();
        var scale = surface.PixelsPerSecond;
        var calls = new List<string>();
        var block = new TimelineBlock
        {
            Left = 1 * scale, Width = 2 * scale, Name = "clip", Clip = "clip",
            CanDrag = () => true,
            Commit = (_, _) => calls.Add("commit"),
            CanMoveTo = (_, _) => true,
            MoveTo = (data, _) => calls.Add($"move to {data}"),
            CanMoveToNewTrack = (_, _, _) => true,
            MoveToNewTrack = (data, above, _) => calls.Add($"new track {(above ? "above" : "below")} {data}"),
        };
        var (upper, lower) = (Row("upper"), Row("lower"));
        lower.Blocks.Add(block);
        Show(surface, upper, lower);
        var (up, low) = (Lane(surface, "upper"), Lane(surface, "lower"));
        Point At(double seconds, double y) => new(surface.HeaderWidth + seconds * scale, y);
        bool Seam(string key, bool above) => surface.NewTrackSeam is { } seam && Equals(seam.Key, key) && seam.Above == above;

        surface.PressBlockAt(lower, block, At(2, low.Top + 28), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(2, up.Bottom - 8), 20, ModifierKeys.None);
        expect(surface.NewTrackSeam is null && Equals(surface.DragTargetKey, "upper"), "swept 37 DIPs up in 20 ms into the band of the seam between the rows: Transit, the seam asleep; the row under the pointer is the target as before");
        surface.SettleTick(60);
        expect(surface.NewTrackSeam is null, "40 ms after the sweep stopped: still asleep");
        surface.SettleTick(95);
        expect(Seam("lower", above: true) && surface.DragTargetKey is null, "75 ms after it stopped (the dwell's look again): settled, the + New Track seam wakes, the row target goes");
        surface.MoveBlockDrag(At(2, up.Bottom - 8), 95, ModifierKeys.None);
        surface.MoveBlockDrag(At(2, up.Bottom - 11), 125, ModifierKeys.None);
        expect(Seam("lower", above: true), "a drift of 3 DIPs in 30 ms within its band (100 DIP/s, under Transit): the seam stays awake (the hysteresis)");
        surface.MoveBlockDrag(At(2, up.Top + 4), 135, ModifierKeys.None);
        expect(surface.NewTrackSeam is null && Equals(surface.DragTargetKey, "upper"), "swept on 41 DIPs in 10 ms into the band of the seam over the top row: that seam asleep, the row under the pointer the target");
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["move to upper"]), "let go mid-sweep: onto the row under it, never onto a new track");

        // A slow approach (50 DIP/s from the press): the seam wakes on the move that enters its band, with no look again.
        calls.Clear();
        block.Left = 1 * scale;
        surface.PressBlockAt(lower, block, At(2, low.Top + 28), ModifierKeys.None, 1000);
        double? woke = null;
        for (var i = 1; i <= 36; i++)
        {
            var y = low.Top + 28 - i * 0.5;
            surface.MoveBlockDrag(At(2, y), 1000 + i * 10, ModifierKeys.None);
            if (woke is null && surface.NewTrackSeam is not null)
                woke = y - low.Top;
        }

        expect(Seam("lower", above: true) && woke == 13.5, F($"a slow approach (50 DIP/s, slow for 75 ms and more): the seam wakes on the very move that enters its band ({woke} DIPs into the lower row), no rest needed"));
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["new track above lower"]), "let go there: onto a new track above the lower row, once");
    }

    // ----- timeline-drag-vertical-deadzone -----

    private static void VerticalDeadzone(Action<bool, string> expect, StringBuilder text)
    {
        var surface = Surface();
        var scale = surface.PixelsPerSecond;
        var calls = new List<string>();
        var block = new TimelineBlock
        {
            Left = 1 * scale, Width = 2 * scale, Name = "clip", Clip = "clip",
            CanDrag = () => true,
            Commit = (left, _) => calls.Add(F($"commit at {left / scale:0.00} s")),
            CanMoveTo = (_, _) => true,
            MoveTo = (data, left) => calls.Add(F($"move to {data} at {left / scale:0.00} s")),
        };
        var (upper, lower) = (Row("upper"), Row("lower"));
        lower.Blocks.Add(block);
        Show(surface, upper, lower);
        var (up, low) = (Lane(surface, "upper"), Lane(surface, "lower"));
        Point At(double seconds, double y) => new(surface.HeaderWidth + seconds * scale, y);

        surface.PressBlockAt(lower, block, At(2, low.Top + 3), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(2, low.Top - 2), 1000, ModifierKeys.None);
        expect(surface.DragTargetKey is null, "pressed 3 DIPs into the lower row, moved 2 DIPs past its top (into the row above): within the deadzone, still on its own row");
        surface.MoveBlockDrag(At(2, low.Top - 7), 2000, ModifierKeys.None);
        expect(Equals(surface.DragTargetKey, "upper"), "7 DIPs past its top: past the 6 DIP deadzone, the row above is the target");
        surface.MoveBlockDrag(At(2, low.Top + 3), 3000, ModifierKeys.None);
        expect(Equals(surface.DragTargetKey, "upper"), "back 3 DIPs into the lower row: the row it is over holds it the same way (within 6 DIPs past that row's edge)");
        surface.MoveBlockDrag(At(2, low.Top + 8), 4000, ModifierKeys.None);
        expect(surface.DragTargetKey is null, "8 DIPs in: its own row again");
        expect(surface.ReleaseBlockDrag() && calls.Count == 0, "let go where it began: nothing moved, nothing committed");

        // Shift: time held on a move up or down past the deadzone; never on a slide along its row.
        var (middle, upperMiddle) = ((low.Top + low.Bottom) / 2, (up.Top + up.Bottom) / 2);
        block.Left = 1 * scale;
        surface.PressBlockAt(lower, block, At(2, middle), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(3.5, middle + 4), 1000, ModifierKeys.Shift);
        expect(Math.Abs(block.Left - 2.5 * scale) < 0.5 && surface.DragTargetKey is null, "Shift on a slide along its row (4 DIPs down: within the deadzone): the block follows the pointer 1.5 s on, its time not held");
        surface.MoveBlockDrag(At(3.5, upperMiddle), 2000, ModifierKeys.Shift);
        expect(block.Left == 1 * scale && Equals(surface.DragTargetKey, "upper"), "Shift on a move up onto the row above: its time held exactly where it began (1.00 s), the pointer 1.5 s along");
        surface.MoveBlockDrag(At(4, middle + 2), 3000, ModifierKeys.Shift);
        expect(block.Left == 1 * scale && surface.DragTargetKey is null, "back on its own row, Shift still held: still held (for the rest of the drag while Shift is)");
        surface.MoveBlockDrag(At(4, middle + 2), 4000, ModifierKeys.None);
        expect(Math.Abs(block.Left - 3 * scale) < 0.5, "Shift let go: the block follows the pointer again (2 s on)");
        surface.MoveBlockDrag(At(3.5, upperMiddle), 5000, ModifierKeys.Shift);
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["move to upper at 1.00 s"]), "let go on the row above with Shift: onto it at exactly its own time");

        // 5.5A review: Shift keeps the time only once the move leaves its own lane. A slide along it with the hand drifting 7 to 10 DIPs up or
        // down (still on its lane) keeps Precision Unsnapped: frame steps (30 fps here).
        surface.FrameRate = () => 30;
        bool OnFrame(double frame) => Math.Abs(block.Left / scale * 30 - frame) < 1e-6;
        calls.Clear();
        block.Left = 1 * scale;
        surface.PressBlockAt(lower, block, At(2, middle), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(3.53, middle + 7), 1000, ModifierKeys.Shift);
        expect(OnFrame(76) && surface.DragTargetKey is null, F($"Shift on a slide along its lane, the hand 7 DIPs low: on frame 76 ({block.Left / scale:0.0000} s, 2.53 s stepped to the frame), its time not held"));
        surface.MoveBlockDrag(At(3.47, middle - 10), 2000, ModifierKeys.Shift);
        expect(OnFrame(74) && surface.DragTargetKey is null, F($"the hand 10 DIPs high, still on its lane: on frame 74 ({block.Left / scale:0.0000} s), still not held"));
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["commit at 2.47 s"]), "let go: placed on its own row at that frame");

        calls.Clear();
        block.Left = 1 * scale;
        surface.PressBlockAt(lower, block, At(2, middle), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(3.53, middle + 8), 1000, ModifierKeys.Shift);
        surface.MoveBlockDrag(At(3.53, upperMiddle), 2000, ModifierKeys.Shift);
        expect(block.Left == 1 * scale && Equals(surface.DragTargetKey, "upper"), "then on with Shift onto the row above: its time held exactly (1.00 s, not the frame it was on), the row above the target");
        surface.MoveBlockDrag(At(3.53, middle + 8), 3000, ModifierKeys.Shift);
        expect(block.Left == 1 * scale && surface.DragTargetKey is null, "back over its own lane, Shift still held: still held");
        surface.MoveBlockDrag(At(3.53, upperMiddle), 4000, ModifierKeys.Shift);
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["move to upper at 1.00 s"]), "let go on the row above: onto it at exactly its own time");
    }

    // ----- timeline-clip-tether -----

    private static void ClipTether(Action<bool, string> expect, StringBuilder text)
    {
        var surface = Surface();
        var scale = surface.PixelsPerSecond;
        var calls = new List<string>();
        var refuse = false;
        var parent = new TimelineBlock { Left = 1 * scale, Width = 3 * scale, Name = "", Clip = "P", Family = new BlockFamily("P", null, false, BlockEdges.None) };
        var child = new TimelineBlock
        {
            Left = 2 * scale, Width = 1 * scale, Name = "C", Clip = "C", Family = new BlockFamily("C", null, false, BlockEdges.None),
            CanDrag = () => true,
            Commit = (_, _) => calls.Add("commit"),
            CanMoveTo = (_, _) => true,
            MoveTo = (data, _) => calls.Add($"move to {data}"),
            CanMoveToNewTrack = (_, _, _) => true,
            MoveToNewTrack = (data, above, _) => calls.Add($"new track {(above ? "above" : "below")} {data}"),
            CanAttachTo = (to, _) => refuse || to.Clip is not "P" ? null : "Talk",
            AttachTo = (to, left) => calls.Add(F($"attach C to {to.Clip} at {left / scale:0.00} s")),
        };
        var other = new TimelineBlock { Left = 6 * scale, Width = 1 * scale, Name = "X", Clip = "X", Family = new BlockFamily("X", null, false, BlockEdges.None), CanDrag = () => true, Commit = (_, _) => calls.Add("X") };
        var (upper, lower) = (Row("upper"), Row("lower"));
        upper.Blocks.Add(parent);
        lower.Blocks.Add(child);
        lower.Blocks.Add(other);
        Show(surface, upper, lower);
        var (up, low) = (Lane(surface, "upper"), Lane(surface, "lower"));
        var (lowMiddle, upMiddle, band) = ((low.Top + low.Bottom) / 2, (up.Top + up.Bottom) / 2, up.Bottom - 20);
        Point At(double seconds, double y) => new(surface.HeaderWidth + seconds * scale, y);
        bool Tethered() => surface.AttachTarget is { } attach && Equals(attach.Key, "P") && attach.Name == "Talk";
        void Again()
        {
            calls.Clear();
            child.Left = 2 * scale;
        }

        surface.PressBlockAt(lower, child, At(2.5, lowMiddle), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(2.5, band), 20, ModifierKeys.None);
        expect(surface.AttachTarget is null && Equals(surface.DragTargetKey, "upper"), "swept up onto the band along the bottom of the clip above it (overlapping it): no tether in Transit, the row the target");
        var swept = Shot(surface);
        surface.SettleTick(95);
        expect(Tethered() && surface.DragTargetKey is null && surface.NewTrackSeam is null, "rested there past the dwell: tethered to that clip, its name on the pill from the host (\"Talk\"), no row target, no seam");
        var tethered = Shot(surface);
        var middle = (surface.HeaderWidth + 2.5 * scale, (low.Top + 2 + up.Bottom - 2) / 2);
        expect(Differs(swept, tethered, middle.Item1, middle.Item2), "the tether is drawn: the curve from the block's top to the clip's bottom edge, at its middle");
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["attach C to P at 2.00 s"]), "let go: attached as that clip's child at its time, and nothing else (no move along its row, none onto the row above)");

        Again();
        surface.PressBlockAt(lower, child, At(2.5, lowMiddle), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(2.5, upMiddle), 20, ModifierKeys.Alt);
        expect(Tethered(), "Alt pressed during the move, over the middle of the clip: tethered at once, Transit or not");
        surface.MoveBlockDrag(At(2.5, upMiddle), 30, ModifierKeys.None);
        expect(surface.AttachTarget is null && Equals(surface.DragTargetKey, "upper"), "Alt let go there: no tether (without Alt only the bottom edge tethers), the row the target");
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["move to upper"]), "let go: onto the row above as before");

        Again();
        surface.PressBlockAt(lower, child, At(2.5, lowMiddle), ModifierKeys.Alt, 0);
        surface.MoveBlockDrag(At(2.5, upMiddle), 20, ModifierKeys.Alt);
        expect(surface.AttachTarget is null, "Alt held from the press (a cut where it is clicked, a slip of a clip that slips): never a tether at once");
        surface.ReleaseBlockDrag();

        Again();
        refuse = true;
        surface.PressBlockAt(lower, child, At(2.5, lowMiddle), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(2.5, band), 20, ModifierKeys.None);
        surface.SettleTick(95);
        expect(surface.AttachTarget is null && Equals(surface.DragTargetKey, "upper"), "the host refusing the tether there: the row is the target as before");
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["move to upper"]), "and let go, it goes onto that row");
        refuse = false;

        Again();
        surface.PressBlockAt(lower, child, At(2.5, lowMiddle), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(2.5, up.Bottom - 6), 20, ModifierKeys.None);
        surface.SettleTick(95);
        expect(surface.NewTrackSeam is { } seam && Equals(seam.Key, "lower") && seam.Above && surface.AttachTarget is null,
            "lower on that clip, in the band of the + New Track seam under its row: the seam, never the tether (the tether's band is just above it)");
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["new track above lower"]), "let go there: onto a new track");

        Again();
        surface.IsSelected = clip => clip is "C" or "X";
        Show(surface, upper, lower);
        surface.PressBlockAt(lower, child, At(2.5, lowMiddle), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(2.5, band), 20, ModifierKeys.Alt);
        surface.SettleTick(95);
        expect(surface.AttachTarget is null, "a selected group dragged there, even with Alt: never tethered");
        surface.ReleaseBlockDrag();
        surface.IsSelected = null;
        expect(!calls.Exists(c => c.StartsWith("attach", StringComparison.Ordinal)), "and none attached as it is let go");
    }

    // ----- timeline-drag-rest-keys (5.5A review) -----

    private static void RestKeys(Action<bool, string> expect, StringBuilder text)
    {
        var surface = Surface();
        var scale = surface.PixelsPerSecond;
        var (calls, looks, asked) = (new List<string>(), 0, 0);
        var parent = new TimelineBlock { Left = 1 * scale, Width = 3 * scale, Name = "", Clip = "P", Family = new BlockFamily("P", null, false, BlockEdges.None) };
        var child = new TimelineBlock
        {
            Left = 2 * scale, Width = 1 * scale, Name = "C", Clip = "C", Family = new BlockFamily("C", null, false, BlockEdges.None),
            CanDrag = () => true,
            Commit = (_, _) => calls.Add("commit"),
            CanMoveTo = (_, _) =>
            {
                looks++;
                return true;
            },
            MoveTo = (data, left) => calls.Add(F($"move to {data} at {left / scale:0.00} s")),
            CanAttachTo = (to, _) =>
            {
                asked++;
                return to.Clip is "P" ? "Talk" : null;
            },
            AttachTo = (to, left) => calls.Add(F($"attach C to {to.Clip} at {left / scale:0.00} s")),
        };
        var (upper, lower) = (Row("upper"), Row("lower"));
        upper.Blocks.Add(parent);
        lower.Blocks.Add(child);
        Show(surface, upper, lower);
        var (up, low) = (Lane(surface, "upper"), Lane(surface, "lower"));
        var (lowMiddle, upMiddle) = ((low.Top + low.Bottom) / 2, (up.Top + up.Bottom) / 2);
        Point At(double seconds, double y) => new(surface.HeaderWidth + seconds * scale, y);

        // Alt pressed while the pointer rests over the middle of the clip above (only its bottom band tethers without Alt).
        surface.PressBlockAt(lower, child, At(2.5, lowMiddle), ModifierKeys.None, 0);
        surface.MoveBlockDrag(At(2.5, upMiddle), 20, ModifierKeys.None);
        expect(surface.AttachTarget is null && Equals(surface.DragTargetKey, "upper") && looks == 1, "moved onto the middle of the clip above, no keys: no tether, the row the target (one look)");
        expect(surface.RestTick(60, ModifierKeys.None) is { } wait && Math.Abs(wait - 40) < 1e-9 && looks == 1, "40 ms after the move, the dwell's timer: waits on 40 ms (the 35 left of the dwell, and 5), no look");
        expect(surface.RestTick(95, ModifierKeys.None) == DragSettle.SettleMs && looks == 2 && surface.AttachTarget is null,
            "75 ms after it: the dwell's look (still no tether there), and the timer kept ticking every 75 ms while the move rests");
        expect(surface.RestTick(170, ModifierKeys.None) == DragSettle.SettleMs && looks == 2, "75 ms on, the keys unchanged: no look again (nothing asked of the host)");
        expect(surface.RestTick(245, ModifierKeys.Alt) == DragSettle.SettleMs && surface.AttachTarget is { } attach && Equals(attach.Key, "P") && attach.Name == "Talk" && asked == 1,
            "Alt pressed while it rests: at the next tick the keys changed, it looks again and is tethered at once, the pointer never moved");
        expect(surface.RestTick(320, ModifierKeys.Alt) == DragSettle.SettleMs && asked == 1, "Alt still held: no look again");
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["attach C to P at 2.00 s"]), "let go without a move: attached as Alt asked");

        // Shift pressed while it rests on the row above, the block 1.5 s along: its time kept, as at a move off its lane; let go of, it follows again.
        calls.Clear();
        child.Left = 2 * scale;
        surface.PressBlockAt(lower, child, At(2.5, lowMiddle), ModifierKeys.None, 1000);
        surface.MoveBlockDrag(At(4, upMiddle), 1020, ModifierKeys.None);
        surface.RestTick(1095, ModifierKeys.None);
        expect(Math.Abs(child.Left - 3.5 * scale) < 0.5 && Equals(surface.DragTargetKey, "upper"), "moved 1.5 s along onto the row above and rested: the block after the pointer (3.50 s)");
        surface.RestTick(1170, ModifierKeys.Shift);
        expect(child.Left == 2 * scale && Equals(surface.DragTargetKey, "upper"), "Shift pressed while it rests: at the next tick its time is kept exactly (2.00 s), the row above still the target");
        surface.RestTick(1245, ModifierKeys.None);
        expect(Math.Abs(child.Left - 3.5 * scale) < 0.5, "Shift let go while it rests: after the pointer again");
        surface.RestTick(1320, ModifierKeys.Shift);
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["move to upper at 2.00 s"]), "Shift again, let go: onto the row above at its own time");

        // A header's drag at rest: one look, then the timer stops (its keys change nothing).
        surface.CanDragRow = _ => true;
        surface.PressHeaderAt(lower, new Point(60, lowMiddle), 2000);
        surface.MoveHeaderDrag(new Point(60, lowMiddle - 20), 2020);
        expect(surface.IsDragging && surface.RestTick(2095, ModifierKeys.Alt) is null, "a header's drag rested past the dwell: one look, and no tick after it");
        surface.ReleaseHeaderDrag();
    }

    // ----- timeline-header-drag-nest -----

    private static void HeaderDragNest(Action<bool, string> expect, StringBuilder text)
    {
        var surface = Surface();
        var calls = new List<string>();
        var ended = 0;
        var captions = Row("captions", TimelineRowKind.Captions);
        var (a, b, c, d) = (Row("A"), Row("B"), Row("C"), Row("D"));
        var cutout = Row("A-cut");
        cutout.Parent = a;
        a.Children.Add(cutout);
        surface.CanDragRow = row => row.Kind == TimelineRowKind.Video;
        surface.CanMoveRowTo = (row, beside, _) => !ReferenceEquals(row, beside);
        surface.CanNestRow = (row, under) => !ReferenceEquals(row, under) && !ReferenceEquals(row.Parent, under);
        surface.RowMoveRequested += (row, beside, above) => calls.Add($"move {row.Key} {(above ? "above" : "below")} {beside.Key}");
        surface.RowNestRequested += (row, under) => calls.Add($"nest {row.Key} under {under.Key}");
        surface.DragEnded += () => ended++;
        Show(surface, captions, a, b, c, d);
        surface.SetExpanded(a, ChildStack.Visual, true);
        surface.UpdateLayout();
        var (laneA, laneCut, laneB, laneC, laneD) = (Lane(surface, "A"), Lane(surface, "A-cut"), Lane(surface, "B"), Lane(surface, "C"), Lane(surface, "D"));
        double Middle((double Top, double Bottom) lane) => (lane.Top + lane.Bottom) / 2;
        bool Seam(string key, bool above) => surface.RowSeam is { } seam && Equals(seam.Key, key) && seam.Above == above;

        expect(!surface.PressHeaderAt(captions, new Point(60, 20), 0), "the captions' header: the host does not let it be dragged");

        expect(surface.PressHeaderAt(c, new Point(60, Middle(laneC)), 0) && !surface.IsDragging, "C's header pressed: held for a drag, not dragging yet");
        surface.MoveHeaderDrag(new Point(60, laneB.Top + 10), 20);
        expect(surface.IsDragging && Seam("B", above: true) && surface.NestTargetKey is null,
            F($"dragged straight up {Middle(laneC) - laneB.Top - 10:0} DIPs in 20 ms into B's upper half: the seam over B (between A's stack and B) at once, Transit or not"));
        expect(surface.ReleaseHeaderDrag() && calls.SequenceEqual(["move C above B"]) && ended == 1 && !surface.IsDragging, "let go: moved there, once; the drag's end told once");

        calls.Clear();
        surface.PressHeaderAt(d, new Point(60, Middle(laneD)), 1000);
        surface.MoveHeaderDrag(new Point(80, laneB.Top + 42), 1020);
        expect(surface.NestTargetKey is null && Seam("C", above: true), "D's header swept up, and 20 DIPs right, onto B's lower half: no nest in Transit (the seam under B meanwhile)");
        var swept = Shot(surface);
        surface.SettleTick(1095);
        expect(Equals(surface.NestTargetKey, "B") && surface.RowSeam is null, "rested past the dwell: nested under B (indented 18 DIPs or more, on its lower half)");
        expect(Differs(swept, Shot(surface), 28, laneB.Top + 38), "the nest is drawn: the indented accent bracket down B's header");
        expect(surface.ReleaseHeaderDrag() && calls.SequenceEqual(["nest D under B"]) && surface.IsExpanded(b, ChildStack.Visual), "let go: D made B's Sub-Lane, B's stack opened (kept by its key)");

        calls.Clear();
        surface.PressHeaderAt(d, new Point(60, Middle(laneD)), 2000);
        surface.MoveHeaderDrag(new Point(70, laneB.Top + 42), 2020);
        surface.SettleTick(2095);
        expect(surface.NestTargetKey is null && Seam("C", above: true), "only 10 DIPs right on B's lower half, settled: no nest, the seam under B");
        surface.MoveHeaderDrag(new Point(80, laneB.Top + 10), 2200);
        surface.SettleTick(2300);
        expect(surface.NestTargetKey is null && Seam("B", above: true), "20 DIPs right on B's upper half, settled: no nest, the seam over B");
        expect(surface.ReleaseHeaderDrag() && calls.SequenceEqual(["move D above B"]), "let go: moved over B");

        calls.Clear();
        surface.PressHeaderAt(cutout, new Point(60, Middle(laneCut)), 3000);
        surface.MoveHeaderDrag(new Point(40, laneA.Top + 5), 3020);
        expect(Seam("A", above: true), "A's Sub-Lane dragged left and up into A's upper half: the seam over A, the top of the picture tracks");
        expect(surface.ReleaseHeaderDrag() && calls.SequenceEqual(["move A-cut above A"]), "let go: taken out of A's stack, over A");

        // 5.5A review: over its own family's rows (A's and its Sub-Lane's), but the outer seam bands at their top and bottom, a Sub-Lane's drag
        // is over nothing: put back where it began, nothing changes.
        calls.Clear();
        ended = 0;
        surface.PressHeaderAt(cutout, new Point(60, Middle(laneCut)), 3100);
        surface.MoveHeaderDrag(new Point(60, Middle(laneCut) - 12), 3120);
        expect(surface.IsDragging && surface.RowSeam is null && surface.NestTargetKey is null, "A's Sub-Lane dragged 12 DIPs up within its own lane: a drag, over nothing (no seam bar)");
        surface.MoveHeaderDrag(new Point(60, Middle(laneA)), 3140);
        expect(surface.RowSeam is null, "on over the middle of A's own row: still over nothing (before, the seam over A: let go there, out of its stack)");
        surface.MoveHeaderDrag(new Point(90, laneA.Top + 42), 3160);
        surface.SettleTick(3240);
        expect(surface.RowSeam is null && surface.NestTargetKey is null, "right onto A's lower half, settled: never nested under its own track, and no seam either");
        surface.MoveHeaderDrag(new Point(60, Middle(laneCut)), 3300);
        expect(surface.ReleaseHeaderDrag() && calls.Count == 0 && ended == 1 && !surface.IsDragging, "back on its own header and let go: nothing moved or nested, the drag's end told once");

        surface.PressHeaderAt(cutout, new Point(60, Middle(laneCut)), 3400);
        surface.MoveHeaderDrag(new Point(60, laneCut.Bottom - 4), 3420);
        expect(Seam("B", above: true), "into the outer band at the bottom of A's family: the seam under it (said as B's, above it)");
        ended = 0;
        expect(surface.EscapeRowDrag() && !surface.IsDragging && surface.RowSeam is null && ended == 1, "Esc: the drag let go of, its seam bar gone, the drag's end told");
        expect(!surface.ReleaseHeaderDrag() && calls.Count == 0, "the button let go after it: no header held, nothing moved");
        surface.PressHeaderAt(c, new Point(60, Middle(laneC)), 3500);
        expect(!surface.EscapeRowDrag() && surface.ReleaseHeaderDrag() && calls.Count == 0, "Esc on a header pressed but not dragged: nothing to cancel, its release a click");

        calls.Clear();
        ended = 0;
        surface.PressHeaderAt(c, new Point(60, Middle(laneC)), 4000);
        surface.MoveHeaderDrag(new Point(62, Middle(laneC) + 2), 4010);
        expect(!surface.IsDragging && surface.RowSeam is null, "a press that moves 2 DIPs: no drag (under the system's drag distance)");
        expect(surface.ReleaseHeaderDrag() && calls.Count == 0 && ended == 0 && !surface.IsDragging, "let go: a click as before, nothing raised");

        surface.PressHeaderAt(c, new Point(60, Middle(laneC)), 5000);
        Show(surface, captions, a, b, Row("C"), d);
        expect(!surface.MoveHeaderDrag(new Point(60, laneA.Top + 5), 5020) && !surface.IsDragging, "the rows made again while a header is held: the press is let go, nothing follows");
    }

    // ----- track-nest-reorder-attach-model -----

    private static void NestReorderAttachModel(Action<bool, string> expect, StringBuilder text)
    {
        // Track 1: the file opened (0-8 s). V2: a clip 0-4 s, with a Cutout lane under it (its Cutout a child of that clip). V3: a clip 2-6 s.
        var v2 = DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 4));
        var cut = DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 4));
        cut.ParentTrackId = v2.Id;
        cut.Clips[0].ParentClipId = v2.Clips[0].Id;
        var v3 = DogfoodTests.Lane(DogfoodTests.FileClip(Other, 2, 6));
        var vm = DogfoodTests.Open(v2, cut, v3);
        var t1 = vm.Document.Tracks[0];
        var names = new Dictionary<Guid, string> { [t1.Id] = "T1", [v2.Id] = "V2", [cut.Id] = "Cut", [v3.Id] = "V3" };
        var (mainId, v2ClipId, cutClipId, v3ClipId) = (t1.Clips[0].Id, v2.Clips[0].Id, cut.Clips[0].Id, v3.Clips[0].Id);
        TimelineTrackModel Track(Guid id) => vm.Document.Tracks.First(t => t.Id == id);
        TimelineClip Clip(Guid id) => vm.Document.FindClip(id)!;
        Layer View(Guid clip) => vm.ViewOf(Clip(clip))!;
        Layer RowOf(TimelineTrackModel track) => vm.ViewOf(Track(track.Id).Clips[0])!;
        string Order() => string.Join(" ", vm.Document.Tracks.Select(t => names.GetValueOrDefault(t.Id, "?") + (t.ParentTrackId is { } p ? "<" + names.GetValueOrDefault(p, "?") : "")));
        string Shape() => Order() + " | " + string.Join(" ", vm.Document.Tracks.Select(t => string.Join(",", t.Clips.Select(c => F($"{c.Start:0.##}-{c.End:0.##}{(c.ParentClipId is null ? "" : "*")}")))));
        var start = Shape();
        text.Append("   at the start: ").Append(start).Append('\n');

        expect(vm.CanDragTrack(RowOf(v2)) && vm.CanDragTrack(RowOf(cut)) && vm.CanDragTrack(RowOf(t1)), "a picture track's header may be dragged, a top-level one's and a Sub-Lane's");

        // Nested under Track 1: in front of it before, in front of it after; its family together; one step of Undo.
        expect(vm.CanNestTrack(RowOf(v3), RowOf(t1)) && vm.NestTrackUnder(RowOf(v3), RowOf(t1)), "V3 nested under Track 1");
        expect(Order() == "T1 V3<T1 V2 Cut<V2" && vm.Document.FindProblems().Count == 0, F($"V3 a Sub-Lane of Track 1, kept in front of it, just over it ({Order()}); the document holds together"));
        expect(!vm.CanNestTrack(RowOf(v3), RowOf(t1)) && !vm.CanNestTrack(RowOf(t1), RowOf(v3)), "not again under the same track, and Track 1 never under its own Sub-Lane");
        vm.Undo();
        expect(Shape() == start, "one Undo: the order and the parent as they were");

        // V3 under V2, then V2 moved below Track 1: its Cutout lane and its nested V3 with it, together.
        expect(vm.NestTrackUnder(RowOf(v3), RowOf(v2)) && Order() == "T1 V2 Cut<V2 V3<V2", F($"V3 nested under V2, over V2's family ({Order()})"));
        expect(!vm.CanMoveTrackToSeam(RowOf(v2), RowOf(t1), above: true), "V2 at the seam over Track 1, where it already is: nothing to do, refused");
        expect(vm.CanMoveTrackToSeam(RowOf(v2), RowOf(t1), above: false) && vm.MoveTrackToSeam(RowOf(v2), RowOf(t1), above: false), "V2 moved to the seam under Track 1");
        expect(Order() == "V2 Cut<V2 V3<V2 T1" && vm.Document.FindProblems().Count == 0, F($"V2 under Track 1 with its Cutout lane and its nested V3, together ({Order()})"));
        vm.Undo();
        expect(Order() == "T1 V2 Cut<V2 V3<V2", "one Undo: V2's family back over Track 1");

        // A clip attached to a clip on another track, moved in time as it is let go; then the parent's edits carry it. Pass 5.5A fix round 3:
        // V3 lies under V2, and its clip is still a tether, never one of V2's clip's pieces (the mark, not the lane, says which).
        var templateChildren = typeof(MainViewModel).GetMethod("TemplateChildrenOf", Hidden)!;
        List<TimelineClip> Pieces(Guid id) => ((List<(TimelineTrackModel Track, TimelineClip Clip)>)templateChildren.Invoke(vm, [Clip(id)])!).ConvertAll(c => c.Clip);
        expect(vm.CanAttachClip(View(v3ClipId), View(v2ClipId), 1) && vm.AttachClip(View(v3ClipId), View(v2ClipId), 1), "V3's clip (on a Sub-Lane of V2) attached to V2's clip, let go at 1 s");
        expect(Clip(v3ClipId) is { Start: 1, End: 5, Tethered: true } attached && attached.ParentClipId == v2ClipId && vm.ClipLock(View(v3ClipId)) == true
               && Pieces(v2ClipId).ConvertAll(c => c.Id).SequenceEqual([cutClipId]),
            "it is V2's clip's child at 1-5 s, marked tethered: none of its pieces (the Cutout alone is), its Link switch shown locked");
        var attachedShape = Shape();
        vm.Undo();
        expect(Clip(v3ClipId) is { Start: 2, End: 6, ParentClipId: null, Tethered: false }, "one Undo: back at 2-6 s, no parent, no mark");
        vm.Redo();
        expect(Shape() == attachedShape, "Redo attaches it again");
        var edit = typeof(MainViewModel).GetMethod("Edit", Hidden)!;
        edit.Invoke(vm, ["move V2's clip", (Action<ITimelineTransaction>)(e => e.MoveClip(Clip(v2ClipId), Track(v2.Id), 0.5, allowCrossfadeOverlap: false)), true, true]);
        expect(Clip(v2ClipId).Start == 0.5 && Clip(v3ClipId) is { Start: 1.5, End: 5.5 }, "the parent moved 0.5 s later: its child went with it (1.5-5.5 s)");
        edit.Invoke(vm, ["split V2's clip", (Action<ITimelineTransaction>)(e => e.SplitClipAt(Clip(v2ClipId), 3)), true, true]);
        expect(Track(v3.Id).Clips.Count == 2 && Track(v3.Id).Clips.All(k => k.ParentClipId is not null && k.Tethered), "the parent split at 3 s: its child split there too, each part a child of a half, both still tethered");
        vm.Undo();
        vm.Undo();
        vm.SetClipLocked(View(v3ClipId), false);
        expect(Clip(v3ClipId) is { ParentClipId: null, Tethered: false } && vm.ClipLock(View(v3ClipId)) != true, "its Link switch clicked: unlinked, its mark gone with the link");

        // The refusals, each changing nothing.
        var before = Shape();
        expect(!vm.AttachClip(View(v3ClipId), View(v3ClipId), 2), "a clip onto itself: refused");
        expect(!vm.AttachClip(View(v3ClipId), View(v2ClipId), 20), "where they do not overlap in time: refused");
        expect(!vm.AttachClip(View(cutClipId), View(mainId), 0), "a Cutout (already its clip's child): refused (its Link switch frees it first)");
        vm.AttachClip(View(v3ClipId), View(v2ClipId), 2);
        var linked = Shape();
        expect(!vm.AttachClip(View(v2ClipId), View(v3ClipId), 0) && Shape() == linked, "a parent onto its own child: refused (no loop)");
        vm.Undo();
        Track(v3.Id).IsLocked = true;
        expect(!vm.CanAttachClip(View(v3ClipId), View(v2ClipId), 2), "a clip on a locked track: refused");
        Track(v3.Id).IsLocked = false;
        expect(Shape() == before, "the refusals changed nothing");

        // A Sub-Lane taken out at a seam: its stack left as the menu's Unlink from Parent leaves it (its clips' links too): the Cutout lane, then
        // V3 with its clip tethered to V2's clip (Pass 5.5A fix round 3: the mark goes with the link, and Undo brings both back).
        expect(Order() == "T1 V2 Cut<V2 V3<V2" && Clip(cutClipId).ParentClipId == v2ClipId, "set up: the Cutout lane under V2, its Cutout V2's clip's child");
        expect(vm.CanMoveTrackToSeam(RowOf(cut), RowOf(t1), above: false) && vm.MoveTrackToSeam(RowOf(cut), RowOf(t1), above: false), "the Cutout lane's header let go at the seam under Track 1");
        expect(Order() == "Cut T1 V2 V3<V2" && Clip(cutClipId).ParentClipId is null && vm.Document.FindProblems().Count == 0,
            F($"the Cutout lane a track of its own under Track 1 ({Order()}), its clip's link to V2's clip gone with its stack"));
        vm.Undo();
        expect(Order() == "T1 V2 Cut<V2 V3<V2" && Clip(cutClipId).ParentClipId == v2ClipId, "one Undo: back in V2's stack, its clip linked again");
        expect(vm.AttachClip(View(v3ClipId), View(v2ClipId), 2) && vm.MoveTrackToSeam(RowOf(v3), RowOf(t1), above: true)
               && Order() == "T1 V3 V2 Cut<V2" && Clip(v3ClipId) is { ParentClipId: null, Tethered: false } && vm.Document.FindProblems().Count == 0,
            F($"V3's clip tethered to V2's clip, then V3's header let go at the seam over Track 1 ({Order()}): its tether and mark gone with its stack"));
        vm.Undo();
        expect(Order() == "T1 V2 Cut<V2 V3<V2" && Clip(v3ClipId) is { Tethered: true } back && back.ParentClipId == v2ClipId, "one Undo: back in V2's stack, tethered again");
    }

    // ----- track-tether-not-a-cutout (5.5A review) -----

    // The file DogfoodTests.Open opens (Track 1's clip), and three more.
    private const string Game = "D:/media/game.mkv", Webcam = "D:/media/webcam.mkv", Fourth = "D:/media/fourth.mkv", Song = "D:/media/song.wav";

    private static void TetherNotACutout(Action<bool, string> expect, StringBuilder text)
    {
        // Track 1: the file opened (0-8 s). V2: a webcam 0-6 s. V3: a clip of another file 8-12 s. V4: a fourth file 12-16 s. V5: the file
        // opened again, 0-4 s. An audio track: music 6-20 s.
        var (cam, x, y, same) = (DogfoodTests.Lane(DogfoodTests.FileClip(Webcam, 0, 6)), DogfoodTests.Lane(DogfoodTests.FileClip(Other, 8, 12)),
            DogfoodTests.Lane(DogfoodTests.FileClip(Fourth, 12, 16)), DogfoodTests.Lane(DogfoodTests.FileClip(Game, 0, 4)));
        var music = new TimelineTrackModel { Kind = TimelineTrackKind.Audio };
        music.Clips.Add(new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = Song, AudioStreamIndex = 0, Start = 6, End = 20, SourceDuration = 60 });
        var vm = DogfoodTests.Open(cam, x, y, same, music);
        var (mainId, camId, xId, yId, sameId, songId) = (vm.Document.Tracks[0].Clips[0].Id, cam.Clips[0].Id, x.Clips[0].Id, y.Clips[0].Id, same.Clips[0].Id, music.Clips[0].Id);
        TimelineClip Clip(Guid id) => vm.Document.FindClip(id)!;
        Layer View(Guid id) => vm.ViewOf(Clip(id))!;
        var templateChildren = typeof(MainViewModel).GetMethod("TemplateChildrenOf", Hidden)!;
        List<TimelineClip> Pieces(Guid id) => ((List<(TimelineTrackModel Track, TimelineClip Clip)>)templateChildren.Invoke(vm, [Clip(id)])!).ConvertAll(c => c.Clip);
        List<TimelineClip> Pictures(Guid id) => vm.Document.ChildrenOf(Clip(id)).Where(c => c.Track.Kind == TimelineTrackKind.Video).Select(c => c.Clip).ToList();
        int Webcams() => vm.Document.Tracks.Sum(t => t.Clips.Count(c => c.SourcePath == Webcam));
        bool StillTethered() => vm.Document.FindClip(camId) is { } still && still.ParentClipId == mainId && Webcams() == 1;

        expect(Clip(mainId).SourcePath == Game && vm.AttachClip(View(camId), View(mainId), 0) && Clip(camId).ParentClipId == mainId, "the webcam on V2 tethered to the opened file's clip on Track 1");
        expect(Pictures(mainId).Exists(c => c.Id == camId) && Clip(camId).Tethered && Pieces(mainId).Count == 0, "it is that clip's child, marked tethered: none of its template's pieces");

        expect(vm.MoveClipToTrack(View(xId), View(mainId), 8) && ReferenceEquals(vm.Document.TrackOf(Clip(xId)), vm.Document.TrackOf(Clip(mainId))), "V3's clip moved onto Track 1 after it");
        expect(Pictures(xId).Count == 0 && Webcams() == 1, "it takes no copy of the webcam: the clip on Track 1 has no template to give");

        var name = Preset(vm);
        expect(vm.ApplyGamePreset([View(mainId)], name) && Pieces(mainId).Count == 1 && StillTethered(), "a Game Preset on the opened file's clip: its one Cutout on a Sub-Lane of Track 1, the webcam still there, still its child");
        expect(vm.ApplyGamePreset([View(mainId)], name) && Pieces(mainId).Count == 1 && StillTethered(), "applied again it takes the place of the first Cutout only: the webcam stays (the replace takes the clip's pieces alone)");
        expect(vm.MoveClipToTrack(View(yId), View(mainId), 12) && Pictures(yId) is { Count: 1 } taken && taken.TrueForAll(c => c.SourcePath == Fourth) && Webcams() == 1,
            "V4's clip moved onto Track 1 takes the template now: one Cutout of its own file, no copy of the webcam");
        expect(vm.GamePresetTargets([View(camId)]) is [var only] && only.ClipId == camId, "the webcam selected: a Game Preset goes on it, never on the clip it hangs from");

        expect(!vm.CanAttachClip(View(sameId), View(mainId), 0) && !vm.AttachClip(View(sameId), View(mainId), 0) && Clip(sameId).ParentClipId is null
               && vm.StatusText.Contains("own file", StringComparison.Ordinal),
            "the opened file again (V5, 0-4 s) let go on the opened file's clip: never tethered (it would be one of its Cutouts), the status bar says why");

        var length = SequenceSpans.ProgramSeconds(vm.Document);
        expect(!vm.CanAttachClip(View(songId), View(mainId), 6) && !vm.AttachClip(View(songId), View(mainId), 6) && Clip(songId).ParentClipId is null
               && vm.StatusText.Contains("audio track", StringComparison.Ordinal) && length == 20 && SequenceSpans.ProgramSeconds(vm.Document) == length,
            F($"the music (6-20 s, on an audio track) let go on the opened file's clip it overlaps: never tethered, the status bar says why, the program still {length:0.##} s long"));
        expect(!vm.CanAttachClip(View(xId), View(songId), 8), "nor V3's clip (8-12 s) tethered to the music it overlaps");
        expect(vm.Document.FindProblems().Count == 0, "the document holds together");
    }

    // ----- track-nest-tether-pieces (Pass 5.5A fix round 3: a tether is a tether wherever it lies) -----

    private const string Fifth = "D:/media/fifth.mkv";

    private static void NestTetherPieces(Action<bool, string> expect, StringBuilder text)
    {
        // Track 1: the file opened (0-8 s), then one clip on a track of its own for each clip given. Opened afresh for each route.
        MainViewModel vm = null!;
        Guid[] Open(params TimelineClip[] clips)
        {
            vm = DogfoodTests.Open([.. clips.Select(c => DogfoodTests.Lane(c))]);
            return [vm.Document.Tracks[0].Clips[0].Id, .. clips.Select(c => c.Id)];
        }

        TimelineClip Clip(Guid id) => vm.Document.FindClip(id)!;
        Layer View(Guid id) => vm.ViewOf(Clip(id))!;
        TimelineTrackModel TrackOf(Guid id) => vm.Document.TrackOf(Clip(id))!;
        Layer Row(Guid id) => vm.ViewOf(TrackOf(id).Clips[0])!;
        var templateChildren = typeof(MainViewModel).GetMethod("TemplateChildrenOf", Hidden)!;
        List<TimelineClip> Pieces(Guid id) => ((List<(TimelineTrackModel Track, TimelineClip Clip)>)templateChildren.Invoke(vm, [Clip(id)])!).ConvertAll(c => c.Clip);
        List<TimelineClip> Pictures(Guid id) => vm.Document.ChildrenOf(Clip(id)).Where(c => c.Track.Kind == TimelineTrackKind.Video).Select(c => c.Clip).ToList();
        int Webcams() => vm.Document.Tracks.Sum(t => t.Clips.Count(c => c.SourcePath == Webcam));
        bool TetheredTo(Guid cam, Guid parent) => Clip(cam) is { Tethered: true } kid && kid.ParentClipId == parent && !Pieces(parent).Exists(c => c.Id == cam);
        string Shape() => string.Join(" | ", vm.Document.Tracks.Where(t => t.Clips.Count > 0).Select(t => t.DisplayName
            + (t.ParentTrackId is { } p ? "<" + vm.Document.Tracks.First(u => u.Id == p).DisplayName : "") + ": "
            + string.Join(",", t.Clips.Select(c => System.IO.Path.GetFileNameWithoutExtension(c.SourcePath) + (c.ParentClipId is null ? "" : c.Tethered ? "~" : "*")))));

        // Every route ends the same way: a Game Preset on the webcam's parent, then again (the second a replace), leaves the webcam there,
        // still tethered and none of its pieces; a clip moved onto the parent's track takes the parent's template with no copy of the webcam.
        void Survives(string route, Guid parent, Guid cam, Guid moved, double at)
        {
            text.Append("   ").Append(route).Append(": ").Append(Shape()).Append('\n');
            var name = Preset(vm);
            expect(vm.ApplyGamePreset([View(parent)], name) && vm.ApplyGamePreset([View(parent)], name) && Pieces(parent).Count == 1 && TetheredTo(cam, parent) && Webcams() == 1,
                $"{route}: a Game Preset on the parent, then again (a replace): its one Cutout, the webcam still there, still tethered to it and none of its pieces");
            expect(vm.MoveClipToTrack(View(moved), Row(parent), at) && Pictures(moved) is { Count: 1 } taken && taken.TrueForAll(c => c.SourcePath == Fourth) && Webcams() == 1
                   && vm.Document.FindProblems().Count == 0,
                $"{route}: a clip moved onto the parent's track takes its template, one Cutout of its own file and no copy of the webcam; the document holds together");
        }

        // 1. The tether first, then its track nested under the parent's own track (made: no nest is refused for where a tether would lie).
        var ids = Open(DogfoodTests.FileClip(Webcam, 0, 6), DogfoodTests.FileClip(Fourth, 12, 16));
        var (main, cam, y) = (ids[0], ids[1], ids[2]);
        expect(vm.AttachClip(View(cam), View(main), 0) && TetheredTo(cam, main), "1. the webcam on V2 tethered to the opened file's clip on Track 1: marked tethered, none of its pieces");
        expect(vm.CanNestTrack(Row(cam), Row(main)) && vm.NestTrackUnder(Row(cam), Row(main)) && TrackOf(cam).ParentTrackId == TrackOf(main).Id && TetheredTo(cam, main),
            "1. then V2 nested under Track 1: made, the webcam on a lane under its parent's own track and still a tether");
        Survives("1", main, cam, y, 12);

        // 2. The nest first, then the tether (made: a tether from a lane under the parent's track is still a tether).
        ids = Open(DogfoodTests.FileClip(Webcam, 0, 6), DogfoodTests.FileClip(Fourth, 12, 16));
        (main, cam, y) = (ids[0], ids[1], ids[2]);
        expect(vm.NestTrackUnder(Row(cam), Row(main)) && vm.CanAttachClip(View(cam), View(main), 0) && vm.AttachClip(View(cam), View(main), 0)
               && TrackOf(cam).ParentTrackId == TrackOf(main).Id && TetheredTo(cam, main),
            "2. V2 nested under Track 1, then its webcam tethered to the opened file's clip: made, marked tethered, none of its pieces");
        Survives("2", main, cam, y, 12);

        // 3. The tethered clip's block dragged onto a lane under its parent's track (the review's T2-1).
        ids = Open(DogfoodTests.FileClip(Webcam, 0, 6), DogfoodTests.FileClip(Other, 12, 16), DogfoodTests.FileClip(Fourth, 16, 20));
        (main, cam, y) = (ids[0], ids[1], ids[3]);
        var x = ids[2];
        expect(vm.AttachClip(View(cam), View(main), 0) && vm.NestTrackUnder(Row(x), Row(main)), "3. the webcam tethered to the opened file's clip; V3 (another file, 12-16 s) nested under Track 1");
        expect(vm.MoveClipToTrack(View(cam), Row(x), 0) && ReferenceEquals(TrackOf(cam), TrackOf(x)) && TetheredTo(cam, main),
            "3. the webcam's block dragged onto V3 at 0 s, a lane under its parent's track: still tethered, none of its pieces");
        Survives("3", main, cam, y, 16);

        // 4. The parent dragged onto the track the tether's lane lies under, a Track Preset Node there (the review's T2-2a).
        ids = Open(DogfoodTests.FileClip(Second, 10, 14), DogfoodTests.FileClip(Webcam, 10, 14), DogfoodTests.FileClip(Fourth, 16, 20));
        var b = ids[1];
        (main, cam, y) = (ids[0], ids[2], ids[3]);
        expect(vm.AttachClip(View(cam), View(b), 10) && vm.NestTrackUnder(Row(cam), Row(main)) && TrackOf(cam).ParentTrackId == TrackOf(main).Id && TetheredTo(cam, b),
            "4. the webcam on V3 tethered to B (another file on V2, 10-14 s); V3 nested under Track 1");
        expect(vm.SetTrackPreset(Row(main), Preset(vm)) && vm.MoveClipToTrack(View(b), Row(main), 10) && ReferenceEquals(TrackOf(b), TrackOf(main))
               && Pieces(b).Count == 1 && TetheredTo(cam, b) && Clip(cam).Start == 10 && Webcams() == 1,
            "4. Track 1 given a Track Preset Node, then B dragged onto it at 10 s: B takes the node's preset (one Cutout) in that step, the webcam still tethered to it");
        Survives("4", b, cam, y, 16);

        // 5. The tether's lane follows its clips' parents to another track (FollowTrack: the review's T2-2b).
        ids = Open(DogfoodTests.FileClip(Second, 10, 14), DogfoodTests.FileClip(Webcam, 0, 6), DogfoodTests.FileClip(Fourth, 16, 20));
        (main, b, cam, y) = (ids[0], ids[1], ids[2], ids[3]);
        expect(vm.NestTrackUnder(Row(cam), Row(b)) && vm.AttachClip(View(cam), View(main), 0) && TrackOf(cam).ParentTrackId == TrackOf(b).Id && TetheredTo(cam, main),
            "5. V3 nested under V2 (B, another file, 10-14 s), its webcam tethered to the opened file's clip on Track 1");
        expect(vm.MoveClipToTrack(View(b), Row(main), 10) && TrackOf(cam).ParentTrackId == TrackOf(main).Id && TetheredTo(cam, main),
            "5. B dragged onto Track 1: V3 follows its clips' parent under Track 1, the webcam still tethered, none of the clip's pieces");
        Survives("5", main, cam, y, 16);

        // 6. A Cutout dragged off its lane onto a top-level track is still its clip's piece (the review's T2-1, the other way round).
        ids = Open(DogfoodTests.FileClip(Webcam, 0, 6), DogfoodTests.FileClip(Other, 12, 16));
        (main, cam, x) = (ids[0], ids[1], ids[2]);
        var preset = Preset(vm);
        expect(vm.ApplyGamePreset([View(main)], preset) && Pieces(main).Count == 1, "6. a Game Preset on the opened file's clip: its one Cutout, on a lane under Track 1");
        var cut = Pieces(main)[0].Id;
        expect(vm.MoveClipToTrack(View(cut), Row(x), 0) && TrackOf(cut).ParentTrackId is null && Clip(cut) is { Tethered: false } moved && moved.ParentClipId == main
               && Pieces(main).ConvertAll(c => c.Id).SequenceEqual([cut]) && vm.GamePresetTargets([View(cut)]) is [var target] && target.ClipId == main,
            "6. the Cutout's block dragged onto V3 (a top-level track) at 0 s: still the clip's piece, a preset chosen with it selected goes on its clip");
        expect(vm.ApplyGamePreset([View(main)], preset) && vm.Document.FindClip(cut) is null && Pieces(main).Count == 1 && vm.Document.FindProblems().Count == 0,
            "6. the preset again: that Cutout replaced like any piece (none left behind, one Cutout in all)");

        // 7. The Link switch: a clip on a track nested under Track 1, locked to the clip under it, is one of its pieces (as before 5.5A).
        expect(vm.NestTrackUnder(Row(cam), Row(main)) && vm.ClipLock(View(cam)) == false, "7. V2 nested under Track 1: the webcam's Link switch shown unlocked (there is a clip to lock it to)");
        vm.SetClipLocked(View(cam), true);
        expect(Clip(cam) is { Tethered: false } locked && locked.ParentClipId == main && Pieces(main).Exists(c => c.Id == cam),
            "7. its Link switch locked: the opened file's clip's child and one of its pieces, not tethered");

        // 8. Pass 5.5A review (Relane): a lone clip with its Cutout lane over its track, dragged down onto a lower track, keeps that lane
        // just over its new track (it was moved over, then taken for a lane under and put below the track, its HUD under the gameplay).
        ids = Open(DogfoodTests.FileClip(Second, 10, 14));
        (main, b) = (ids[0], ids[1]);
        expect(vm.ApplyGamePreset([View(b)], Preset(vm)) && Pieces(b) is [var hud] && vm.Document.TrackOf(hud) is { } lane
               && vm.Document.Tracks.IndexOf(lane) > vm.Document.Tracks.IndexOf(TrackOf(b)),
            "8. a Game Preset on B, alone on V2: its Cutout on a lane over V2");
        var hudId = Pieces(b)[0].Id;
        expect(vm.MoveClipToTrack(View(b), Row(main), 10) && ReferenceEquals(TrackOf(b), TrackOf(main)) && TrackOf(hudId) is { } over
               && over.ParentTrackId == TrackOf(main).Id && vm.Document.Tracks.IndexOf(over) == vm.Document.Tracks.IndexOf(TrackOf(main)) + 1
               && vm.Document.FindProblems().Count == 0,
            "8. B dragged down onto Track 1 at 10 s: its Cutout lane follows it, just over Track 1 (never under it)");
        text.Append("   8: ").Append(Shape()).Append('\n');
    }

    // ----- track-tether-mark-model (Pass 5.5A fix round 3) -----

    private static void TetherMarkModel(Action<bool, string> expect, StringBuilder text)
    {
        // Track 1: the file opened (0-8 s). V2: a webcam 0-6 s. V3: another file 0-4 s. V4: a fourth file 12-16 s. V5: a fifth file 20-24 s.
        var (camLane, xLane, yLane, zLane) = (DogfoodTests.Lane(DogfoodTests.FileClip(Webcam, 0, 6)), DogfoodTests.Lane(DogfoodTests.FileClip(Other, 0, 4)),
            DogfoodTests.Lane(DogfoodTests.FileClip(Fourth, 12, 16)), DogfoodTests.Lane(DogfoodTests.FileClip(Fifth, 20, 24)));
        var vm = DogfoodTests.Open(camLane, xLane, yLane, zLane);
        var (main, cam, x, y, z) = (vm.Document.Tracks[0].Clips[0].Id, camLane.Clips[0].Id, xLane.Clips[0].Id, yLane.Clips[0].Id, zLane.Clips[0].Id);
        TimelineClip Clip(Guid id) => vm.Document.FindClip(id)!;
        Layer View(Guid id) => vm.ViewOf(Clip(id))!;
        Layer Row(Guid id) => vm.ViewOf(vm.Document.TrackOf(Clip(id))!.Clips[0])!;
        var templateChildren = typeof(MainViewModel).GetMethod("TemplateChildrenOf", Hidden)!;
        List<TimelineClip> Pieces(Guid id) => ((List<(TimelineTrackModel Track, TimelineClip Clip)>)templateChildren.Invoke(vm, [Clip(id)])!).ConvertAll(c => c.Clip);
        var edit = typeof(MainViewModel).GetMethod("Edit", Hidden)!;
        void Edit(string label, Action<ITimelineTransaction> change) => edit.Invoke(vm, [label, change, true, true]);
        List<TimelineClip> Webcams() => [.. vm.Document.Tracks.SelectMany(t => t.Clips).Where(c => c.SourcePath == Webcam).OrderBy(c => c.Start)];

        // Undo and Redo.
        expect(vm.AttachClip(View(cam), View(main), 0) && Clip(cam) is { Tethered: true }, "the webcam tethered to the opened file's clip: marked");
        vm.Undo();
        expect(Clip(cam) is { ParentClipId: null, Tethered: false }, "Undo: no link, no mark");
        vm.Redo();
        expect(Clip(cam) is { Tethered: true } redone && redone.ParentClipId == main, "Redo: linked and marked again");

        // A split: both halves keep it, the webcam's own and the parent's.
        Edit("split the webcam", e => e.SplitClipAt(Clip(cam), 3));
        expect(Webcams() is [{ Tethered: true } first, { Tethered: true } second] && first.ParentClipId == main && second.ParentClipId == main, "the webcam split at 3 s: both halves tethered to the clip");
        vm.Undo();
        Edit("split the clip", e => e.SplitClipAt(Clip(main), 3));
        expect(Webcams() is [{ Tethered: true } head, { Tethered: true } tail] && head.ParentClipId == main && tail.ParentClipId != main && Clip(tail.ParentClipId!.Value).Start == 3,
            "the opened file's clip split at 3 s: the webcam split with it, each half tethered to a half of it");
        vm.Undo();

        // Copy and paste: the clip with its family, the webcam alone (inside its parent: kept; outside: let go, the mark with the link).
        expect(vm.CopyClips([View(main)]) == 1 && vm.PasteClips(30), "the opened file's clip copied with its family, pasted at 30 s");
        var pasted = vm.Document.Tracks.SelectMany(t => t.Clips).Single(c => c.SourcePath == Clip(main).SourcePath && c.Start == 30);
        expect(Webcams() is [_, { Tethered: true } copy] && copy.ParentClipId == pasted.Id && vm.Document.TrackOf(copy)!.ParentTrackId == vm.Document.TrackOf(pasted)!.Id
               && Pieces(pasted.Id).Count == 0,
            "its webcam pasted with it on a lane under its track, tethered to the pasted clip and none of its pieces");
        vm.Undo();
        expect(vm.CopyClips([View(cam)]) == 1 && vm.PasteClips(1) && Webcams() is [_, { Start: 1, Tethered: true } inside] && inside.ParentClipId == main,
            "the webcam alone pasted at 1 s, inside its parent: still tethered to it");
        vm.Undo();
        expect(vm.PasteClips(40) && Webcams() is [_, { Start: 40, ParentClipId: null, Tethered: false }], "pasted at 40 s, outside it: its link and its mark let go together");
        vm.Undo();

        // The Link switch: unlocking lets go of both; Undo brings both back.
        vm.SetClipLocked(View(cam), false);
        expect(Clip(cam) is { ParentClipId: null, Tethered: false }, "the webcam's Link switch unlocked: no link, no mark");
        vm.Undo();
        expect(Clip(cam) is { Tethered: true } relinked && relinked.ParentClipId == main, "Undo: linked and marked again");

        // A Game Preset's Cutout dragged off its lane onto a top-level track (V4): still a piece. Then V3 nested under Track 1, its clip locked
        // to the clip under it by its Link switch: a piece, as before 5.5A.
        var preset = Preset(vm);
        expect(vm.ApplyGamePreset([View(main)], preset) && Pieces(main).Count == 1 && Clip(cam).Tethered, "a Game Preset on the clip: its one Cutout, the webcam left as it was");
        var cut = Pieces(main)[0].Id;
        expect(vm.MoveClipToTrack(View(cut), Row(y), 0) && vm.Document.TrackOf(Clip(cut))!.ParentTrackId is null && Pieces(main).ConvertAll(c => c.Id).SequenceEqual([cut]),
            "its Cutout dragged onto V4 (a top-level track) at 0 s: still the clip's piece");
        expect(vm.NestTrackUnder(Row(x), Row(main)) && vm.ClipLock(View(x)) == false, "V3 nested under Track 1");
        vm.SetClipLocked(View(x), true);
        expect(Clip(x) is { Tethered: false } locked && locked.ParentClipId == main && Pieces(main).Exists(c => c.Id == x), "V3's clip locked by its Link switch: one of the clip's pieces, not tethered");

        // Saved and read: the marks as they were. The file says "tethered" on the webcam alone, and no tethersMarked (no piece lies where a
        // tether would: V3's clip lies under its parent's track, and the Cutout is a Cutout).
        var json = ProjectStore.Serialize(vm.CaptureState("Tethers"));
        var read = ProjectStore.Deserialize(json).Timeline;
        expect(read.FindClip(cam) is { Tethered: true } && read.FindClip(x) is { Tethered: false, ParentClipId: not null } && read.FindClip(cut) is { Tethered: false, ParentClipId: not null }
               && Marks(json, "tethered") == 1 && Marks(json, "tethersMarked") == 0,
            "saved and read: the webcam still tethered, V3's clip and the moved Cutout still pieces; \"tethered\" written once, no \"tethersMarked\"");

        // Written before the mark (a 5.4.4 project, a 5.5A preview's): the webcam's tether found as it is read, the pieces left pieces.
        var older = ProjectStore.Deserialize(Without(json, "tethered", "tethersMarked")).Timeline;
        expect(older.FindClip(cam) is { Tethered: true } && older.FindClip(x) is { Tethered: false } && older.FindClip(cut) is { Tethered: false } && older.FindProblems().Count == 0,
            "a project written before the mark: the webcam (a picture child off its parent's track) read as tethered; V3's clip (under its parent's track) and the Cutout (of its parent's own file) read as pieces");
        expect(ProjectStore.Serialize(ProjectStore.Deserialize(Without(json, "tethered"))) == ProjectStore.Serialize(ProjectStore.Deserialize(json)),
            "read and written again, it is the file this version writes");
        var step = System.Text.Json.JsonSerializer.Serialize(new TimelineSnapshot(vm.Document.Clone(), []));
        expect(System.Text.Json.JsonSerializer.Deserialize<TimelineSnapshot>(Without(step, "Tethered", "TethersMarked"))!.Document.FindClip(cam) is { Tethered: true },
            "an undo step written before the mark (a project's history): its tether found the same way as it is read");

        // A piece lying where a tether would (V3's locked clip dragged onto V5, a top-level track): the file says so, and is read as written.
        expect(vm.MoveClipToTrack(View(x), Row(z), 0) && vm.Document.TrackOf(Clip(x))!.ParentTrackId is null && Clip(x) is { Tethered: false } && Pieces(main).Exists(c => c.Id == x),
            "V3's locked clip dragged onto V5 (a top-level track) at 0 s: still a piece");
        json = ProjectStore.Serialize(vm.CaptureState("Tethers"));
        read = ProjectStore.Deserialize(json).Timeline;
        expect(Marks(json, "tethersMarked") == 1 && read.FindClip(x) is { Tethered: false } && read.FindClip(cam) is { Tethered: true },
            "saved and read: the file carries \"tethersMarked\", so that piece is read as a piece and the webcam as a tether");
        expect(ProjectStore.Deserialize(Without(json, "tethersMarked")).Timeline.FindClip(x) is { Tethered: true },
            "without it (as a file before the mark is read), that piece would be taken for a tether: why it is written");

        // 5.5.0 (the user's Q6): the webcam and V5's piece deleted into the recycle bin (each as the bin keeps one: the clip, its track's id,
        // kind, place and the track it lay under), then the webcam folded away: the marks go with them, and an older file's are found.
        (TimelineClip Clip, TimelineTrackModel Track) Take(TimelineDocument document, Guid id)
        {
            var clip = document.FindClip(id)!;
            var track = document.TrackOf(clip)!;
            track.Clips.Remove(clip);
            return (clip, track);
        }

        DeletedClip Binned(TimelineDocument document, Guid id) =>
            Take(document, id) is var (clip, track) ? new DeletedClip(clip, track.Id, track.Kind, document.Tracks.IndexOf(track)) { TrackParentId = track.ParentTrackId } : null!;

        var binned = vm.CaptureState("Bin");
        binned.Deleted = [Binned(binned.Timeline, cam), Binned(binned.Timeline, x)];
        json = ProjectStore.Serialize(binned);
        var bin = ProjectStore.Deserialize(json).Deleted;
        expect(bin.Find(d => d.Clip.Id == cam)?.Clip is { Tethered: true, ParentClipId: not null } && bin.Find(d => d.Clip.Id == x)?.Clip is { Tethered: false, ParentClipId: not null }
               && Marks(json, "binTethersMarked") == 1,
            "in the recycle bin, saved and read: the webcam still tethered, V5's piece still a piece (the file says \"binTethersMarked\": a piece lies where a tether would)");
        bin = ProjectStore.Deserialize(Without(json, "tethered", "binTethersMarked")).Deleted;
        expect(bin.Find(d => d.Clip.Id == cam)?.Clip is { Tethered: true },
            "a bin written before the mark: the webcam's tether found as it is read (its track, or the track it lay under, off its parent's track)");
        var step2 = System.Text.Json.JsonSerializer.Serialize(new TimelineSnapshot(binned.Timeline, [], Bin: [.. binned.Deleted.Where(d => d.Clip.Id == cam)]));
        expect(System.Text.Json.JsonSerializer.Deserialize<TimelineSnapshot>(step2)!.Bin is [{ Clip.Tethered: true }]
               && System.Text.Json.JsonSerializer.Deserialize<TimelineSnapshot>(Without(step2, "Tethered", "TethersMarked", "BinTethersMarked"))!.Bin is [{ Clip.Tethered: true }],
            "an undo step's bin: the mark kept, and found as an older step is read");

        var folded = vm.CaptureState("Fold");
        var (foldedCam, camTrack) = Take(folded.Timeline, cam);
        folded.Timeline.Folds = [new FoldedSegment(Guid.NewGuid(), main, false, false, 1, [new FoldedClip(camTrack.Id, camTrack.Kind, foldedCam)])];
        json = ProjectStore.Serialize(folded);
        expect(ProjectStore.Deserialize(json).Timeline.Folds is [{ Clips: [{ Clip.Tethered: true }] }]
               && ProjectStore.Deserialize(Without(json, "tethered", "tethersMarked")).Timeline.Folds is [{ Clips: [{ Clip.Tethered: true }] }],
            "folded away (a seam's stretch): the webcam still tethered, saved and read, and found as an older file is read");

        // The model holds a mark only with a link.
        var broken = vm.Document.Clone();
        broken.FindClip(y)!.Tethered = true;
        expect(broken.FindProblems() is [var problem] && problem.Contains("tethered", StringComparison.Ordinal) && vm.Document.FindProblems().Count == 0,
            F($"a clip marked tethered with no link is a problem (\"{(broken.FindProblems() is [var said] ? said : "")}\"); the document itself holds together"));
    }

    // How many properties of that name a JSON text holds set to true, at any depth.
    private static int Marks(string json, string name)
    {
        static int Count(System.Text.Json.Nodes.JsonNode? node, string name) => node switch
        {
            System.Text.Json.Nodes.JsonObject o => o.Sum(p => (p.Key == name && p.Value?.GetValueKind() == System.Text.Json.JsonValueKind.True ? 1 : 0) + Count(p.Value, name)),
            System.Text.Json.Nodes.JsonArray a => a.Sum(v => Count(v, name)),
            _ => 0,
        };

        return Count(System.Text.Json.Nodes.JsonNode.Parse(json), name);
    }

    // The same JSON text with the properties of those names taken out at any depth: a text written before them.
    private static string Without(string json, params string[] names)
    {
        static void Strip(System.Text.Json.Nodes.JsonNode? node, string[] names)
        {
            if (node is System.Text.Json.Nodes.JsonObject o)
            {
                foreach (var name in names)
                    o.Remove(name);
                foreach (var (_, value) in o.ToList())
                    Strip(value, names);
            }
            else if (node is System.Text.Json.Nodes.JsonArray a)
            {
                foreach (var value in a)
                    Strip(value, names);
            }
        }

        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        Strip(root, names);
        return root.ToJsonString();
    }

    // ----- timeline-drag-scroll-keys (Pass 5.5A final review) -----

    private static void ScrollKeys(Action<bool, string> expect, StringBuilder text)
    {
        // No key can be pressed without a window, so the source says it: a drag placed again while the view moves under a still pointer takes
        // Keyboard.Modifiers right before it is placed (Shift pressed or let go mid-scroll, a group's move too), and the seams given keys keep them.
        var hover = Read("Controls", "UnifiedTimelineControl.HoverAndWheel.cs");
        var (tick, held) = (Method(hover, "private void AutoScroll_Tick("), Method(hover, "private void FollowHeldDrag()"));
        expect(tick.Length > 0 && InOrder(tick, "PanRequested?.Invoke(pixels);", "drag.Keys = Keyboard.Modifiers;\n        Follow(drag, _lastPointer, pointerMoved: false, Now());"),
            "the auto-scroll's tick: the view moved, then the keys as they are now, then the drag placed again");
        expect(held.Length > 0 && InOrder(held, "_lastPointer = laid;", "drag.Keys = Keyboard.Modifiers;\n            Follow(drag, laid, pointerMoved: false, Now());"),
            "a wheel scroll or zoom under a still drag (FollowHeldDrag): the keys as they are now, then the drag placed again");
        var move = Method(hover, "protected override void OnMouseMove(");
        expect(InOrder(move, "drag.Keys = Keyboard.Modifiers;", "FollowPointer(drag, point, laid, Now())"), "a move of the pointer: the keys as they are now, as before");
        var rest = Method(Read("Controls", "UnifiedTimelineControl.Parenting.cs"), "internal double? RestTick(");
        expect(rest.Contains("drag.Keys = keys;", StringComparison.Ordinal) && !rest.Contains("Keyboard.Modifiers", StringComparison.Ordinal),
            "the rest tick (a test seam given its keys) keeps the keys it is given");
    }

    // ----- Source text -----

    private static string Read(params string[] parts) => System.IO.File.ReadAllText(System.IO.Path.Combine([RepositoryRoot(), "src", .. parts])).ReplaceLineEndings("\n");

    private static string RepositoryRoot()
    {
        for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "HandPegApp.csproj")))
                return dir.FullName;
        }

        throw new System.IO.DirectoryNotFoundException("the repository (HandPegApp.csproj) above " + AppContext.BaseDirectory);
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

    private static string Preset(MainViewModel vm)
    {
        const string name = "Parenting Preset";
        CutoutPiece[] pieces = [new("Hud", "0.02,0.8 0.2,0.8 0.2,0.95 0.02,0.95", new Rect(0.02, 0.8, 0.18, 0.15), 0.05, 0.75, 0.5)];
        if (vm.SaveCutoutPreset(name, pieces, 1.7) is { } why)
            throw new InvalidOperationException(why);
        return name;
    }
}
