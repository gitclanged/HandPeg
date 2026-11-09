using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Services;
using Pose = HandPegApp.Services.GpuPreviewEngine.CameraPose;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4D (Subagent C): each project tab's own 3D view and camera pose, and its own window layout.
/// <para>project-tabs-exploded-pose: a tab header's 3D view and camera (and layout) round-trip, a header from 5.4A to 5.4C reads flat at
/// the view's start pose with no layout, a duplicate takes them all; the engine's SetExplodedInstant has no transition (the camera drawn is
/// where it is going at once, both ways, held to the camera's limits) where SetExploded has one; a still as it is shown is in the player's
/// shape (GpuPreviewEngine.ShownStillSize); and the window's code: StopPlayingForTabs no longer closes the 3D view, a tab is captured
/// before it stops and its still asShown, the view is left at once before the engine lets go, a tab comes back in it (the Live Encoded
/// Preview off first) before its player opens, the still is never hidden under the 3D view, a close leaves it at once. The 5.4D review:
/// a tab put behind before its view was restored (or while its engine had not started) keeps its header's 3D view and camera
/// (TakeExplodedState), a tab in front that could not be brought back keeps its header's window, a refused switch keeps the wait, an
/// engine that starts late brings the tab into its 3D view, and a thumbnail's request carries what it is drawn as.</para>
/// <para>project-tabs-window-layouts: a headless DockManager (the main window's panes as stand-ins) with a Pane Docker holding Media: tab
/// A's layout kept, layout B laid out, A comes back exactly (the side window as it was, every time); the same layout again is no load; a
/// difference of the tabs in front alone comes back too (Load, never Apply); with the setting off nothing is kept or laid out. The window
/// lays a tab's layout out on a switch, a take-back and a close only (never at start-up, as tabs left behind are taken in, or for a new or
/// opened tab), and keeps the selector's preview out of it (5.4D review: also as the layout is laid out); with the layout, the Media
/// Browser's view (a Vertical layout's Filmstrip, 5.4D review); the setting is on as HandPeg comes, and is in Settings (General, Appearance)
/// as well as the selector; the Settings window's title-bar progress is the editor's (a 16 % wash over everything, a 2 DIP rail).</para>
/// </summary>
public static class TabWindowStateTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly IReadOnlyList<string> TabIds = ["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];
    private static readonly string[] PaneIds = ["Player", "Playback", "Timeline", "UnifiedTimeline", "Layout", "Header", "Media", "Keyframes", "Effects", "Meter", "Presets"];

    /// <summary>The window-free cases (and those on a window of their own), before the UI-scale tests.</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("project-tabs-exploded-pose", "# Each project tab keeps its 3D Exploded View and camera: in its header, restored at once with no transition, its still as it was shown.", ExplodedPose);
        yield return Case("project-tabs-window-layouts", "# Each project tab keeps its own main-window layout (AppSettings.PerTabWindowLayouts): laid out again on a switch, the side windows the window's.", WindowLayouts);
    }

    /// <summary>The cases that need the WPF Application the UI-scale tests make: run last, after header-flyouts-placement.</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
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

    /// <summary>The repository's folder (where HandPegApp.csproj is), found upwards from the built program.</summary>
    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A source file under src, its line endings made \n.</summary>
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), "src", .. parts])).ReplaceLineEndings("\n");

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

    private static string Describe(Pose pose) => F($"({pose.Yaw:0.##}, {pose.Pitch:0.##}, {pose.Zoom:0.###}, {pose.Spacing:0.###})");

    // ----- project-tabs-exploded-pose -----

    private static void ExplodedPose(Action<bool, string> expect)
    {
        Header(expect);
        Engine(expect);
        Still(expect);
        Window(expect);
    }

    private static void Header(Action<bool, string> expect)
    {
        var fresh = new ProjectTabHeader { Id = "fresh" };
        expect(fresh is { ExplodedOpen: false, ExplodedYaw: GpuPreviewEngine.DefaultYaw, ExplodedPitch: GpuPreviewEngine.DefaultPitch, ExplodedZoom: GpuPreviewEngine.DefaultZoom, ExplodedSpacing: GpuPreviewEngine.DefaultSpacing, LayoutJson: null },
            F($"a new header is flat, at the view's start pose ({GpuPreviewEngine.DefaultYaw:0}, {GpuPreviewEngine.DefaultPitch:0}, {GpuPreviewEngine.DefaultZoom:0.##}, {GpuPreviewEngine.DefaultSpacing:0.##}), with no layout"));

        var layout = JsonSerializer.Serialize(MainWindow.StandardLayout(TabIds), AppSettingsJsonContext.Default.DockLayoutState);
        var header = new ProjectTabHeader { Id = "tab-3d", Title = "Orbit", Dirty = true, Empty = false, FrameWidth = 1920, FrameHeight = 1080, FrameRate = 60 };
        (header.ExplodedOpen, header.ExplodedYaw, header.ExplodedPitch, header.ExplodedZoom, header.ExplodedSpacing) = (true, -42.5, 22.25, 2.6, 0.31);
        (header.LayoutJson, header.MediaView) = (layout, "Filmstrip");
        var json = ProjectTabs.HeaderJson(header);
        expect(ProjectTabs.ReadHeader(json) == header && json.Contains("\"MediaView\":\"Filmstrip\"", StringComparison.Ordinal),
            "a header in the 3D view at (-42.5, 22.25, 2.6, 0.31) with Standard Editing's layout and the Media Browser's Filmstrip reads back as it was (record equality)");
        expect(json.Contains("\"ExplodedOpen\":true", StringComparison.Ordinal) && json.Contains("\"ExplodedYaw\":-42.5", StringComparison.Ordinal) && !json.Contains("ExplodedPan", StringComparison.Ordinal),
            "the camera is its turn, distance and spacing (the 3D camera has no pan)");

        var older = "{\"Id\":\"old\",\"Title\":\"From 5.4C\",\"ProjectPath\":\"\",\"Dirty\":true,\"Empty\":false,\"FrameWidth\":1920,\"FrameHeight\":1080,\"FrameRate\":30}";
        expect(ProjectTabs.ReadHeader(older) is { ExplodedOpen: false, ExplodedYaw: GpuPreviewEngine.DefaultYaw, ExplodedPitch: GpuPreviewEngine.DefaultPitch, ExplodedZoom: GpuPreviewEngine.DefaultZoom, ExplodedSpacing: GpuPreviewEngine.DefaultSpacing, LayoutJson: null, MediaView: null, FrameWidth: 1920 },
            "a header written by 5.4A to 5.4C reads flat, at the view's start pose, with no layout and no Media Browser view (the tab takes the window as it is)");

        var copy = new ProjectTabHeader { Id = "copy" };
        copy.TakeWindowStateOf(header);
        expect(copy is { ExplodedOpen: true, ExplodedYaw: -42.5, ExplodedPitch: 22.25, ExplodedZoom: 2.6, ExplodedSpacing: 0.31, MediaView: "Filmstrip" } && copy.LayoutJson == layout && copy.Title == "",
            "a duplicate takes the window's part (TakeWindowStateOf: the 3D view, its camera, the layout and the Media Browser's view) and nothing else");

        var project = typeof(ProjectState).GetProperties().Select(p => p.Name).ToList();
        expect(!project.Any(n => n.StartsWith("Exploded", StringComparison.Ordinal) || n == "LayoutJson"),
            "none of it is in the project file (ProjectState): orbiting or a splitter is no change to the project");

        Waiting(expect);
    }

    /// <summary>The 5.4D review's R16/R17: what a tab put behind is taken as while the 3D view it came back in is not up yet.</summary>
    private static void Waiting(Action<bool, string> expect)
    {
        static ProjectTabHeader InThreeD(string id) => new() { Id = id, ExplodedOpen = true, ExplodedYaw = -30, ExplodedPitch = 12, ExplodedZoom = 1.8, ExplodedSpacing = 0.4 };

        var b = InThreeD("b");
        MainWindow.TakeExplodedState(b, null, waiting: b);
        expect(b is { ExplodedOpen: true, ExplodedYaw: -30, ExplodedPitch: 12, ExplodedZoom: 1.8, ExplodedSpacing: 0.4 },
            "a tab put behind before its 3D view was restored (a held Ctrl+Tab, an engine not started yet: it is the tab waiting) keeps its header's view and camera");

        var copy = new ProjectTabHeader { Id = "copy" };
        MainWindow.TakeExplodedState(copy, null, waiting: b);
        expect(copy is { ExplodedOpen: true, ExplodedYaw: -30, ExplodedPitch: 12, ExplodedZoom: 1.8, ExplodedSpacing: 0.4 },
            "its duplicate made meanwhile takes the camera it came back with");

        var orbit = new Pose(55, -20, 3.1, 0.22);
        var shown = InThreeD("shown");
        MainWindow.TakeExplodedState(shown, orbit, waiting: shown);
        expect(shown is { ExplodedOpen: true, ExplodedYaw: 55, ExplodedPitch: -20, ExplodedZoom: 3.1, ExplodedSpacing: 0.22 },
            F($"the view up: the engine's target pose {Describe(orbit)}, whatever waits"));

        var flat = InThreeD("flat");
        MainWindow.TakeExplodedState(flat, null, waiting: null);
        expect(flat is { ExplodedOpen: false, ExplodedYaw: -30 }, "neither up nor waiting: flat (its last camera kept, unused)");
    }

    /// <summary>An engine with only its camera's state (no worker, no surface): what SetExploded, SetExplodedInstant and TargetPose touch.</summary>
    private static GpuPreviewEngine CameraOnly()
    {
        var engine = (GpuPreviewEngine)RuntimeHelpers.GetUninitializedObject(typeof(GpuPreviewEngine));
        var type = typeof(GpuPreviewEngine);
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        type.GetField("_camera", hidden)!.SetValue(engine, new object());
        var scene = type.GetField("_lastExploded", hidden)!;
        scene.SetValue(engine, Activator.CreateInstance(scene.FieldType, nonPublic: true));
        type.GetField("_dirty", hidden)!.SetValue(engine, new AutoResetEvent(false));
        return engine;
    }

    private static Pose DrawnOf(GpuPreviewEngine engine) =>
        (Pose)typeof(GpuPreviewEngine).GetProperty("Drawn", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;

    private static double? ClockOf(GpuPreviewEngine engine) =>
        (double?)typeof(GpuPreviewEngine).GetField("_transitionClock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine);

    private static void Engine(Action<bool, string> expect)
    {
        var orbit = new Pose(-42.5, 22.25, 2.6, 0.31);
        var engine = CameraOnly();
        engine.SetExplodedInstant(true, orbit);
        expect(engine.IsExploded && engine.IsDrawingExploded && engine.TargetPose == orbit && DrawnOf(engine) == orbit && ClockOf(engine) is null,
            F($"SetExplodedInstant(true, {Describe(orbit)}): on and drawing in 3D, the camera drawn where it is going ({Describe(DrawnOf(engine))}), no transition under way"));

        engine.SetExplodedInstant(false);
        expect(!engine.IsExploded && !engine.IsDrawingExploded && DrawnOf(engine) == Pose.Flat && ClockOf(engine) is null && engine.TargetPose == orbit,
            "SetExplodedInstant(false): off and flat at once (no way out), the camera's own pose kept for the tab");

        var wild = CameraOnly();
        wild.SetExplodedInstant(true, new Pose(200, -120, 50, double.NaN));
        expect(wild.TargetPose == new Pose(85, -85, 8, GpuPreviewEngine.DefaultSpacing) && DrawnOf(wild) == wild.TargetPose,
            F($"a pose beyond the camera's limits is held to them: {Describe(wild.TargetPose)} (the turns short of edge on, the distance at most 8, a spacing that is not a number the view's start)"));
        var none = CameraOnly();
        none.SetExplodedInstant(true);
        expect(none.TargetPose == new Pose(GpuPreviewEngine.DefaultYaw, GpuPreviewEngine.DefaultPitch, GpuPreviewEngine.DefaultZoom, GpuPreviewEngine.DefaultSpacing),
            "with no pose, the view's start");

        var animated = CameraOnly();
        animated.SetExploded(true);
        expect(animated.IsExploded && DrawnOf(animated) == Pose.Flat && ClockOf(animated) is 0.0,
            "where SetExploded (the toggle) starts its 240 ms way in from the flat picture: a transition under way, the camera drawn flat");
    }

    private static void Still(Action<bool, string> expect)
    {
        var cases = new (int ViewW, int ViewH, int CanvasW, int CanvasH, int W, int H, string What)[]
        {
            (1600, 900, 1080, 1920, 960, 540, "a 1600 x 900 player (a vertical canvas in it): 960 x 540, the player's shape, not the canvas's"),
            (700, 1000, 1920, 1080, 540, 772, "a 700 x 1000 player: 540 x 772"),
            (300, 200, 1920, 1080, 300, 200, "a 300 x 200 player: its own size (never larger than the player)"),
            (4, 4, 1080, 1920, 540, 960, "a player too small to draw on: the canvas's shape, 540 x 960"),
            (8000, 100, 1920, 1080, 4000, 50, "an 8000 x 100 player: 4000 x 50 (the long side at most 4096)"),
        };
        foreach (var (viewW, viewH, canvasW, canvasH, w, h, what) in cases)
        {
            var size = GpuPreviewEngine.ShownStillSize(viewW, viewH, canvasW, canvasH, 540);
            expect(size == (w, h) && size.Width % 2 == 0 && size.Height % 2 == 0, F($"a still as it is shown, short side 540: {what} (got {size.Width} x {size.Height})"));
        }

        var engine = Read("Services", "GpuPreviewEngine.cs");
        var capture = Method(engine, "private void CaptureThumbnail(");
        expect(engine.Contains("CaptureThumbnailAsync(int shortSide = ProjectThumbnail.ShortSide, bool asShown = false)", StringComparison.Ordinal)
               && InOrder(capture, "plan.Evaluate(at, ref header, layers);", "if (asked.AsShown && ShownPose() is { } pose)", "ShownStillSize(", "FrameSceneHeader.FlagLetterbox", "WriteCameraAt(ref header, layers, pose, plan);", "else", "ProjectThumbnail.ThumbnailSize(header.CanvasWidth, header.CanvasHeight"),
            "CaptureThumbnailAsync(..., asShown): in the 3D view the still is drawn with the camera where it is going, in the player's shape; otherwise the canvas's, flat as before");
        var ask = Method(engine, "public Task<(byte[] Bgra, int Width, int Height)?> CaptureThumbnailAsync(");
        expect(engine.Contains("private sealed record ThumbnailRequest(TaskCompletionSource<(byte[] Bgra, int Width, int Height)?> Done, GpuScenePlan? Plan, int ShortSide, bool AsShown);", StringComparison.Ordinal)
               && ask.Contains("Interlocked.Exchange(ref _thumbnail, new ThumbnailRequest(request, unfaded, Math.Max(shortSide, 16), asShown))?.Done.TrySetResult(null);", StringComparison.Ordinal)
               && capture.Contains("private void CaptureThumbnail(ThumbnailRequest asked)", StringComparison.Ordinal) && InOrder(capture, "asked.Plan ?? Volatile.Read(ref _plan)", "asked.ShortSide", "asked.ShortSide")
               && !engine.Contains("_thumbnailAsShown", StringComparison.Ordinal) && !engine.Contains("_thumbnailShortSide", StringComparison.Ordinal) && !engine.Contains("_thumbnailPlan", StringComparison.Ordinal),
            "a thumbnail's request carries what it is drawn as (5.4D review: one record exchanged at once, read only from it): a save's thumbnail taken by the pump is never drawn as a tab's 3D still asks, nor that still flat");
        var exploded = Read("Services", "GpuPreviewEngine.Exploded.cs");
        expect(InOrder(Method(exploded, "partial void WriteCamera("), "StepCamera();", "WriteStack(ref header, layers, Drawn, plan);", "_lastExploded.Take(")
               && !Method(exploded, "private void WriteCameraAt(").Contains("StepCamera", StringComparison.Ordinal) && !Method(exploded, "private void WriteCameraAt(").Contains("_lastExploded", StringComparison.Ordinal),
            "the player's frames and the still lay the stack out alike (WriteStack); the still neither steps the camera nor is kept for picking");
    }

    private static void Window(Action<bool, string> expect)
    {
        var tabs = Read("MainWindow.ProjectTabs.cs");
        var exploded = Read("MainWindow.Exploded.cs");

        var stop = Method(tabs, "private void StopPlayingForTabs()");
        expect(stop.Length > 0 && !stop.Contains("SetExplodedView(", StringComparison.Ordinal) && InOrder(stop, "if (_explodedUi)", "EndExplodedDrag(cancelled: true);", "_gpu?.LiftLayer(0, 0);"),
            "StopPlayingForTabs no longer closes the 3D view: it ends a drag or a lift only");

        // 5.4D-TabPerf: SuspendActiveAsync is PutActiveBehind, at once and never refused (the tab is kept live in memory); its still is the
        // one kept fresh ahead (MainWindow.TabStill.cs), taken as it is shown.
        var suspend = Method(tabs, "private ProjectTabHeader PutActiveBehind(");
        expect(InOrder(suspend, "var tab = _tabs.Active;", "CaptureWindowStateForTab(tab);", "StopPlayingForTabs();", "if (_viewModel.HoldsNothingForTab)", "LeaveExplodedAtOnce();", "return tab;"),
            "PutActiveBehind captures the tab's window first (before it stops, and before an empty tab returns, which leaves the view too)");
        var held = suspend.IndexOf("if (_viewModel.HoldsNothingForTab)", StringComparison.Ordinal);
        expect(held > 0 && !suspend[..held].Contains("_explodedWaitingTab = null;", StringComparison.Ordinal) && !suspend.Contains("await ", StringComparison.Ordinal)
               && InOrder(suspend, "if (_viewModel.HoldsNothingForTab)", "_explodedWaitingTab = null;", "LeaveExplodedAtOnce();", "return tab;", "var live = DetachActive(tab, out write);", "_explodedWaitingTab = null;", "LeaveExplodedAtOnce();", "_gpu?.Suspend();"),
            "the wait for a tab's player or engine is let go only as the tab goes behind (5.4D review), which is at once and never refused (5.4D-TabPerf: nothing awaited)");
        var still = Method(Read("MainWindow.TabStill.cs"), "private async Task TakeLiveStillAsync(");
        expect(InOrder(still, "var mark = new StillMark(_viewModel.UndoMark, _viewModel.PositionMs, _explodedUi);", "engine.CaptureThumbnailAsync(StillShortSide, asShown: mark.Shown3D)")
               && InOrder(Method(tabs, "private TabViewContext DetachActive("), "var (still, stillMark) = (_liveStill, _liveStillMark);", "still is not null && !stillMark.Shown3D ? still : null")
               && InOrder(suspend, "var live = DetachActive(tab, out write);", "LeaveExplodedAtOnce();", "_gpu?.Suspend();"),
            "its still is captured as it is shown (the 3D picture), kept fresh ahead, never made the project's thumbnail, and the view is left at once right before the engine lets go");

        var capture = Method(tabs, "private void CaptureWindowStateForTab(");
        expect(InOrder(Method(tabs, "private void EndLayoutPreview()"), "_layoutHover.Close();", "RestoreLayoutBeforePreview();")
               && InOrder(capture, "EndLayoutPreview();", "if (_unresumed.Contains(tab.Id))", "return;", "tab.LayoutJson = TabLayoutOf(_dock, perTab);", "tab.MediaView = TabMediaViewOf(MediaBrowser.ViewMode, perTab);",
                   "TakeExplodedState(tab, _explodedUi && _gpu is { } engine ? engine.TargetPose : null, _explodedWaitingTab);"),
            "CaptureWindowStateForTab: the selector closed and its preview put back before the layout is taken; nothing of a tab in front that could not be brought back (5.4D review: its header keeps its window); the layout, the Media Browser's view, the 3D view and the engine's target pose");
        expect(Method(tabs, "private void TakeHeaderForTab(") is { Length: > 0 } header && !header.Contains("CaptureWindowStateForTab", StringComparison.Ordinal),
            "never in TakeHeaderForTab (the header's project part only)");

        var view = Method(tabs, "private void RestoreTabView(");
        expect(InOrder(view, "RestoreExplodedForTab(tab);", "OpenInPlayer((long)at, paused: true);"), "RestoreTabView restores the 3D view before the player opens");
        expect(InOrder(Method(tabs, "private async Task<bool> ResumeTabAsync("), "DeleteTabFiles(id);", "if (tab.ExplodedOpen)", "_explodedWaitingTab = tab;", "Dispatcher.BeginInvoke(() => RestoreTabView(tab), DispatcherPriority.Background);")
               && InOrder(Method(tabs, "private bool AttachHydrated("), "DeleteTabFiles(tab.Id);", "if (tab.ExplodedOpen)", "_explodedWaitingTab = tab;", "RestoreTabView(tab);")
               && InOrder(view, "if (ReferenceEquals(tab, _tabs.Active) && ReferenceEquals(_explodedWaitingTab, tab))", "_explodedWaitingTab = null;", "HideTabStill();", "return;", "RestoreExplodedForTab(tab);"),
            "a tab brought back in the 3D view waits for its view to be restored (5.4D review): put behind again before then, it keeps its header's 3D view; in front with nothing to show, it is flat");
        var restore = Method(tabs, "private void RestoreExplodedForTab(");
        expect(InOrder(restore, "_explodedWaitingTab = null;", "if (!tab.ExplodedOpen || _explodedUi || !_viewModel.HasSource)", "if (_gpu is null)", "_explodedWaitingTab = tab;", "return;", "if (!VideoView.IsVisible)", "if (_viewModel.IsExportQualityPreview)",
                   "_viewModel.IsExportQualityPreview = false;", "SetExplodedView(true, instant: true, new(tab.ExplodedYaw, tab.ExplodedPitch, tab.ExplodedZoom, tab.ExplodedSpacing));"),
            "RestoreExplodedForTab: only with a picture to show, waiting once for an engine not started yet (5.4D review) or a player not shown yet, the Live Encoded Preview turned off first (the tab's 3D view wins)");
        var started = Method(Read("MainWindow.GpuPreview.cs"), "private async Task StartGpuAsync(");
        expect(InOrder(Method(tabs, "private void RestoreExplodedOnEngine()"), "if (_explodedWaitingTab is { } tab && ReferenceEquals(tab, _tabs.Active))", "RestoreExplodedForTab(tab);")
               && InOrder(started, "_gpu = engine;", "UpdateVideoVisibility();", "if (_viewModel.HasSource)", "RestoreExplodedOnEngine();", "GpuOpen(start, paused);"),
            "the engine started late (Law 2's first video, after a fault or Reload Player): the tab in front waiting for it goes into its 3D view before its player opens (5.4D review)");

        var set = Method(exploded, "private void SetExplodedView(bool on, bool instant = false, GpuPreviewEngine.CameraPose? pose = null)");
        var instantOn = set.IndexOf("if (instant)", StringComparison.Ordinal);
        var byHand = set.IndexOf("engine.ResetCamera();", StringComparison.Ordinal);
        expect(instantOn > 0 && byHand > instantOn && InOrder(set[instantOn..byHand], "engine.SetExplodedInstant(true,", "ShowExplodedLayer();", "return;")
               && InOrder(set, "engine.ResetCamera();", "engine.SetExploded(true);", "else", "if (instant)", "_gpu?.SetExplodedInstant(false);", "HideExplodedLayer();"),
            "SetExplodedView(on, instant, pose): a tab's way in sets its pose at once (no ResetCamera); turned on by hand the camera still starts where it always does; the instant way out hides the layer at once");
        expect(InOrder(Method(exploded, "private void ShowExplodedLayer()"), "ReferenceEquals(child, ExplodedLayer) || ReferenceEquals(child, _tabStill)", "continue;"),
            "ShowExplodedLayer never hides a tab's still (the overlay children it dims skip it)");

        var close = Method(tabs, "private async Task CloseProjectTabAsync(");
        expect(InOrder(close, "if (!_viewModel.NewProject())", "LeaveExplodedAtOnce();", "StopPlayingForTabs();", "LeaveExplodedAtOnce();", "_gpu?.Suspend();"),
            "closing the tab in front leaves the 3D view at once (the last tab emptied, or the next one brought forward)");
        var duplicate = Method(tabs, "private async Task DuplicateProjectTabAsync(");
        expect(InOrder(duplicate, "copy.TakeWindowStateOf(tab);", "CaptureWindowStateForTab(copy);"), "a duplicate takes its original's window, from its header behind or from the window in front");
    }

    // ----- project-tabs-window-layouts -----

    /// <summary>A dock manager with the main window's panes as plain stand-ins (SideWindowTests' pattern).</summary>
    private static DockManager NewDock()
    {
        var dock = new DockManager(new Window(), new ContentControl(), () => MainWindow.StandardLayout(TabIds));
        foreach (var id in PaneIds.Concat(TabIds))
        {
            var content = new Border { Child = new TextBlock { Text = id } };
            dock.Register(new DockPane
            {
                Id = id, Title = id, Content = () => content, Handle = id.StartsWith("Tab:", StringComparison.Ordinal) ? PaneHandle.Tab : PaneHandle.TitleSlot,
                MinWidth = () => 160, MinHeight = 60,
            });
        }

        return dock;
    }

    private static string Shape(DockManager dock) => DockManager.Shape(DockLayoutState.Read(dock.Save())!.Main);

    private static void WindowLayouts(Action<bool, string> expect)
    {
        var dock = NewDock();
        var docker = SideWindowStates.Write([new SideWindowState { Id = "side-1", Mode = SideWindowState.PanesMode, Tree = DockNode.Group("Media") }]);
        dock.Load(JsonSerializer.Serialize(MainWindow.StandardLayout(TabIds), AppSettingsJsonContext.Default.DockLayoutState), sideWindows: docker);
        var sides = dock.SaveSideWindows();
        var standard = Shape(dock);

        var tabA = new ProjectTabHeader { Id = "a" };
        tabA.LayoutJson = MainWindow.TabLayoutOf(dock, perTab: true);
        expect(tabA.LayoutJson == dock.Save() && DockLayoutState.Read(tabA.LayoutJson)!.Main.GroupOf("Media") is null,
            "tab A put behind keeps the main window's layout (DockManager.Save): Standard Editing, without Media (the Pane Docker's)");

        // Tab B's own layout (Assembly & Media, as chosen while it was in front).
        dock.Apply(dock.Prepared(MainWindow.AssemblyLayout(TabIds)));
        var tabB = new ProjectTabHeader { Id = "b", LayoutJson = MainWindow.TabLayoutOf(dock, perTab: true) };
        var assembly = Shape(dock);
        expect(assembly != standard && dock.SaveSideWindows() == sides, "tab B chose Assembly & Media: another shape, the side window as it was");

        expect(MainWindow.LoadTabLayout(dock, tabA, perTab: true) && dock.Save() == tabA.LayoutJson && Shape(dock) == standard && dock.SaveSideWindows() == sides,
            "A back to the front: its layout laid out again exactly (the same JSON), the Pane Docker still holding Media");
        expect(MainWindow.LoadTabLayout(dock, tabB, perTab: true) && Shape(dock) == assembly && dock.SaveSideWindows() == sides,
            "B back to the front: Assembly & Media again, the side window untouched");
        expect(!MainWindow.LoadTabLayout(dock, tabB, perTab: true) && Shape(dock) == assembly, "the window's own layout again: no load at all");

        // Only the tabs in front differ: Load binds them again (Apply would skip the same signature and lose them).
        var shown = DockLayoutState.Read(dock.Save())!;
        var group = shown.Main.GroupOf("Tab:Audio")!;
        var was = group.Active;
        group.Active = "Tab:Audio";
        var tabC = new ProjectTabHeader { Id = "c", LayoutJson = JsonSerializer.Serialize(shown, AppSettingsJsonContext.Default.DockLayoutState) };
        expect(was != "Tab:Audio" && MainWindow.LoadTabLayout(dock, tabC, perTab: true) && DockLayoutState.Read(dock.Save())!.Main.GroupOf("Tab:Audio")!.Active == "Tab:Audio",
            F($"a layout that differs only in the tab in front (Tab:Audio, not {was}): that tab is in front again"));

        var before = dock.Save();
        expect(MainWindow.TabLayoutOf(dock, perTab: false) is null && !MainWindow.LoadTabLayout(dock, tabA, perTab: false) && dock.Save() == before,
            "the setting off: a tab put behind keeps no layout, and none is laid out as one comes back");
        expect(!MainWindow.LoadTabLayout(dock, new ProjectTabHeader { Id = "new" }, perTab: true) && !MainWindow.LoadTabLayout(dock, new ProjectTabHeader { Id = "blank", LayoutJson = "" }, perTab: true) && dock.Save() == before,
            "a tab with no layout (new, opened, or from before 5.4D) takes the window as it is");

        // The Media Browser's view with it (5.4D review): a Vertical layout's Then sets the Filmstrip, which no layout's JSON holds.
        expect(MainWindow.TabMediaViewOf(HandPegApp.Views.MediaBrowserView.Filmstrip, perTab: true) == "Filmstrip" && MainWindow.TabMediaViewOf(HandPegApp.Views.MediaBrowserView.List, perTab: false) is null,
            "a tab put behind keeps the Media Browser's view by name (Filmstrip), none while the setting is off");
        expect(MainWindow.MediaViewOf("Filmstrip") == HandPegApp.Views.MediaBrowserView.Filmstrip && MainWindow.MediaViewOf("list") == HandPegApp.Views.MediaBrowserView.List
               && MainWindow.MediaViewOf(null) is null && MainWindow.MediaViewOf("") is null && MainWindow.MediaViewOf("Carousel") is null && MainWindow.MediaViewOf("7") is null,
            "and reads it back (any case); none, an empty name, or a view that does not exist leaves the Media Browser as it is");

        // The header with a layout: under 4 KB, the largest built-in with every split's share as a drag leaves it.
        var largest = 0;
        foreach (var (_, layout) in MainWindow.BuiltInLayoutStates(TabIds))
        {
            foreach (var split in Splits(layout.Main))
                split.Sizes = split.Sizes.Select((s, i) => s + 0.123456789012345 * (i + 1)).ToList();
            var header = new ProjectTabHeader { Id = Guid.NewGuid().ToString("N"), Title = new string('N', 120), ProjectPath = @"C:\Users\someone\Documents\HandPeg\Projects\" + new string('p', 120) + ".hproj" };
            (header.NewProjectName, header.NewProjectFolder, header.ExplodedOpen, header.MediaView) = ("Studio Name", @"D:\Projects", true, "Filmstrip");
            header.LayoutJson = JsonSerializer.Serialize(layout, AppSettingsJsonContext.Default.DockLayoutState);
            largest = Math.Max(largest, Encoding.UTF8.GetByteCount(ProjectTabs.HeaderJson(header)));
        }

        expect(largest is > 1024 and < 4096, "a header with its layout (each built-in, its splits dragged, a 120-character name and a long path) is over 1 KB and under 4 KB");

        Wiring(expect);
        Settings(expect);
    }

    private static IEnumerable<DockNode> Splits(DockNode node) => node.IsGroup ? [] : node.Children.SelectMany(Splits).Prepend(node);

    private static void Wiring(Action<bool, string> expect)
    {
        var tabs = Read("MainWindow.ProjectTabs.cs");
        expect(InOrder(Method(tabs, "private async Task SwitchToTabAsync("), "_tabs.Activate(tab);", "ShowTabs();", "ApplyTabLayout(tab);", "await ResumeTabAsync(tab, behind);"),
            "a switch lays the tab's layout out right after it is in front, before its still shows (ResumeTabAsync)");
        expect(InOrder(Method(tabs, "private void TakeBack("), "_tabs.Activate(behind);", "ApplyTabLayout(behind);", "AttachHydrated(behind, live)")
               && InOrder(Method(tabs, "private void RestoreTabView("), "RestoreExplodedForTab(tab);", "OpenInPlayer("),
            "a switch taken back lays the tab put behind's own layout out again, then brings it back from memory (its 3D view with it, 5.4D-TabPerf)");
        expect(InOrder(Method(tabs, "private async Task CloseProjectTabAsync("), "EndLayoutPreview();", "_tabs.Activate(next);", "ApplyTabLayout(next);", "await ResumeTabAsync(next, null);"),
            "closing the tab in front lays the next tab's layout out (a layout the selector previews put back first)");
        expect(InOrder(Method(tabs, "private void ApplyTabLayout("), "EndLayoutPreview();", "var perTab = AppSettings.Current.PerTabWindowLayouts;", "if (perTab && MediaViewOf(tab.MediaView) is { } view && view != MediaBrowser.ViewMode)",
                   "MediaBrowser.ViewMode = view;", "LoadTabLayout(_dock, tab, perTab)", "_viewModel.SavePaneLayout(SaveDockInto);"),
            "a tab's layout laid out (the selector closed and its preview put back first, its Media Browser view set before the build: 5.4D review) is saved as the window's arrangement, as any change of it is");
        var never = new[] { "private async void RecoverProjectTabs(", "private bool NewProjectTab(", "internal async Task OpenProjectInTabAsync(", "private void InitializeProjectTabs(", "private async Task<bool> ResumeTabAsync(",
            "private bool AttachHydrated(" };
        var found = never.Where(m => Method(tabs, m) is { Length: > 0 } body && (body.Contains("ApplyTabLayout", StringComparison.Ordinal) || body.Contains("_dock.Load", StringComparison.Ordinal))).ToList();
        expect(found.Count == 0 && never.All(m => Method(tabs, m).Length > 0),
            "nothing else lays a layout out: not at start-up, nor as tabs left behind are taken in, nor for a new or opened tab (they take the window as it is)");
        var layouts = Read("MainWindow.Layouts.cs");
        expect(layouts.Contains("private void RestoreLayoutBeforePreview()", StringComparison.Ordinal) && !layouts.Contains(".LayoutJson", StringComparison.Ordinal),
            "choosing a layout in a tab needs no hook in the selector: it is captured as the tab goes behind");
    }

    private static void Settings(Action<bool, string> expect)
    {
        expect(new AppSettings().PerTabWindowLayouts && JsonSerializer.Deserialize("{}", AppSettingsJsonContext.Default.AppSettings)!.PerTabWindowLayouts
               && !JsonSerializer.Deserialize("{\"PerTabWindowLayouts\":false}", AppSettingsJsonContext.Default.AppSettings)!.PerTabWindowLayouts,
            "AppSettings.PerTabWindowLayouts is on as HandPeg comes (and for a settings file from before it); off stays off");

        var settings = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Dialogs", "SettingsWindow.xaml"));
        var wpf = settings.Root!.Name.Namespace;
        XElement? Named(string name) => settings.Descendants().FirstOrDefault(e => (string?)e.Attribute(X + "Name") == name);
        var boxes = settings.Descendants(wpf + "CheckBox").Where(c => (string?)c.Attribute("IsChecked") == "{Binding PerTabWindowLayouts}").ToList();
        var tab = boxes.FirstOrDefault()?.Ancestors(wpf + "TabItem").FirstOrDefault();
        expect(boxes.Count == 1 && (string?)tab?.Attribute("Header") == "General" && (string?)boxes[0].Attribute("Content") == "Each project tab remembers its own layout",
            "Settings has one CheckBox for it, \"Each project tab remembers its own layout\", in the General tab (Appearance)");

        var progress = Named("TitleProgress");
        var fill = Named("TitleProgressFill");
        var rail = Named("TitleProgressRail");
        expect(progress is not null && (string?)progress.Attribute("Panel.ZIndex") == "10" && (string?)progress.Attribute("IsHitTestVisible") == "False"
               && (string?)fill?.Attribute("Opacity") == "0.16" && rail?.Parent == progress && (string?)rail.Attribute("Height") == "2" && (string?)rail.Attribute("Canvas.Bottom") == "0",
            "the Settings window's title-bar progress is the editor's: over everything (ZIndex 10, no hits), a 16 % wash and a 2 DIP rail along the bottom");
        var stage = Read("Dialogs", "SettingsWindow.Stage.cs");
        expect(InOrder(Method(stage, "private void UpdateTitleProgress()"), "TitleProgressFill.Opacity = work.IsIndeterminate ? TitleProgressFaint : TitleProgressStrength;", "TitleProgressRail.Width = TitleProgressFill.Width;", "TitleProgressRail.Opacity = work.IsIndeterminate ? TitleProgressRailFaint : 1;")
               && stage.Contains("TitleProgressStrength = 0.16, TitleProgressFaint = 0.08, TitleProgressRailFaint = 0.5;", StringComparison.Ordinal),
            "and it is sized as the editor's: 0.16, or 0.08 while the work cannot say how far it is (its rail then at half strength)");
    }
}
