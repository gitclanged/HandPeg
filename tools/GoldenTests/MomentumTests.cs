using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4C (Subagent B): the shared MomentumScroller and its coasting on the Timeline, the Subtitle Studio's strip and the Media Browser.
/// momentum-scroller-physics: the pure engine with its own clock: the release velocity over the last 60 ms, the 250 DIP/s threshold
/// and the 40 ms rest exactly at their edges, the closed-form coast the same at 500 Hz and 60 Hz, a stalled frame advancing 50 ms at
/// most, the stop below 20 DIP/s, touch-to-stop, the wheel's rules (a fresh stream, the absorb, a tick against the coast), momentum
/// switched off. momentum-timeline: a real UnifiedTimelineControl and TimelineRuler with a host stub moving the view: Shift + wheel,
/// the sideways wheel and a middle-button flick coast on and end, a slow drag and a rested release do not, a press, Ctrl + wheel, a zoom
/// and the end of the sequence stop it, the plain wheel coasts the rows to their ends; the sideways-wheel hook kept out of the frame
/// hooks. momentum-subtitle-scrub: a real SubtitleTimeline: a flick of the strip scrubs on, a cue's drag never coasts, the start of the
/// strip and a position set from outside stop it. momentum-media-browser: ScrollMomentum on a bare ScrollViewer and a ListBox: a notch is
/// 16 DIPs a wheel line, a fast stream coasts, a press and both ends stop it, nothing made until the first wheel. momentum-setting: the
/// setting's default, its JSON, its two homes.
/// </summary>
public static class MomentumTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("momentum-scroller-physics", "# The momentum engine with its own clock: a flick coasts on as v0 * d^t, the same at any frame rate; a slow or rested release, a press, a tick against the coast, or momentum off never coast.", Physics);
        yield return Case("momentum-timeline", "# The real Timeline and its ruler: Shift + wheel, the sideways wheel and a middle-button flick coast the view on and stop at its end; the plain wheel coasts the rows; a press, Ctrl + wheel or a zoom stops it.", Timeline);
        yield return Case("momentum-subtitle-scrub", "# The Subtitle Studio's strip: a flick scrubs on and slows to a stop; a cue's drag never coasts; the strip's start and a position set from outside stop it.", SubtitleScrub);
        yield return Case("momentum-media-browser", "# ScrollMomentum on a list: a notch moves its usual step, a fast wheel stream coasts on, a press and both ends stop it, and nothing is made until the first wheel.", MediaBrowser);
        yield return Case("momentum-setting", "# Enable Momentum Scrolling: on in a new install, kept in the settings file, in Settings > Timeline & Player and once in the Timeline Settings menu.", Setting);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>> run)
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
            run(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // A drag of `speed` DIP/s for `ms` milliseconds from `from` ms, sampled every 5 ms (a 200 Hz mouse), starting at position 0.
    private static void Drag(MomentumScroller scroller, double from, double ms, double speed)
    {
        scroller.Press(from);
        for (var t = 0.0; t <= ms + 1e-9; t += 5)
            scroller.Sample(speed * t / 1000, from + t);
    }

    // Ticks a coast every `step` ms from `now` until its own time reaches `until` ms; the ground covered.
    private static double TickUntil(MomentumScroller scroller, ref double now, double step, double until)
    {
        var covered = 0.0;
        while (scroller.IsCoasting && scroller.CoastMs < until - 1e-9)
        {
            now += Math.Min(step, until - scroller.CoastMs);
            covered += scroller.Tick(now);
        }

        return covered;
    }

    private static void Physics(Action<bool, string> expect)
    {
        var on = () => true;

        // The release velocity: the endpoints over the last 60 ms (pure).
        expect(MomentumScroller.VelocityOf([(0, 0), (30, 10)]) == 3000 && MomentumScroller.VelocityOf([(0, 0)]) == 0
            && MomentumScroller.VelocityOf([(0, 0), (5, 0.5)]) == 0 && MomentumScroller.VelocityOf([(0, 0), (200, 10)]) == MomentumScroller.MaxSpeed,
            "the velocity of samples: 30 DIP in 10 ms is 3000 DIP/s; one sample, or under a millisecond between them, is 0; faster than 8000 DIP/s is held to 8000");
        expect(MomentumScroller.VelocityOf([(0, 0), (500, 10), (500, 50), (530, 110)]) == 500,
            "only the last 60 ms count: a fast move 100 ms before letting go, then 30 DIP in the last 60 ms, is 500 DIP/s");

        // A flick: 300 DIP in 60 ms, let go at once.
        var flick = new MomentumScroller(on);
        Drag(flick, 0, 60, 5000);
        var v0 = flick.Release(60);
        expect(Math.Abs(v0 - 5000) < 1e-6 && flick.IsCoasting && Math.Abs(flick.DecayPerMs - MomentumScroller.DragDecayPerMs) < 1e-12,
            F($"a 300 DIP drag over 60 ms let go at once coasts at {v0:0} DIP/s, decaying at d = {flick.DecayPerMs:0.000} a millisecond"));

        // The same coast at 500 Hz and at 60 Hz: closed form, so the same ground at the same moment of its own time.
        var fast = new MomentumScroller(on);
        Drag(fast, 0, 60, 5000);
        fast.Release(60);
        var slow = new MomentumScroller(on);
        Drag(slow, 0, 60, 5000);
        slow.Release(60);
        var (nowFast, nowSlow) = (60.0, 60.0);
        var at100Fast = TickUntil(fast, ref nowFast, 2, 100);
        var at100Slow = TickUntil(slow, ref nowSlow, 1000 / 60.0, 100);
        var speed100 = fast.Velocity / v0;
        var at500Fast = at100Fast + TickUntil(fast, ref nowFast, 2, 500);
        var at500Slow = at100Slow + TickUntil(slow, ref nowSlow, 1000 / 60.0, 500);
        var k = -Math.Log(MomentumScroller.DragDecayPerMs);
        double Closed(double t) => 5.0 / k * (1 - Math.Pow(MomentumScroller.DragDecayPerMs, t));
        expect(Math.Abs(at100Fast - at100Slow) < 1e-6 && Math.Abs(at100Fast - Closed(100)) < 1e-6,
            F($"after 100 ms it has covered {at100Fast:0.00} DIP, ticked at 500 Hz and at 60 Hz alike (v0 / k * (1 - d^100))"));
        expect(Math.Abs(at500Fast - at500Slow) < 1e-6 && Math.Abs(at500Fast - Closed(500)) < 1e-6,
            F($"after 500 ms {at500Fast:0.0} DIP, the same at both rates"));
        expect(Math.Abs(speed100 - Math.Pow(MomentumScroller.DragDecayPerMs, 100)) < 1e-9,
            F($"its speed after 100 ms is v0 * d^100 = {speed100:0.0000} of v0"));

        // It ends below 20 DIP/s, at about 2758 ms for 5000 DIP/s, short of the whole v0 / k.
        var total = at500Fast;
        while (fast.IsCoasting && fast.CoastMs < 10000)
        {
            nowFast += 2;
            total += fast.Tick(nowFast);
        }

        var ended = fast.CoastMs;
        expect(!fast.IsCoasting && ended >= 2756 && ended <= 2762 && fast.Velocity == 0,
            F($"it stops once slower than 20 DIP/s: after {ended:0} ms of its own time"));
        expect(Math.Abs(total - Closed(ended)) < 1e-6 && total < 5.0 / k,
            F($"having covered {total:0} DIP in all, short of v0 / k = {5.0 / k:0} DIP"));
        expect(fast.Tick(nowFast + 16) == 0, "a tick after it stopped covers nothing");

        // A stalled frame (200 ms since the last) advances the coast by 50 ms at most: no jump.
        var stall = new MomentumScroller(on);
        Drag(stall, 0, 60, 5000);
        stall.Release(60);
        var jumped = stall.Tick(260);
        expect(stall.CoastMs == MomentumScroller.MaxStepMs && Math.Abs(jumped - Closed(50)) < 1e-6,
            F($"a frame 200 ms late advances the coast by {stall.CoastMs:0} ms of its own time ({jumped:0.0} DIP), never the whole gap"));

        // The flick threshold, exactly at its edge: 12.5 DIP in 50 ms is 250 DIP/s and coasts; 12.45 DIP is 249 and stops dead.
        var edge = new MomentumScroller(on);
        edge.Press(0);
        edge.Sample(0, 0);
        edge.Sample(12.5, 50);
        var at250 = edge.Release(50);
        expect(at250 == 250 && edge.IsCoasting, F($"a release at exactly {at250:0} DIP/s coasts (the threshold is 250 or more)"));
        edge.Press(100);
        edge.Sample(0, 100);
        edge.Sample(12.45, 150);
        var under = edge.Release(150);
        expect(under == 0 && !edge.IsCoasting, "a release at 249 DIP/s gives 0 and does not coast: a slow positioning drag stops dead");

        // The rest before letting go: 39.9 ms after the last move coasts; 40 ms stops dead.
        var rest = new MomentumScroller(on);
        Drag(rest, 0, 60, 5000);
        var justInTime = rest.Release(60 + 39.9);
        Drag(rest, 1000, 60, 5000);
        var rested = rest.Release(1060 + 40);
        expect(justInTime == 5000 && rested == 0 && !rest.IsCoasting,
            "let go 39.9 ms after the last move it coasts at 5000 DIP/s; let go 40 ms after it, 0: a pause before lifting never coasts");

        // A hand held still: tremors under half a DIP are no move, so the rest goes on ageing through them.
        var tremor = new MomentumScroller(on);
        Drag(tremor, 0, 60, 5000);
        for (var t = 65.0; t <= 105; t += 5)
            tremor.Sample(300 + (t % 10 == 0 ? 0.3 : 0), t);
        expect(tremor.Release(105) == 0 && !tremor.IsCoasting,
            "a flick then 45 ms of tremors under half a DIP, let go: no coast (they are no move, the rest counts from the last real one)");

        // The window: a fast move more than 60 ms before letting go, then a slow one, gives the slow speed.
        var window = new MomentumScroller(on);
        window.Press(0);
        for (var t = 0.0; t <= 40; t += 5)
            window.Sample(t * 10, t);
        for (var t = 50.0; t <= 140; t += 10)
            window.Sample(400 + (t - 40) * 0.3, t);
        var windowed = window.Release(140);
        expect(Math.Abs(windowed - 300) < 1e-6 && window.IsCoasting,
            F($"10000 DIP/s, then 300 DIP/s for the last 100 ms: it coasts at {windowed:0} DIP/s, the speed of the last 60 ms"));

        // Touch-to-stop.
        var touch = new MomentumScroller(on);
        Drag(touch, 0, 60, 5000);
        touch.Release(60);
        touch.Tick(76);
        touch.Press(80);
        expect(!touch.IsCoasting && touch.Velocity == 0 && touch.Tick(96) == 0, "a press while coasting stops it at once: nothing more is covered");
        expect(touch.Release(100) == 0, "and that press let go without moving is no flick");

        // The wheel: two ticks under 40 ms apart start a coast at the stream's speed; a single tick never does.
        var wheel = new MomentumScroller(on);
        var first = wheel.Wheel(120, 0);
        var coastedAfterOne = wheel.IsCoasting;
        var second = wheel.Wheel(120, 15);
        expect(first == 120 && !coastedAfterOne && second == 120 && wheel.IsCoasting && wheel.Velocity == MomentumScroller.MaxSpeed
            && Math.Abs(wheel.DecayPerMs - MomentumScroller.WheelDecayPerMs) < 1e-12,
            F($"a notch of 120 applies 120 and does not coast; a second 15 ms later applies 120 and starts a coast at {wheel.Velocity:0} DIP/s (held to the most), d = {wheel.DecayPerMs:0.00}"));

        // The absorb: 10 ms into the coast it has covered some of the next notch, which gives only the rest.
        var covered10 = wheel.Tick(25);
        var third = wheel.Wheel(120, 25);
        expect(Math.Abs(third - (120 - covered10)) < 1e-9 && third > 0 && wheel.IsCoasting,
            F($"a third notch 10 ms into the coast applies {third:0.00}: the 120 less the {covered10:0.00} the coast already covered"));

        // A tick against the coast stops it and is spent doing so.
        var against = wheel.Wheel(-120, 30);
        expect(against == 0 && !wheel.IsCoasting && wheel.Velocity == 0, "a notch the other way while coasting stops the coast and applies nothing");
        var afterStop = wheel.Wheel(-120, 32);
        expect(afterStop == -120 && !wheel.IsCoasting, "the next notch that way is a fresh stream: applied whole, no coast from one tick");

        // A pause of 40 ms or more between ticks begins a fresh stream.
        var paused = new MomentumScroller(on);
        paused.Wheel(120, 0);
        var late = paused.Wheel(120, 40);
        expect(late == 120 && !paused.IsCoasting, "two notches 40 ms apart: each applied whole, and no coast (a fresh stream each)");
        var quick = paused.Wheel(120, 60);
        expect(quick == 120 && paused.IsCoasting, "a third 20 ms after the second: the stream of two is fast, and coasts");

        // A coast that runs out ends; the next notch is applied whole.
        var tail = new MomentumScroller(on);
        tail.Wheel(120, 0);
        tail.Wheel(120, 15);
        var tailNow = 15.0;
        var tailCovered = 0.0;
        while (tail.IsCoasting && tailNow < 2000)
        {
            tailNow += 2;
            tailCovered += tail.Tick(tailNow);
        }

        expect(!tail.IsCoasting && tailCovered > 700 && tailCovered < 800 && tail.CoastMs < 700,
            F($"a wheel coast at 8000 DIP/s runs out after {tail.CoastMs:0} ms, {tailCovered:0} DIP on (the short tail of d = 0.99)"));

        // Momentum off: a release gives 0, a notch is applied as it is, nothing coasts.
        var off = new MomentumScroller(() => false);
        Drag(off, 0, 60, 5000);
        var offRelease = off.Release(60);
        var offA = off.Wheel(120, 100);
        var offB = off.Wheel(120, 110);
        expect(offRelease == 0 && offA == 120 && offB == 120 && !off.IsCoasting, "with momentum off a flick gives 0 and notches are applied as they are: nothing coasts");

        // The default reads the setting at each release.
        var current = AppSettings.Current;
        var was = current.EnableMomentumScrolling;
        try
        {
            var setting = new MomentumScroller();
            current.EnableMomentumScrolling = true;
            Drag(setting, 0, 60, 5000);
            var onRelease = setting.Release(60);
            current.EnableMomentumScrolling = false;
            Drag(setting, 1000, 60, 5000);
            var offSetting = setting.Release(1060);
            expect(was && onRelease == 5000 && offSetting == 0,
                "new MomentumScroller() follows AppSettings.EnableMomentumScrolling (true in a new install): it coasts while on, not once it is switched off");
        }
        finally
        {
            current.EnableMomentumScrolling = was;
        }

        expect(MomentumScroller.FlickThreshold == 250 && MomentumScroller.DetentionMs == 40 && MomentumScroller.WindowMs == 60,
            "the guards: 250 DIP/s, a 40 ms rest, velocity over 60 ms (CLAUDE.md 10.2)");
    }

    private static void Timeline(Action<bool, string> expect)
    {
        // A control 1000 DIPs wide, 10 s across of a 60 s sequence from 20 s; twelve rows of 56 (taller than its 300).
        var surface = new UnifiedTimelineControl();
        surface.Measure(new Size(1000, 300));
        surface.Arrange(new Rect(0, 0, 1000, 300));
        const double span = 10, sequence = 60, last = 50;
        var start = 20.0;
        surface.SetView(start, span, sequence);
        var rows = new List<TimelineRow>();
        for (var i = 0; i < 12; i++)
            rows.Add(new TimelineRow { Key = "row" + i, Data = "row" + i, Kind = TimelineRowKind.Video, Height = 56 });
        surface.SetRows(rows);
        var scale = surface.PixelsPerSecond;

        // The host, as MainWindow's PanTimeline: the view moves at once, held within the sequence.
        var pans = new List<double>();
        var zooms = 0;
        surface.PanRequested += pixels =>
        {
            pans.Add(pixels);
            start = Math.Clamp(start + pixels / scale, 0, last);
            surface.SetView(start, span, sequence);
        };
        surface.ZoomRequested += (_, _) => zooms++;
        expect(scale > 0 && surface.ExtentHeight > 300, F($"laid out without a window: 10 s across, rows {surface.ExtentHeight:0} DIPs tall in a 300 DIP control"));

        // Steps the coast at 500 Hz from `now`; the pans it made, until it ends or `ms` pass.
        List<double> Coast(ref double now, double ms)
        {
            var from = pans.Count;
            for (var end = now + ms; surface.IsCoasting && now < end;)
            {
                now += 2;
                surface.StepMomentum(now);
            }

            return pans.GetRange(from, pans.Count - from);
        }

        static bool Shrinking(List<double> steps) => steps.Count > 2 && steps.All(s => s > 0) && steps.Zip(steps.Skip(1)).All(p => p.Second <= p.First + 1e-9);

        // Shift + wheel: four notches toward later, 15 ms apart.
        var now = 1000.0;
        for (var i = 0; i < 4; i++)
            surface.WheelAt(-120, ModifierKeys.Shift, now + 15 * i);
        now += 45;
        expect(pans.Count == 4 && pans.All(p => p == 120) && surface.IsCoasting,
            "four quick Shift + wheel notches each pan 120 DIPs later, and the view coasts on");
        var coast = Coast(ref now, 3000);
        expect(Shrinking(coast) && !surface.IsCoasting && coast.Sum() > 120,
            F($"stepped, it pans on later in {coast.Count} shrinking steps ({coast.Sum():0} DIPs) and stops by itself"));
        expect(!surface.StepMomentum(now + 2), "once stopped, a frame pans nothing");

        // The sideways wheel of a touchpad: small deltas 8 ms apart coast the same way.
        start = 20;
        surface.SetView(start, span, sequence);
        now += 1000;
        pans.Clear();
        for (var i = 0; i < 6; i++)
            surface.PanWheelAt(30, now + 8 * i);
        now += 40;
        expect(pans.Count >= 2 && pans.All(p => p > 0) && surface.IsCoasting, "a touchpad's sideways scroll (30 a message, 8 ms apart) pans later and coasts on");
        surface.StopMomentum();

        // A middle-button flick: the pointer swept 300 DIPs left in 60 ms pans 300 DIPs later and coasts on.
        start = 20;
        surface.SetView(start, span, sequence);
        now += 1000;
        pans.Clear();
        surface.MiddlePanPress(700, now);
        for (var t = 5; t <= 60; t += 5)
            surface.MiddlePanMove(700 - t * 5, now + t);
        surface.MiddlePanRelease(now + 60);
        now += 60;
        var dragged = pans.Sum();
        expect(Math.Abs(dragged - 300) < 1e-9 && surface.IsCoasting, "a middle-button flick (300 DIPs in 60 ms) pans one for one, then the view coasts on");
        var flickCoast = Coast(ref now, 100);
        expect(Shrinking(flickCoast), F($"its first 100 ms pan on {flickCoast.Sum():0} DIPs later, slowing"));

        // Any press stops it: the real PreviewMouseDown, as a click on the tracks raises it before anything else.
        surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
        expect(!surface.IsCoasting && !surface.StepMomentum(now + 2), "a press on the timeline stops the coast where it is");

        // A slow middle-button drag (100 DIP/s) never coasts; nor does a flick held still 40 ms before letting go.
        now += 1000;
        surface.MiddlePanPress(500, now);
        for (var t = 10; t <= 200; t += 10)
            surface.MiddlePanMove(500 - t * 0.1, now + t);
        surface.MiddlePanRelease(now + 200);
        expect(!surface.IsCoasting, "a slow middle-button drag (100 DIP/s) stops dead when let go");
        now += 1000;
        surface.MiddlePanPress(700, now);
        for (var t = 5; t <= 60; t += 5)
            surface.MiddlePanMove(700 - t * 5, now + t);
        surface.MiddlePanRelease(now + 100);
        expect(!surface.IsCoasting, "a flick held still for 40 ms before letting go stops dead (careful cut alignment never coasts)");

        // Into the end of the sequence: the coast stops there.
        start = 40;
        surface.SetView(start, span, sequence);
        now += 1000;
        surface.MiddlePanPress(700, now);
        for (var t = 5; t <= 60; t += 5)
            surface.MiddlePanMove(700 - t * 5, now + t);
        surface.MiddlePanRelease(now + 60);
        now += 60;
        Coast(ref now, 3000);
        expect(!surface.IsCoasting && start == last, F($"a flick near the end coasts into it and stops there (the view at {start:0} s, its last start)"));

        // Ctrl + wheel zooms and never coasts; it stops a coast under way.
        start = 20;
        surface.SetView(start, span, sequence);
        now += 1000;
        surface.WheelAt(-120, ModifierKeys.Shift, now);
        surface.WheelAt(-120, ModifierKeys.Shift, now + 15);
        var wasCoasting = surface.IsCoasting;
        surface.WheelAt(120, ModifierKeys.Control, now + 20);
        surface.WheelAt(120, ModifierKeys.Control, now + 30);
        expect(wasCoasting && !surface.IsCoasting && zooms == 2, "Ctrl + wheel zooms (twice) and stops the coast; it never coasts itself");

        // A zoom (a new span) stops the coast along the old view.
        now += 1000;
        surface.WheelAt(-120, ModifierKeys.Shift, now);
        surface.WheelAt(-120, ModifierKeys.Shift, now + 15);
        wasCoasting = surface.IsCoasting;
        surface.SetView(start, 8, sequence);
        expect(wasCoasting && !surface.IsCoasting, "the view set to another span (a zoom, a fit) stops the coast");
        surface.SetView(start, span, sequence);

        // The plain wheel coasts the rows down, and stops at the bottom; up, at the top.
        now += 1000;
        var bottom = surface.ExtentHeight - 300;
        surface.VerticalOffset = 0;
        for (var i = 0; i < 3; i++)
            surface.WheelAt(-120, ModifierKeys.None, now + 15 * i);
        now += 30;
        var afterNotches = surface.VerticalOffset;
        expect(afterNotches == 144 && surface.IsCoasting, "three quick plain notches scroll the rows 48 DIPs each (144) and coast on");
        Coast(ref now, 3000);
        expect(!surface.IsCoasting && surface.VerticalOffset == bottom, F($"the rows coast on down to the bottom ({bottom:0}) and stop there"));
        now += 1000;
        for (var i = 0; i < 3; i++)
            surface.WheelAt(120, ModifierKeys.None, now + 15 * i);
        now += 30;
        Coast(ref now, 3000);
        expect(!surface.IsCoasting && surface.VerticalOffset == 0, "and back up, to the top, where they stop");

        // With the setting off: notches move exactly as before and nothing coasts (the scrollers read it at each notch).
        var current = AppSettings.Current;
        var was = current.EnableMomentumScrolling;
        try
        {
            current.EnableMomentumScrolling = false;
            start = 20;
            surface.SetView(start, span, sequence);
            now += 1000;
            pans.Clear();
            for (var i = 0; i < 4; i++)
                surface.WheelAt(-120, ModifierKeys.Shift, now + 15 * i);
            now += 45;
            expect(pans.Count == 4 && pans.All(p => p == 120) && !surface.IsCoasting && !surface.StepMomentum(now + 2),
                "with Enable Momentum Scrolling off, four quick notches pan 120 each and nothing more");
        }
        finally
        {
            current.EnableMomentumScrolling = was;
        }

        // The ruler: a middle-button flick coasts the bars on through its own frame hook (stepped here), a slow drag does not, a zoom stops it.
        var ruler = new TimelineRuler();
        ruler.Measure(new Size(800, 24));
        ruler.Arrange(new Rect(0, 0, 800, 24));
        var rulerStart = 20.0;
        ruler.Show(rulerStart, span, sequence, []);
        var rulerPans = new List<double>();
        ruler.PanRequested += pixels =>
        {
            rulerPans.Add(pixels);
            rulerStart = Math.Clamp(rulerStart + pixels / 800 * span, 0, last);
            ruler.Show(rulerStart, span, sequence, []);
        };
        now += 1000;
        ruler.MiddlePanPress(600, now);
        for (var t = 5; t <= 60; t += 5)
            ruler.MiddlePanMove(600 - t * 5, now + t);
        ruler.MiddlePanRelease(now + 60);
        now += 60;
        var beforeCoast = rulerPans.Count;
        expect(ruler.IsCoasting && Math.Abs(rulerPans.Sum() - 300) < 1e-9, "on the ruler, a middle-button flick pans one for one and coasts on");
        for (var i = 0; i < 25; i++)
            ruler.StepCoast(now += 2);
        var rulerCoast = rulerPans.Skip(beforeCoast).ToList();
        expect(Shrinking(rulerCoast), F($"its coast pans on later, slowing ({rulerCoast.Sum():0} DIPs in 50 ms)"));
        ruler.Show(rulerStart, 8, sequence, []);
        expect(!ruler.IsCoasting && !ruler.StepCoast(now + 2), "a zoom (the ruler shown at another span) stops it");
        now += 1000;
        ruler.MiddlePanPress(400, now);
        for (var t = 10; t <= 200; t += 10)
            ruler.MiddlePanMove(400 - t * 0.1, now + t);
        ruler.MiddlePanRelease(now + 200);
        expect(!ruler.IsCoasting, "a slow drag on the ruler stops dead");
        now += 1000;
        ruler.PanWheelAt(120, now);
        ruler.PanWheelAt(120, now + 15);
        expect(ruler.IsCoasting, "quick Shift + wheel notches over the ruler coast too");
        ruler.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
        expect(!ruler.IsCoasting, "and a press on the ruler stops it");

        // The sideways wheel's hook: its own, on the main window, never in the frame hooks (golden window-frame-messages).
        var root = RepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "MainWindow.TimelineView.cs"));
        var titleBar = File.ReadAllText(Path.Combine(root, "src", "MainWindow.TitleBar.cs"));
        expect(view.Contains("AddHook(TimelineSidewaysWheel)") && view.Contains("msg != WmMouseHWheel") && !titleBar.Contains("WmMouseHWheel") && !titleBar.Contains("TimelineSidewaysWheel"),
            "WM_MOUSEHWHEEL reaches the timeline through a hook of its own (MainWindow.TimelineView.cs), not TitleBarHook or FrameHook");
        expect(view.Contains("StopTimelineMomentum();\r\n            _viewStart = at - span * 0.05;") || view.Contains("StopTimelineMomentum();\n            _viewStart = at - span * 0.05;"),
            "the playhead turning the page (FollowPlayhead) stops a coast first");
    }

    private static void SubtitleScrub(Action<bool, string> expect)
    {
        // 800 DIPs wide, 90 DIPs a second, a 600 s strip at 300 s; one cue at 301 to 303 s (x 490 to 670, right of the playhead at 400).
        var cue = new SubtitleCue { Start = 301, End = 303, Text = "hello there" };
        var strip = new SubtitleTimeline { DurationSeconds = 600, Cues = [cue] };
        strip.Measure(new Size(800, 120));
        strip.Arrange(new Rect(0, 0, 800, 120));
        strip.PositionSeconds = 300;
        var scrubs = new List<double>();
        strip.Scrubbed += seconds => scrubs.Add(seconds);
        var released = 0;
        strip.CueReleased += _ => released++;

        // A flick of the strip to the left (later moments come under the playhead): 300 DIPs in 60 ms.
        var now = 1000.0;
        strip.PressAt(new Point(300, 80), now);
        for (var t = 5; t <= 60; t += 5)
            strip.MoveTo(new Point(300 - t * 5, 80), now + t);
        strip.ReleaseAt(now + 60);
        now += 60;
        var afterDrag = strip.PositionSeconds;
        expect(Math.Abs(afterDrag - (300 + 300 / 90.0)) < 1e-9 && strip.IsCoasting,
            F($"a flick of the strip 300 DIPs left scrubs to {afterDrag:0.000} s and coasts on"));
        var fromCoast = scrubs.Count;
        for (var i = 0; i < 50; i++)
            strip.StepCoast(now += 2);
        var coastScrubs = scrubs.Skip(fromCoast).ToList();
        expect(coastScrubs.Count == 50 && coastScrubs.Zip(coastScrubs.Skip(1)).All(p => p.Second > p.First) && coastScrubs[^1] == strip.PositionSeconds,
            F($"each coasting frame scrubs on later and says so: {coastScrubs[^1] - afterDrag:0.000} s further in 100 ms"));

        // The host echoing the scrub back leaves the coast alone; another moment set from outside (playback, a typed time) stops it.
        strip.PositionSeconds = strip.PositionSeconds;
        var echoKept = strip.IsCoasting;
        strip.PositionSeconds = 100;
        expect(echoKept && !strip.IsCoasting && !strip.StepCoast(now + 2) && strip.PositionSeconds == 100,
            "the same moment set again (an echo) keeps the coast; another moment set from outside stops it there");

        // A cue dragged quickly by its middle never coasts.
        strip.PositionSeconds = 300;
        now += 1000;
        strip.PressAt(new Point(580, 80), now);
        for (var t = 5; t <= 60; t += 5)
            strip.MoveTo(new Point(580 + t * 5, 80), now + t);
        strip.ReleaseAt(now + 60);
        expect(!strip.IsCoasting && released == 1 && cue.Start > 301 && strip.PositionSeconds == 300,
            F($"a cue flicked by its middle moves (to {cue.Start:0.000} s) and is let go, and nothing coasts"));

        // A flick toward the start coasts into 0 s and stops there.
        strip.PositionSeconds = 2;
        now += 1000;
        strip.PressAt(new Point(100, 80), now);
        for (var t = 5; t <= 60; t += 5)
            strip.MoveTo(new Point(100 + t * 2, 80), now + t);
        strip.ReleaseAt(now + 60);
        now += 60;
        var coastingToStart = strip.IsCoasting;
        for (var i = 0; i < 2000 && strip.IsCoasting; i++)
            strip.StepCoast(now += 2);
        expect(coastingToStart && !strip.IsCoasting && strip.PositionSeconds == 0, "a flick toward the start coasts into 0 s and stops there");

        // A slow drag of the strip (100 DIP/s), or a click, never coasts.
        strip.PositionSeconds = 300;
        now += 1000;
        strip.PressAt(new Point(300, 80), now);
        for (var t = 10; t <= 300; t += 10)
            strip.MoveTo(new Point(300 - t * 0.1, 80), now + t);
        strip.ReleaseAt(now + 300);
        expect(!strip.IsCoasting, "a slow drag of the strip stops dead when let go");
        now += 1000;
        strip.PressAt(new Point(300, 80), now);
        strip.ReleaseAt(now + 5);
        expect(!strip.IsCoasting, "a click on the empty strip is no flick");

        // A press stops a coast: the real PreviewMouseDown.
        now += 1000;
        strip.PressAt(new Point(300, 80), now);
        for (var t = 5; t <= 60; t += 5)
            strip.MoveTo(new Point(300 - t * 5, 80), now + t);
        strip.ReleaseAt(now + 60);
        var coasting = strip.IsCoasting;
        strip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseDownEvent });
        expect(coasting && !strip.IsCoasting, "any press on the strip (a right-click too) stops a coast");
    }

    private static void MediaBrowser(Action<bool, string> expect)
    {
        var content = new Border { Height = 5000, Width = 400 };
        var viewer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        viewer.Measure(new Size(400, 300));
        viewer.Arrange(new Rect(0, 0, 400, 300));
        viewer.UpdateLayout();
        ScrollMomentum.Attach(viewer);
        expect(ScrollMomentum.GetIsEnabled(viewer) && double.IsNaN(ScrollMomentum.Target(viewer)),
            "attached, it has made nothing yet (its state comes with the first wheel): cheap enough for a pane's constructor before Frame 1");
        var room = viewer.ScrollableHeight;
        var step = 16.0 * Math.Max(SystemParameters.WheelScrollLines, 1);
        expect(room == 4700, F($"a 5000 DIP list in a 300 DIP view: {room:0} DIPs of room"));

        // One notch moves its usual step and does not coast.
        var now = 1000.0;
        var moved = ScrollMomentum.Wheel(viewer, -120, now);
        viewer.UpdateLayout();
        expect(moved == step && viewer.VerticalOffset == step && !ScrollMomentum.IsCoasting(viewer),
            "one notch scrolls 16 DIPs a wheel line (Windows' setting; 48 at the usual 3) and does not coast");

        // A fast stream coasts on (the aim kept here, as the ScrollViewer catches up at the next layout).
        now += 1000;
        for (var i = 0; i < 3; i++)
            ScrollMomentum.Wheel(viewer, -120, now + 15 * i);
        now += 30;
        viewer.UpdateLayout();
        var afterNotches = viewer.VerticalOffset;
        expect(afterNotches == 4 * step && ScrollMomentum.IsCoasting(viewer) && !ScrollMomentum.IsHooked(viewer),
            "three quick notches scroll three steps and coast on (no frame hooked off screen: a test steps it)");
        var steps = 0;
        for (var i = 0; i < 50; i++)
        {
            if (ScrollMomentum.Step(viewer, now += 2))
                steps++;
        }

        viewer.UpdateLayout();
        expect(steps == 50 && viewer.VerticalOffset > afterNotches && viewer.VerticalOffset == ScrollMomentum.Target(viewer),
            "stepped, it coasts on further every frame for 100 ms, where it aimed (the ScrollViewer's offset caught up with the aim)");

        // A press stops it.
        viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
        expect(!ScrollMomentum.IsCoasting(viewer) && !ScrollMomentum.Step(viewer, now + 2), "a press on the list stops the coast where it is");

        // Near the bottom, a fast stream coasts into the end and stops there.
        viewer.ScrollToVerticalOffset(4500);
        viewer.UpdateLayout();
        now += 1000;
        for (var i = 0; i < 3; i++)
            ScrollMomentum.Wheel(viewer, -120, now + 15 * i);
        now += 30;
        for (var i = 0; i < 1000 && ScrollMomentum.IsCoasting(viewer); i++)
            ScrollMomentum.Step(viewer, now += 2);
        viewer.UpdateLayout();
        expect(!ScrollMomentum.IsCoasting(viewer) && viewer.VerticalOffset == room, "near the bottom, a fast stream coasts into the end and stops there");

        // And up from near the top, into the top.
        viewer.ScrollToVerticalOffset(300);
        viewer.UpdateLayout();
        now += 1000;
        for (var i = 0; i < 3; i++)
            ScrollMomentum.Wheel(viewer, 120, now + 15 * i);
        now += 30;
        for (var i = 0; i < 1000 && ScrollMomentum.IsCoasting(viewer); i++)
            ScrollMomentum.Step(viewer, now += 2);
        viewer.UpdateLayout();
        expect(!ScrollMomentum.IsCoasting(viewer) && viewer.VerticalOffset == 0, "up from near the top, it coasts into the top and stops there");

        // ScrollMomentum.Stop: a host's own scrolling takes over.
        now += 1000;
        ScrollMomentum.Wheel(viewer, -120, now);
        ScrollMomentum.Wheel(viewer, -120, now + 15);
        var wasCoasting = ScrollMomentum.IsCoasting(viewer);
        ScrollMomentum.Stop(viewer);
        expect(wasCoasting && !ScrollMomentum.IsCoasting(viewer), "ScrollMomentum.Stop halts it (the browser's middle-click auto-scroll taking over)");

        // A list with nothing to scroll lets the wheel pass on.
        var small = new ScrollViewer { Content = new Border { Height = 100 } };
        small.Measure(new Size(400, 300));
        small.Arrange(new Rect(0, 0, 400, 300));
        small.UpdateLayout();
        ScrollMomentum.Attach(small);
        expect(double.IsNaN(ScrollMomentum.Wheel(small, -120, now)), "a list with nothing to scroll does not take the wheel: it passes on");

        // A ListBox: its ScrollViewer found in its template at the first wheel, pixel scrolling as the browser's lists do.
        var list = new ListBox();
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        for (var i = 0; i < 100; i++)
            list.Items.Add(new Border { Height = 50, Width = 300 });
        list.Measure(new Size(400, 300));
        list.Arrange(new Rect(0, 0, 400, 300));
        list.UpdateLayout();
        ScrollMomentum.Attach(list);
        now += 1000;
        var listMoved = ScrollMomentum.Wheel(list, -120, now);
        list.UpdateLayout();
        var inner = FindViewer(list);
        expect(listMoved == step && inner is not null && inner.VerticalOffset == step, "on a ListBox the ScrollViewer of its template is found at the first wheel and scrolls the step");

        // Switched off: the handlers and the state go.
        ScrollMomentum.SetIsEnabled(viewer, false);
        expect(!ScrollMomentum.GetIsEnabled(viewer) && double.IsNaN(ScrollMomentum.Target(viewer)), "switched off, its state is dropped");
    }

    private static void Setting(Action<bool, string> expect)
    {
        expect(new AppSettings().EnableMomentumScrolling, "AppSettings.EnableMomentumScrolling is true in a new install");
        var json = JsonSerializer.Serialize(new AppSettings(), AppSettingsJsonContext.Default.AppSettings);
        expect(json.Contains("\"EnableMomentumScrolling\":true", StringComparison.Ordinal), "it is written to the settings file (source-generated JSON)");
        var older = JsonSerializer.Deserialize("{}", AppSettingsJsonContext.Default.AppSettings)!;
        var switchedOff = JsonSerializer.Deserialize("{\"EnableMomentumScrolling\":false}", AppSettingsJsonContext.Default.AppSettings)!;
        expect(older.EnableMomentumScrolling && !switchedOff.EnableMomentumScrolling, "a file from before it reads true; a file with it off reads false");

        var root = RepositoryRoot();
        var settings = XDocument.Load(Path.Combine(root, "src", "Dialogs", "SettingsWindow.xaml"));
        var tab = settings.Descendants(Wpf + "TabItem").FirstOrDefault(t => (string?)t.Attribute("Header") == "Timeline & Player");
        var boxes = settings.Descendants(Wpf + "CheckBox").Where(c => (string?)c.Attribute("IsChecked") == "{Binding EnableMomentumScrolling}").ToList();
        expect(tab is not null && boxes.Count == 1 && boxes[0].Ancestors(Wpf + "TabItem").FirstOrDefault() == tab,
            "Settings has one CheckBox bound to it, in the Timeline & Player tab");

        var items = HandPegApp.MainWindow.TimelineSettingGroups
            .SelectMany(g => g.Items.Select(i => (Group: g.Header, Item: i)))
            .Where(p => p.Item.Kept == nameof(AppSettings.EnableMomentumScrolling)).ToList();
        expect(items.Count == 1 && items[0].Group == "Snapping & Playback",
            F($"the Timeline Settings menu (the gear and the tab's right-click) holds it once, in Snapping & Playback: \"{(items.Count > 0 ? items[0].Item.Header : "")}\""));
    }

    private static ScrollViewer? FindViewer(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer)
                return viewer;
            if (FindViewer(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }
}
