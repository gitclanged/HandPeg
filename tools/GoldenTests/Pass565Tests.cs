using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.Shell;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.6.5's goldens: the --dpi-matrix runner (its geometry rules, and a live run of HandPeg.exe across two simulated DPIs and two
/// aspect ratios), the project-tab controller's prepare / commit / abort / finish and activation generation, the standalone Color Match
/// bar, the decoder pool's urgent-seek yield (in-flight background reads give a visible chase the decode engine and park), the shared
/// touchpad wheel's consumers, the Couch Voice Auto-Switcher, Cutout tight bounds, and the 8-handle gizmo's rules and snapping.
/// Filter terms: dpi, tab-switch, decoder-yield (AutoTest Fast).
/// </summary>
public static class Pass565Tests
{
    // The --dpi-matrix report's cells, as DpiMatrixRunner writes them (JSON).
    private sealed record DpiCell(string Window, double Scale, string Aspect, bool Passed, string Detail);
    private sealed record DpiReport(List<DpiCell> Cells);

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("dpi-matrix-geometry", "# --dpi-matrix: DPI per scale, the aspect rectangle on the work area, 1 px edge matching, its options and its seams.", DpiMatrixGeometry);
        yield return Case("tab-switch-controller", "# ProjectTabController: one operation at a time, every activation bumps the generation, abort returns or marks, finish ends.", TabSwitchController);
        yield return Case("color-match-bar-standalone", "# The Color Match bar is a control of its own: it reaches the window only through IColorMatchHost; the grade wipe follows events, no polling timer.", ColorMatchStandalone);
        yield return Case("decoder-yield-preemption", "# Urgent-seek yield: in-flight pre-roll, pointer work and a paused cut's first frames give a visible chase the decode engine, park and resume.", DecoderYield);
        yield return Case("touchpad-wheel-consumers", "# One touchpad wheel rule (TouchpadWheel) behind the player, the timeline rows and the Cutout Studio.", TouchpadWheelConsumers);
        yield return Case("voice-scene-solver", "# Couch Voice Auto-Switcher: per-mic levels in, a contiguous shot list out, cuts leading the new speaker, crosstalk Wide, silence never cuts.", VoiceScene);
        yield return Case("cutout-tight-bounds", "# Cutout tight bounds: drawn outlines' vertices or a mask picture's visible pixels, landing on the box and centring the auto anchor.", CutoutTightBounds);
        yield return Case("gizmo-8-handle-snapping", "# The 8-handle gizmo: anchor hit square, Shift steps and frees, velocity-gated magnetic snapping to frame, boxes, gaps and sizes.", GizmoSnapping);
    }

    /// <summary>The live matrix: HandPeg.exe --dpi-matrix on this PC's primary monitor (visible windows), run after the Application exists.</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("dpi-matrix-live", "# HandPeg.exe --dpi-matrix at 100 % and 250 %, 16:9 and 21:9: overlay on the video window (1 px), buttons hit by UIA, fullscreen on the monitor.", DpiMatrixLive);
    }

    // ----- dpi-matrix-geometry -----

    private static void DpiMatrixGeometry(Action<bool, string> expect)
    {
        expect(DpiMatrixRunner.DpiOf(1) == 96 && DpiMatrixRunner.DpiOf(1.25) == 120 && DpiMatrixRunner.DpiOf(1.75) == 168 && DpiMatrixRunner.DpiOf(2.5) == 240,
            "a scale is its DPI: 96 at 100 %, 120 at 125 %, 168 at 175 %, 240 at 250 %");
        expect(DpiMatrixRunner.Scales.SequenceEqual([1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5]) && DpiMatrixRunner.QuickScales.SequenceEqual([1.0, 1.75, 2.5])
               && DpiMatrixRunner.Aspects.Select(a => a.Name).SequenceEqual(["16:9", "16:10", "3:2", "21:9"]),
            "the matrix covers 100-250 % in 25 % steps (quick: 100, 175, 250 %) and 16:9, 16:10, 3:2 and 21:9");

        var work = new Int32Rect(0, 0, 2880, 1800);
        var fits = DpiMatrixRunner.Aspects.Select(a => (a, r: DpiMatrixRunner.FitAspect(work, a.Width, a.Height))).ToList();
        expect(fits.All(f => DpiMatrixRunner.Inside(f.r, work) && Math.Abs((double)f.r.Width / f.r.Height - (double)f.a.Width / f.a.Height) < 0.002
                             && Math.Abs(f.r.X + f.r.Width / 2.0 - 1440) <= 1 && Math.Abs(f.r.Y + f.r.Height / 2.0 - 900) <= 1
                             && (f.r.Width == 2592 || f.r.Height == 1620)),
            "each aspect's rectangle keeps its ratio, fills 90 % of the work area one way and is centred on it");
        var offset = DpiMatrixRunner.FitAspect(new Int32Rect(-1920, 40, 1920, 1040), 21, 9);
        expect(offset is { Width: 1728 } && offset.X == -1920 + 96 && DpiMatrixRunner.Inside(offset, new Int32Rect(-1920, 40, 1920, 1040)),
            "a work area left of the primary origin or below a top taskbar is honoured");

        var r = new Int32Rect(100, 100, 500, 300);
        expect(DpiMatrixRunner.Matches(r, new Int32Rect(101, 99, 500, 302)) && !DpiMatrixRunner.Matches(r, new Int32Rect(102, 100, 500, 300))
               && !DpiMatrixRunner.Matches(r, null) && DpiMatrixRunner.Matches(r, r, 0) && !DpiMatrixRunner.Matches(r, new Int32Rect(100, 100, 501, 300), 0),
            "rectangles match when every edge is within 1 px (0 for the fullscreen check); a missing one never matches");

        expect(DpiMatrixRunner.ParseScales("100,150%,2.5,9,x") is [1.0, 1.5, 2.5] && DpiMatrixRunner.ParseScales(null) is null && DpiMatrixRunner.ParseScales("x") is null
               && DpiMatrixRunner.ParseScales("300") is [2.5],
            "--scales takes factors or percents, held to 100-250 %; nothing usable means the default matrix");
        expect(DpiMatrixRunner.ParseAspects("21:9, 16:9,4:3") is [("16:9", 16, 9), ("21:9", 21, 9)] && DpiMatrixRunner.ParseAspects("4:3") is null,
            "--aspects picks the known ratios in matrix order");

        var runner = SourceProbe.Src("Shell", "DpiMatrixRunner.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(runner, "private static void Simulate("), "WmDpiChanged", "SetWindowPos")
               && runner.Contains("WmDpiChanged = 0x02E0", StringComparison.Ordinal)
               && !runner.Contains("ChangeDisplaySettings", StringComparison.Ordinal) && !runner.Contains("SetProcessDpiAwareness", StringComparison.Ordinal),
            "DPI is simulated per window (a synthetic WM_DPICHANGED with the aspect's rectangle), never by changing the display");
        expect(SourceProbe.InOrder(SourceProbe.Member(runner, "public static int Run("), "AppPaths.DataFolderVariable", "SetEnvironmentVariable", "Matrix(", "DeleteData(data)"),
            "everything the windows keep goes into a private data folder (under HANDPEG_DATA_DIR when set), deleted at the end");
        var hit = SourceProbe.Member(runner, "private static List<string> HitTest(");
        expect(hit.Contains("AutomationElement.FromPoint", StringComparison.Ordinal) && hit.Contains("ApartmentState.MTA", StringComparison.Ordinal) && hit.Contains("Pump(", StringComparison.Ordinal),
            "buttons are hit with UIA ElementFromPoint from an MTA thread while the UI thread answers");
        expect(SourceProbe.InOrder(SourceProbe.Member(runner, "private static void CheckMain("), "SurfaceRectangle()", "OverlayWindow", "Matches(overlay, surface)", "TransportRow", "EffectsView")
               && SourceProbe.Member(runner, "private static void CheckFullscreen(").Contains("Matches(rect, monitor, 0)", StringComparison.Ordinal),
            "the overlay is compared with the native video window, transport and inspector buttons are hit, and fullscreen equals rcMonitor");
    }

    // ----- dpi-matrix-live -----

    private static void DpiMatrixLive(Action<bool, string> expect, StringBuilder text)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "HandPeg.exe");
        expect(File.Exists(exe), "HandPeg.exe is built beside the goldens");
        if (!File.Exists(exe))
            return;

        var report = Path.Combine(Path.GetTempPath(), "dpi-matrix-golden-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".json");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            ArgumentList = { DpiMatrixRunner.Argument, "--scales", "1,2.5", "--aspects", "16:9,21:9", "--timeout-minutes", "4", "--out", report },
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            expect(false, "the matrix finished within 5 minutes");
            return;
        }

        var exit = process.ExitCode;
        if (exit != DpiMatrixRunner.Passed)
            Console.WriteLine("        dpi-matrix output:\n" + output.Result);
        expect(exit == DpiMatrixRunner.Passed, "HandPeg.exe --dpi-matrix exits with its pass code");
        if (!File.Exists(report))
        {
            expect(false, "the matrix wrote its report");
            return;
        }

        var parsed = JsonSerializer.Deserialize<DpiReport>(File.ReadAllText(report));
        File.Delete(report);
        var cells = (parsed?.Cells ?? []).Select(c => (c.Window, c.Scale, c.Aspect, c.Passed, c.Detail)).ToList();
        string[] windows = ["main", "overlay", "buttons", "fullscreen", "mask", "mask-buttons", "first-run", "first-run-buttons"];
        expect(cells.Count == 2 * 2 * windows.Length, "every window was checked at each of the 2 x 2 configurations");
        foreach (var window in windows)
        {
            var mine = cells.Where(c => c.Window == window).ToList();
            var failed = mine.Where(c => !c.Passed).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Scale * 100:0} % {c.Aspect}: {c.Detail}")).ToList();
            text.Append(CultureInfo.InvariantCulture, $"  {window}: {mine.Count} configurations\n");
            expect(mine.Count == 4 && failed.Count == 0, $"{window} holds at 100 % and 250 %, 16:9 and 21:9{(failed.Count == 0 ? "" : ": " + string.Join("; ", failed))}");
        }
    }

    // ----- tab-switch-controller -----

    private sealed class LiveTab
    {
    }

    private static void TabSwitchController(Action<bool, string> expect)
    {
        var root = Path.Combine(AppPaths.Temp, "golden-tab-switch-controller");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        var tabs = new ProjectTabs(root, "a", "golden");
        var controller = new HandPegApp.Shell.ProjectTabController<LiveTab>(tabs);
        var a = controller.Active;
        var b = tabs.Add("B", "b");
        var c = tabs.Add("C", "c");
        var switching = new List<bool>();
        controller.SwitchingChanged += switching.Add;

        var generation = controller.Generation;
        var operation = controller.Prepare("switch to B");
        var refused = false;
        try
        {
            controller.Prepare("switch to C");
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        expect(controller.IsSwitching && ReferenceEquals(controller.Running, operation) && operation.From == a && operation.Prepared == generation && refused,
            "prepare starts one operation from the active tab; a second waits (refused while the first runs)");

        var liveA = new LiveTab();
        controller.PutBehind(a, liveA);
        controller.Commit(operation, b);
        expect(controller.Active == b && controller.Generation == generation + 1 && operation is { To.Id: "b", Aborted: false } && operation.Committed == controller.Generation
               && controller.TryGetBehind("a", out var kept) && ReferenceEquals(kept, liveA) && !controller.IsCurrent(generation),
            "commit activates the next tab, bumps the generation (work queued for the old one drops) and keeps the outgoing tab live behind");
        controller.TakeFront(b);
        controller.Finish(operation);
        controller.Finish(operation);
        expect(!controller.IsSwitching && switching.SequenceEqual([true, false]),
            "finish ends the operation once (a second finish does nothing) and announces switching on, then off");

        var stale = controller.Prepare("stale");
        controller.Finish(stale);
        var threw = false;
        try
        {
            controller.Commit(stale, c);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        expect(threw && controller.Active == b, "an operation that is no longer running cannot commit");

        var before = controller.Generation;
        var failing = controller.Prepare("switch to C");
        controller.Commit(failing, c);
        var returned = controller.Abort(c, returnTo: b);
        expect(returned && controller.Active == b && failing is { Aborted: true, To.Id: "b" } && controller.Generation == before + 2 && !controller.IsUnresumed(c),
            "a failed resume aborts back to the tab it came from, as one more activation");
        controller.Finish(failing);

        var lone = controller.Prepare("switch to C");
        controller.Commit(lone, c);
        expect(!controller.Abort(c, returnTo: null) && controller.IsUnresumed(c) && controller.Unresumed.Contains("c"),
            "with nowhere to return, the failed tab stays in front, marked to retry its resume");
        controller.Finish(lone);

        var reinstated = controller.Prepare("switch to B");
        controller.Commit(reinstated, b);
        controller.Reinstate(c);
        expect(controller.Active == c && controller.IsUnresumed(c) && reinstated.To == c && reinstated.Committed == controller.Generation,
            "reinstating puts the failed tab back in front, still marked, as the operation's result");
        controller.Finish(reinstated);

        var fresh = controller.TakeWorkOff(c, "New Project");
        expect(!controller.IsUnresumed(c) && controller.Active == fresh && fresh != c && controller.Generation == reinstated.Committed + 1,
            "new work in front of a failed tab goes to a fresh active tab, the failed one unmarked (activation resumes it from its file)");
        expect(controller.TryTakeBehind("a", out var taken) && ReferenceEquals(taken, liveA) && !controller.TryGetBehind("a", out _),
            "a live tab behind is taken once (closed or let go to its cache file)");

        var window = SourceProbe.Src("MainWindow.ProjectTabs.cs");
        expect(window.Contains("ProjectTabController<", StringComparison.Ordinal) && window.Contains(".Prepare(", StringComparison.Ordinal)
               && window.Contains(".Commit(", StringComparison.Ordinal) && window.Contains(".Finish(", StringComparison.Ordinal),
            "the window's tab switch goes through the controller's prepare, commit and finish");
        Directory.Delete(root, recursive: true);
    }

    // ----- color-match-bar-standalone -----

    private static void ColorMatchStandalone(Action<bool, string> expect)
    {
        var bar = Code(SourceProbe.Src("Controls", "ColorMatchBar.cs"));
        expect(typeof(MainWindow).GetInterfaces().Any(i => i.Name == "IColorMatchHost")
               && typeof(MainWindow).Assembly.GetType("HandPegApp.ColorMatchBar") is { } type && type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Single().GetParameters()[0].ParameterType.Name == "IColorMatchHost",
            "MainWindow implements IColorMatchHost and the bar is built with a host, not the window");
        expect(!System.Text.RegularExpressions.Regex.IsMatch(bar, @"\bMainWindow\b"), "the bar's code never names MainWindow (no private field reach-ins)");

        var studio = Code(SourceProbe.Src("MainWindow.ColorStudio.cs"));
        var match = Code(SourceProbe.Src("MainWindow.ColorMatch.cs"));
        expect(!studio.Contains("DispatcherTimer", StringComparison.Ordinal) && !match.Contains("DispatcherTimer", StringComparison.Ordinal)
               && !bar.Contains("DispatcherTimer", StringComparison.Ordinal),
            "no polling timer places the grade wipe (nor drives the bar)");
        var hook = SourceProbe.MemberOfType("MainWindow", "private void HookGradeWipeTriggers(");
        expect(hook.Contains("SizeChanged += ", StringComparison.Ordinal) && hook.Contains("PropertyChanged += ", StringComparison.Ordinal)
               && hook.Contains("AddSourceChangedHandler", StringComparison.Ordinal)
               && SourceProbe.MemberOfType("MainWindow", "private void QueueGradeWipe(").Contains("BeginInvoke(", StringComparison.Ordinal),
            "size, view-model, DPI and source changes each queue one placement");
    }

    // ----- decoder-yield-preemption -----

    private static void DecoderYield(Action<bool, string> expect)
    {
        expect(DecoderPool.YieldsToVisible(preRollOrHover: true, cutEntry: false, playing: false, visibleDecoding: true)
               && DecoderPool.YieldsToVisible(preRollOrHover: true, cutEntry: false, playing: true, visibleDecoding: true),
            "pre-roll and pointer work in flight yield to a visible chase, playing or paused");
        expect(DecoderPool.YieldsToVisible(false, cutEntry: true, playing: false, visibleDecoding: true) && !DecoderPool.YieldsToVisible(false, cutEntry: true, playing: true, visibleDecoding: true),
            "a cut's first frames yield only while paused (a cut ahead of playback keeps its lane)");
        expect(!DecoderPool.YieldsToVisible(false, false, false, true) && !DecoderPool.YieldsToVisible(true, true, false, visibleDecoding: false),
            "visible and play-ahead reads never yield, and nothing yields without a visible chase");

        expect(DecoderPool.ResumeAt(10, 12, 11, 0.04, covered: true) is var at && Math.Abs(at - 11.02) < 1e-9,
            "a parked read resumes half a frame past its last decoded frame");
        expect(DecoderPool.ResumeAt(10, 12, 11.99, 0.04, true) == 12, "never past its target");
        expect(DecoderPool.ResumeAt(10, 12, 11, 0.04, covered: false) == 10 && DecoderPool.ResumeAt(10, 12, 9.5, 0.04, true) == 10
               && DecoderPool.ResumeAt(10, 12, 12.5, 0.04, true) == 10 && DecoderPool.ResumeAt(10, 12, double.NaN, 0.04, true) == 10,
            "an invalidated resume (frames evicted, the range moved past it, no point) starts the range over");
        expect(DecoderPool.ResumeAt(10, 12, 11, double.NaN, true) == 11, "an unknown frame length resumes at the point itself");

        var lanes = SourceProbe.Src("Compositor", "DecoderPool.Lanes.cs");
        expect(SourceProbe.InOrder(lanes, "if (YieldsToVisibleNow(demand))", "ParkForVisible(demand, decoder);", "return;", "MoreUrgentWaiting(demand, decoder)"),
            "the read loop yields to a visible chase before its next frame, with no frame minimum, ahead of the 4-frame MoreUrgentWaiting rule");
        expect(SourceProbe.Member(lanes, "private bool VisibleBeingDecoded(").Contains("!demand.Done", StringComparison.Ordinal)
               && SourceProbe.Member(lanes, "private double StartOf(").Contains("ResumeAt(", StringComparison.Ordinal)
               && lanes.Contains("_parkedReads.Remove(demand);", StringComparison.Ordinal),
            "a finished chase frees the engine at once, a parked read resumes through ResumeAt, and a done demand drops its park");
    }

    // ----- touchpad-wheel-consumers -----

    private static void TouchpadWheelConsumers(Action<bool, string> expect)
    {
        var timeline = SourceProbe.Src("MainWindow.TimelineView.cs");
        expect(timeline.Contains("private readonly TouchpadWheel _timelineWheel", StringComparison.Ordinal),
            "the timeline rows classify with their own TouchpadWheel");
        var mask = SourceProbe.Src("Dialogs", "MaskDrawWindow.cs");
        expect(mask.Contains("TouchpadWheel", StringComparison.Ordinal) && MaskDrawWindow.TouchpadStreamMs == TouchpadWheel.CarryMs,
            "the Cutout Studio uses the same rule and carry-over");
        var surface = SourceProbe.Src("Controls", "VideoSurface.cs");
        expect(surface.Contains("TellWheel(", StringComparison.Ordinal), "the player raises its wheel through the same decoder");

        var wheel = new TouchpadWheel();
        expect(wheel.Classify(30, true, 0) && wheel.Classify(120, false, 100) && !wheel.Classify(120, false, 400),
            "a touchpad gesture carries whole notches for 250 ms, then a wheel is a mouse's again");
        expect(TouchpadWheel.Decode(TouchpadWheel.WmMouseWheel, (-120L << 16) & 0xFFFF0000, 0, ModifierKeys.None, false) is { Delta: -120, Horizontal: false },
            "a downward notch decodes to -120");
    }

    // ----- voice-scene-solver -----

    private static void VoiceScene(Action<bool, string> expect, StringBuilder text)
    {
        // Two mics, 20 ms hops, 30 s: A talks 1-9 s, B 10-18 s, both 20-23 s (crosstalk), silence after. Bleed of each voice into the other
        // mic sits 15 dB under it.
        const double hop = 0.02;
        var hops = (int)(30 / hop);
        var (a, b) = (new float[hops], new float[hops]);
        for (var h = 0; h < hops; h++)
        {
            var t = h * hop;
            var aTalks = t is >= 1 and < 9 or >= 20 and < 23;
            var bTalks = t is >= 10 and < 18 or >= 20 and < 23;
            a[h] = aTalks ? -18f : bTalks ? -33f : -62f + (h % 7) * 0.3f;
            b[h] = bTalks ? -20f : aTalks ? -35f : -64f + (h % 5) * 0.3f;
        }

        float[][] levels = [a, b];
        var settings = new VoiceSceneSettings();
        var shots = VoiceSceneSolver.Solve(levels, settings);
        foreach (var shot in shots)
            text.Append(CultureInfo.InvariantCulture, $"  {shot.Start,6:0.00} - {shot.End,6:0.00} s  {shot.Kind}{(shot.Mic >= 0 ? " mic " + shot.Mic : "")}\n");

        expect(shots.Count > 0 && shots[0].Start == 0 && Math.Abs(shots[^1].End - hops * hop) < 1e-9
               && shots.Zip(shots.Skip(1)).All(p => Math.Abs(p.First.End - p.Second.Start) < 1e-9 && p.First.End > p.First.Start),
            "the shots cover the analysed span end to end, in order, with no gap or overlap");
        expect(shots.Any(s => s is { Kind: VoiceShotKind.Speaker, Mic: 0 }) && shots.Any(s => s is { Kind: VoiceShotKind.Speaker, Mic: 1 }),
            "each talker gets their own shot");
        var toB = shots.FirstOrDefault(s => s is { Kind: VoiceShotKind.Speaker, Mic: 1 });
        expect(toB.End > 0 && toB.Start <= 10 - settings.LeadSeconds + hop && toB.Start >= 10 - settings.LeadSeconds - 2 * hop,
            "the cut to the new speaker lands its lead before their first word");
        expect(shots.Any(s => s.Kind == VoiceShotKind.Wide && s.Start < 23 && s.End > 20.6), "crosstalk past the overlap time goes Wide");
        expect(shots.Where(s => s.Kind != VoiceShotKind.Reaction).SkipLast(1).All(s => s.Length >= settings.MinShotSeconds - 1e-9),
            "no shot but a reaction (or the last) is shorter than the minimum shot");
        expect(shots.Count(s => s.Start > 23.5) == 0 || shots.Last().Start < 23.5 + settings.TailSeconds + hop,
            "silence never cuts: the last speech's shot holds to the end");
        expect(VoiceSceneSolver.Solve(levels, settings).SequenceEqual(shots) && VoiceSceneSolver.Solve([], settings).Count == 0,
            "pure and deterministic; no mics, no shots");
        var facecams = VoiceSceneSolver.Solve(levels, settings with { Mode = VoiceSceneMode.Facecams, ReactionAfterSeconds = 2 });
        expect(facecams.All(s => s.Kind != VoiceShotKind.Reaction), "facecam mode makes no reaction cutaways");
    }

    // ----- cutout-tight-bounds -----

    private static void CutoutTightBounds(Action<bool, string> expect)
    {
        expect(MaskStore.BoundsOfOutlines("0.2,0.3 0.6,0.25 0.5,0.7; 0.1,0.5 0.15,0.55 0.12,0.6") == new SourceBounds(0.1, 0.25, 0.6, 0.7),
            "drawn outlines give the bounds of every vertex of every outline");
        expect(MaskStore.BoundsOfOutlines("") is null && MaskStore.BoundsOfOutlines(null) is null && MaskStore.BoundsOfOutlines("0.3,0.3 0.3,0.3 0.3,0.3") is null,
            "no outline, or one with no area, gives no bounds");

        var pixels = new byte[10 * 10];
        pixels[2 * 10 + 3] = 255;
        pixels[7 * 10 + 8] = MaskStore.VisibleLevel;
        pixels[9 * 10 + 9] = MaskStore.VisibleLevel - 1;
        expect(MaskStore.VisibleBounds(pixels, 10, 10) == new SourceBounds(0.3, 0.2, 0.9, 0.8),
            "a mask picture's bounds are its pixels at the visible level or above (faint fringe left out), as picture fractions");
        expect(MaskStore.VisibleBounds(new byte[16], 4, 4) is null && MaskStore.VisibleBounds(new byte[3], 4, 4) is null,
            "an empty or short coverage map gives no bounds");

        var layer = new Layer { SourceX = 0.25, SourceY = 0, SourceWidth = 0.5, SourceHeight = 1, TightBounds = new SourceBounds(0.25, 0.2, 0.5, 0.6), AutoCenterAnchor = true };
        expect(layer.TightBoxBounds() is { } box && Math.Abs(box.UMin) < 1e-9 && Math.Abs(box.UMax - 0.5) < 1e-9 && layer.TightAnchor() is { } centre
               && Math.Abs(centre.X - 0.25) < 1e-9 && Math.Abs(centre.Y - 0.4) < 1e-9,
            "the bounds land on the box as fractions of what it shows, and the auto anchor is their centre");
        expect(SourceProbe.Src("ViewModels", "MainViewModel.Cutouts.cs").Contains("MaskStore.TightBoundsOf(layer)", StringComparison.Ordinal),
            "a masked layer's tight bounds follow its mask");
    }

    // ----- gizmo-8-handle-snapping -----

    private static void GizmoSnapping(Action<bool, string> expect)
    {
        var anchor = new Point(100, 100);
        expect(GizmoHitRules.AnchorTakes(new Point(103, 97), anchor, 80, 80) && !GizmoHitRules.AnchorTakes(new Point(104, 100), anchor, 80, 80)
               && !GizmoHitRules.AnchorTakes(anchor, anchor, 35, 200),
            "the anchor takes a press only within its 6 x 6 square, and never on a box under 36 DIP");
        expect(GizmoHitRules.TurnAngle(52, stepped: true) == 45 && GizmoHitRules.TurnAngle(200, false) == -160 && GizmoHitRules.TurnAngle(-180, false) == 180
               && GizmoHitRules.TurnAngle(double.NaN, false) == 0 && GizmoHitRules.TurnAngle(12.345, false) == 12.3,
            "Shift turns in 45 degree steps; angles wrap to -180..180 at a tenth of a degree");
        expect(GizmoHitRules.CornerKeepsShape(ModifierKeys.None) && !GizmoHitRules.CornerKeepsShape(ModifierKeys.Shift), "a corner keeps the shape until Shift frees it");
        expect(GizmoHitRules.Snaps(true, false, 100) && !GizmoHitRules.Snaps(true, false, 200) && !GizmoHitRules.Snaps(true, true, 10) && !GizmoHitRules.Snaps(false, false, 10),
            "snapping is magnetic only slower than 180 DIP/s, with the setting on and no free key");

        var gate = new SnapGate();
        var first = gate.Note(new Point(0, 0), 1000);
        var fast = gate.Note(new Point(10, 0), 1010);
        var paused = new SnapGate();
        paused.Note(new Point(0, 0), 0);
        var afterPause = paused.Note(new Point(20, 0), 5000);
        expect(first == 0 && Math.Abs(fast - 600) < 1e-9 && afterPause < GizmoHitRules.SnapBelowDipsPerSecond,
            "the gate's first move is slow, 10 DIP in 10 ms reads 600 DIP/s (smoothed), and a pause reads slow");

        var moving = new SnapRect(0.297, 0.6, 0.2, 0.2);
        var result = LayoutSnap.Move(moving, [], 0.01, 0.01, 1920, 1080);
        expect(Math.Abs(result.DeltaX - 0.003) < 1e-9 && result.Guides.Count > 0,
            "a box whose centre is near the frame's centre line snaps onto it within reach, with a guide");
        var beside = new SnapRect(0.5, 0.405, 0.2, 0.2);
        var toBox = LayoutSnap.Move(new SnapRect(0.1, 0.41, 0.2, 0.2), [beside], 0.01, 0.01, 1920, 1080);
        expect(Math.Abs(toBox.DeltaY - -0.005) < 1e-9 && toBox.Guides.Count > 0 && toBox.SameSize.Count == 2,
            "a box nearer another's top line than the frame's centre snaps to it, shows a guide, and marks the two as the same size");
        expect(LayoutSnap.Move(moving, [], 0, 0.01, 1920, 1080) == SnapResult.None, "no reach, no snap");
        expect(LayoutSnap.MatchSize(0.248, [0.25, 0.3], 0.005) == 0.25 && LayoutSnap.MatchSize(0.2, [0.25], 0.005) is null,
            "a resize snaps its size to another layer's within reach");
        expect(GizmoHitRules.EdgeHandleMinSide == 30 && SourceProbe.Src("Controls", "LayoutGizmos.cs").Contains("GizmoHitRules", StringComparison.Ordinal),
            "the gizmo's handles follow these rules (edge handles only on sides of 30 DIP or more)");
    }

    // Code without its comments (a type named in a comment is not a reach-in).
    private static string Code(string source) =>
        string.Join('\n', source.Split('\n').Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line));
}
