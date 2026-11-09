using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4C (Subagent C2): any number of side windows (Live Video Preview or Pane Docker), torn off by dragging a tab out, saved and
/// restored. dock-side-windows (a dock manager whose window is never shown, so no side window is ever made: the model alone): the single
/// side window of before 5.4C becomes Pane Docker 1 and the legacy members are never written again; every pane in one place across the
/// main window and N side windows, a side window keeping its own; a Live Video Preview holds none; ShowInItsGroup in every docker; an
/// optional pane found in a docker; the tab menu's window moves; moving to a new docker, into one, back; a mode switch and a docker closed
/// hand panes back; taking up a layout leaves the dockers alone; Reset closes the dockers only; the JSON through the generated metadata.
/// side-window-chrome: a real SideWindow (HandPeg's 26 DIP title bar under WindowChrome and the frame hook, hit-tested as the main
/// window's; the mode box swapping its content; its place as Windows keeps it), then a real dock manager whose side windows come back after
/// the main window's first frame without laying the main window out again, a new preview the same, the screen's order of windows, and a
/// docker closed by its own button handing its pane back. side-window-mirror: the mirror's region of the player's output and the preview's
/// fit (pure), and the never-waiting mirror present and its wiring (source).
/// </summary>
public static partial class SideWindowTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    // The settings tabs' pane ids after 5.4B (as ProjectSettingsTests has them), and the other panes MainWindow registers.
    private static readonly IReadOnlyList<string> TabIds = ["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];
    private static readonly string[] PaneIds = ["Player", "Playback", "Timeline", "UnifiedTimeline", "Layout", "Header", "Media", "Keyframes", "Effects", "Meter", "Presets"];

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("dock-side-windows", "# Any number of side windows, the model alone: the old side window migrated, each pane in one place across every window, moves, modes, closes, layouts and the JSON.", Model);
        yield return Case("side-window-chrome", "# A real side window: HandPeg's 26 DIP title bar and frame, its mode box, its place; side windows restored after the first frame without laying the main window out again.", Chrome);
        yield return Case("side-window-mirror", "# The Live Video Preview's mirror: the canvas's part of the player's output, fitted at its shape, presented without ever waiting, attached wherever the engine is.", Mirror);
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

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }

    private static void Idle()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(150);
    }

    /// <summary>A dock manager with MainWindow's panes (plain stand-ins) and one optional studio homed with the effects.</summary>
    private static DockManager NewDock(Window window, ContentControl host)
    {
        var dock = new DockManager(window, host, () => MainWindow.StandardLayout(TabIds));
        foreach (var id in PaneIds.Concat(TabIds))
        {
            var content = new Border { Child = new TextBlock { Text = id } };
            dock.Register(new DockPane
            {
                Id = id, Title = id, Content = () => content, Handle = id.StartsWith("Tab:", StringComparison.Ordinal) ? PaneHandle.Tab : PaneHandle.TitleSlot,
                MinWidth = () => 160, MinHeight = 60,
            });
        }

        var studio = new Border();
        dock.Register(new DockPane { Id = "SubtitleStudio", Title = "Subtitle Studio", Content = () => studio, Optional = true, HomeWith = ["Effects", "Keyframes"], MinWidth = () => 160, MinHeight = 60 });
        return dock;
    }

    private static string Json(DockLayoutState state) => JsonSerializer.Serialize(state, AppSettingsJsonContext.Default.DockLayoutState);

    private static List<SideWindowState> Sides(DockManager dock) => SideWindowStates.Read(dock.SaveSideWindows());

    private static DockLayoutState MainOf(DockManager dock) => DockLayoutState.Read(dock.Save())!;

    private static string Panes(DockNode? node) => node is null ? "(none)" : string.Join(",", node.AllPanes());

    /// <summary>Every registered pane but the optional one in exactly one place across the main window and the side windows (what is wrong, else "").</summary>
    private static string OnceEach(DockManager dock)
    {
        var all = MainOf(dock).Main.AllPanes().Concat(Sides(dock).SelectMany(s => s.Tree?.AllPanes() ?? [])).ToList();
        var missing = PaneIds.Concat(TabIds).Where(p => !all.Contains(p)).ToList();
        var twice = all.GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        return (missing.Count > 0 ? "missing " + string.Join(",", missing) + " " : "") + (twice.Count > 0 ? "twice " + string.Join(",", twice) : "");
    }

    // ----- dock-side-windows -----

    private static void Model(Action<bool, string> expect)
    {
        // The single side window of before 5.4C (open, holding Media and Effects, with a closed arrangement too), in a settings file
        // with no SideWindows yet: Pane Docker 1.
        var legacy = MainWindow.StandardLayout(TabIds);
        foreach (var pane in new[] { "Media", "Effects" })
            legacy.Main = DockTree.Remove(legacy.Main, pane)!;
        legacy.Side = DockNode.Split(false, (DockNode.Group("Media"), 1), (DockNode.Group("Effects"), 1));
        (legacy.SideOpen, legacy.SideBounds, legacy.SideMaximized, legacy.SideClosed) = (true, [100, 120, 800, 600], true, DockNode.Group("Keyframes"));

        var dock = NewDock(new Window(), new ContentControl());
        dock.Load(Json(legacy));
        var sides = Sides(dock);
        expect(sides.Count == 1 && sides[0] is { Id: SideWindowStates.LegacyId, Mode: SideWindowState.PanesMode, Maximized: true, Placement: null }
               && sides[0].Bounds?.SequenceEqual([100.0, 120, 800, 600]) == true && Panes(sides[0].Tree) == "Media,Effects",
            $"the side window of before 5.4C is Pane Docker \"side-1\": its panes ({Panes(sides.FirstOrDefault()?.Tree)}), its place in DIPs and maximized kept");
        var saved = JsonNode.Parse(dock.Save())!;
        expect(saved["Side"] is null && saved["SideOpen"]?.GetValue<bool>() == false && saved["SideBounds"] is null && saved["SideClosed"] is null
               && saved["SideMaximized"]?.GetValue<bool>() == false,
            "the main arrangement saved again carries none of the legacy members (they are read once, never written)");
        expect(MainOf(dock).Main.GroupOf("Media") is null && MainOf(dock).Main.GroupOf("Effects") is null && MainOf(dock).Main.GroupOf("Keyframes") is not null && OnceEach(dock) == "",
            "Media and Effects are in the docker only; the closed arrangement's Keyframes stays in the main window; every pane in one place");
        expect(dock.IsSideOpen && dock.SideWindows.Count == 0, "with the main window never shown, the docker is listed but no window is made (it would open after the first frame)");

        var shut = new DockLayoutState { Main = DockNode.Group("Player"), Side = DockNode.Group("Media"), SideOpen = false, SideClosed = DockNode.Group("Effects") };
        var none = new List<SideWindowState>();
        expect(!SideWindowStates.Migrate(shut, none) && none.Count == 0 && shut is { Side: null, SideClosed: null, SideOpen: false },
            "a side window that was closed adds no docker, and its members are cleared all the same");

        // Each pane in one place across the main window and four side windows saved by 5.4C: a side window keeps what it holds, a
        // pane unknown or held twice goes, a preview holds nothing, a repeated id is renamed, an unknown mode reads as a docker.
        var standard = Json(MainWindow.StandardLayout(TabIds));
        var many = SideWindowStates.Write(
        [
            new SideWindowState { Id = "side-1", Mode = SideWindowState.PanesMode, Tree = DockNode.Split(true, (DockNode.Group("Media", "Keyframes"), 1), (DockNode.Group("Bogus"), 1)) },
            new SideWindowState { Id = "side-2", Mode = SideWindowState.PanesMode, Tree = DockNode.Group("Keyframes", "Meter") },
            new SideWindowState { Id = "side-3", Mode = SideWindowState.PreviewMode, Tree = DockNode.Group("Effects") },
            new SideWindowState { Id = "side-2", Mode = "sideways" },
        ]);
        dock.Load(standard, sideWindows: many);
        sides = Sides(dock);
        expect(sides.Select(s => s.Id).SequenceEqual(["side-1", "side-2", "side-3", "side-4"]) && sides[3].Mode == SideWindowState.PanesMode,
            "four side windows taken up in their order (the migrated side-1 takes the saved one's state); the repeated id is \"side-4\" now, its unknown mode a Pane Docker");
        expect(Panes(sides[0].Tree) == "Media,Keyframes" && sides[0].Tree!.IsGroup && Panes(sides[1].Tree) == "Meter" && sides[2].Tree is null && sides[3].Tree is null,
            $"side-1 holds {Panes(sides[0].Tree)} (Bogus gone, its split tidied), side-2 {Panes(sides[1].Tree)} (Keyframes was side-1's), the preview nothing, side-4 nothing");
        var main = MainOf(dock).Main;
        expect(main.GroupOf("Effects") is not null && main.GroupOf("Media") is null && main.GroupOf("Keyframes") is null && main.GroupOf("Meter") is null && OnceEach(dock) == "",
            "the main window loses what the side windows hold, Effects (the preview's) stays in it: every pane in exactly one place");
        var names = dock.SideWindowList.Select(s => s.Name).ToList();
        expect(names.SequenceEqual(["Pane Docker 1: Media, Keyframes", "Pane Docker 2: Meter", "Live Video Preview 3", "Pane Docker 4"]),
            "the Layout menu's rows: " + string.Join(" | ", names));

        // An optional pane: never added by a load, found in any window, opened with its mates in the main window, closed.
        expect(dock.Contains("Meter") && dock.Contains("Media") && !dock.Contains("SubtitleStudio"), "Contains finds a pane in a docker; the optional studio is in no window");
        dock.Open("SubtitleStudio");
        expect(MainOf(dock).Main.GroupOf("SubtitleStudio")?.Panes.Contains("Effects") == true, "Open puts it in the main window with its HomeWith mates (the Effects group)");
        dock.Close("SubtitleStudio");
        expect(!dock.Contains("SubtitleStudio"), "Close takes it out again");

        // The tab menu's window moves, by number.
        var fromMain = dock.TabMenuMoves("Effects").Select(m => m.Line).ToList();
        var fromDocker = dock.TabMenuMoves("Media").Select(m => m.Line).ToList();
        expect(fromMain.SequenceEqual(["Move to New Side Window", "Move to Side Window 1", "Move to Side Window 2", "Move to Side Window 4"]),
            "a pane of the main window: " + string.Join(" | ", fromMain) + " (no preview listed)");
        expect(fromDocker.SequenceEqual(["Move to New Side Window", "Move to Side Window 2", "Move to Side Window 4", "Move to Main Window"]),
            "a pane of docker 1: " + string.Join(" | ", fromDocker));

        // Moves: into an empty docker, refused by a preview, back to the main window, out to a new docker.
        dock.MoveToWindow("Effects", "side-4");
        expect(Panes(Sides(dock)[3].Tree) == "Effects" && MainOf(dock).Main.GroupOf("Effects") is null, "Move to Side Window 4: Effects is docker 4's");
        dock.MoveToWindow("Effects", "side-3");
        expect(Panes(Sides(dock)[3].Tree) == "Effects" && Sides(dock)[2].Tree is null, "a Live Video Preview takes no pane: Effects stays where it was");
        dock.MoveToWindow("Effects", sideId: null);
        expect(MainOf(dock).Main.GroupOf("Effects") is not null && Sides(dock)[3].Tree is null, "Move to Main Window: back where Standard Editing has it");
        dock.TabMenuMoves("Layout")[0].Do();
        sides = Sides(dock);
        expect(sides.Count == 5 && sides[4] is { Id: "side-5", Mode: SideWindowState.PanesMode } && Panes(sides[4].Tree) == "Layout" && OnceEach(dock) == "",
            "Move to New Side Window: a fifth window, \"side-5\", a Pane Docker holding the Canvas; every pane still in one place");

        // A docker made a preview hands its panes back; a preview made a docker is empty.
        dock.SetSideWindowMode("side-1", SideWindowMode.LiveVideoPreview);
        dock.SetSideWindowMode("side-3", SideWindowMode.PaneDocker);
        sides = Sides(dock);
        main = MainOf(dock).Main;
        expect(sides[0] is { Mode: SideWindowState.PreviewMode, Tree: null } && main.GroupOf("Media") is not null && main.GroupOf("Keyframes")?.Panes.Contains("Effects") == true,
            "docker 1 made a Live Video Preview: Media and Keyframes back where Standard Editing has them (Keyframes with the effects)");
        expect(sides[2] is { Mode: SideWindowState.PanesMode, Tree: null } && OnceEach(dock) == "", "the preview made a Pane Docker holds nothing yet");

        // A layout taken up leaves the dockers as they are (and their panes out of its main window).
        dock.Apply(dock.Prepared(MainWindow.AssemblyLayout(TabIds)));
        expect(Panes(Sides(dock)[1].Tree) == "Meter" && Panes(Sides(dock)[4].Tree) == "Layout" && dock.Current.Main.GroupOf("Meter") is null && OnceEach(dock) == "",
            "Assembly & Media taken up: Meter stays in docker 2 and the Canvas in docker 5, out of the main window");

        // A docker closed: its panes go back where the main window started them.
        dock.CloseSideWindow("side-2");
        sides = Sides(dock);
        expect(sides.All(s => s.Id != "side-2") && MainOf(dock).Main.GroupOf("Meter") is not null && OnceEach(dock) == "", "docker 2 closed: the Master Meter is back in the main window");

        // The JSON through the generated metadata: written by it as reflection writes it, read back to the same.
        var text = dock.SaveSideWindows();
        var generated = JsonSerializer.Deserialize(text, AppSettingsJsonContext.Default.ListSideWindowState)!;
        expect(JsonSerializer.Serialize(generated, new JsonSerializerOptions()) == text && SideWindowStates.Write(SideWindowStates.Read(text)) == text,
            "the side windows' JSON round-trips through AppSettingsJsonContext (List<SideWindowState>) and as reflection writes it");
        var settings = JsonSerializer.Deserialize(JsonSerializer.Serialize(new AppSettings { SideWindows = text }, AppSettingsJsonContext.Default.AppSettings), AppSettingsJsonContext.Default.AppSettings)!;
        expect(settings.SideWindows == text && new AppSettings().SideWindows is null && settings.Clone().SideWindows == text,
            "AppSettings.SideWindows: null until first saved, kept as written through the settings file and Clone");
        expect(SideWindowStates.NewId([new SideWindowState { Id = "side-1" }, new SideWindowState { Id = "side-3" }]) == "side-2" && SideWindowStates.Read("not json").Count == 0,
            "a new id is the lowest number free (side-2 between side-1 and side-3); unreadable JSON is no side window");

        // ShowInItsGroup in every docker: the pure call, and a load with showFirst.
        var docker = new SideWindowState { Tree = DockNode.Group("Tab:Audio", "UnifiedTimeline") };
        docker.Tree.Active = "Tab:Audio";
        expect(DockManager.ShowInItsGroup(new DockLayoutState { Main = DockNode.Group("Player") }, "UnifiedTimeline", [docker]) && docker.Tree.Active == "UnifiedTimeline",
            "ShowInItsGroup shows the Timeline in a docker's group");
        var timelineDocker = new SideWindowState { Id = "side-1", Tree = DockNode.Group("Tab:Audio", "UnifiedTimeline") };
        timelineDocker.Tree.Active = "Tab:Audio";
        dock.Load(standard, showFirst: "UnifiedTimeline", sideWindows: SideWindowStates.Write([timelineDocker]));
        sides = Sides(dock);
        expect(sides.Count == 1 && sides[0].Tree is { Active: "UnifiedTimeline" } && MainOf(dock).Main.GroupOf("UnifiedTimeline") is null && OnceEach(dock) == "",
            "a load with showFirst: the docker holding the Timeline opens on it, and the main window gives it up to the docker");

        // Reset: every docker closed, a preview left open.
        dock.OpenSideWindow(SideWindowMode.LiveVideoPreview);
        dock.Reset();
        sides = Sides(dock);
        expect(sides.Count == 1 && sides[0].IsPreview && DockManager.SameShape(MainOf(dock), MainWindow.StandardLayout(TabIds)),
            "Reset Panes Layout closes every Pane Docker and keeps the Live Video Preview; the main window is Standard Editing again");

        // The window asks for it all: the side windows with the one load before the first frame, both saved together.
        var docking = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MainWindow.Docking.cs"));
        var manager = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Docking", "DockManager.cs"));
        expect(docking.Contains("_dock.Load(sideWindows: AppSettings.Current.SideWindows,", StringComparison.Ordinal)
               && docking.Contains("settings.SideWindows = _dock.SaveSideWindows();", StringComparison.Ordinal),
            "InitializeDocking loads AppSettings.SideWindows with the arrangement; SaveDockInto saves both");
        expect(manager.Contains("_main.ContentRendered += OpenSidesWhenShown;", StringComparison.Ordinal)
               && manager.Contains("}, System.Windows.Threading.DispatcherPriority.Background);", StringComparison.Ordinal),
            "the side windows open after the main window's first frame, at Background priority (OpenSidesWhenShown)");
    }

    // ----- side-window-chrome -----

    private const int WmNcHitTest = 0x0084, HtClient = 1, HtCaption = 2;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static int HitTest(Window window, FrameworkElement at)
    {
        var point = at.PointToScreen(new Point(at.ActualWidth / 2, at.ActualHeight / 2));
        var lParam = ((int)Math.Round(point.Y) << 16) | ((int)Math.Round(point.X) & 0xFFFF);
        return (int)SendMessage(new WindowInteropHelper(window).Handle, WmNcHitTest, IntPtr.Zero, lParam);
    }

    private static Window NewMain(ContentControl host)
    {
        var main = new Window { Left = 40, Top = 40, Width = 900, Height = 600, Content = host, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };

        // What App.xaml gives the real one, which the side windows find through the main window's resources.
        main.Resources["CaptionBarHeight"] = 26.0;
        main.Resources["CaptionButton"] = new Style(typeof(Button));
        main.Resources["CaptionCloseButton"] = new Style(typeof(Button));
        main.Resources["TitleBarBrush"] = System.Windows.Media.Brushes.DimGray;
        return main;
    }

    private static void Chrome(Action<bool, string> expect)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var mainWindow = app.MainWindow;
        var host = new ContentControl { Focusable = false };
        var main = NewMain(host);
        var opened = new List<Window> { main };
        try
        {
            main.Show();
            Idle();

            // ----- One side window -----
            var side = new SideWindow(main, SideWindowMode.PaneDocker) { SideId = "side-9", Number = 2, ShowActivated = false };
            opened.Add(side);
            var chrome = WindowChrome.GetWindowChrome(side);
            expect(chrome is { CaptionHeight: 26, UseAeroCaptionButtons: false } && chrome.GlassFrameThickness == new Thickness(0, 0, 0, 1)
                   && chrome.CornerRadius == new CornerRadius(0) && chrome.ResizeBorderThickness == SystemParameters.WindowResizeBorderThickness,
                "its frame is HandPeg's: WindowChrome, caption 26 (CaptionBarHeight), glass 0,0,0,1, square, no Aero buttons, Windows' resize border");
            expect(side.Title == "HandPeg: Side Window 2 (Pane Docker)" && side.Owner == main && !side.ShowInTaskbar, "its title names its number and mode; owned by the main window, no taskbar button");

            var requested = new List<SideWindowMode>();
            side.ModeRequested += requested.Add;
            side.Place([100, 100, 1100, 900], null, maximized: false, fullScreen: false, atPointer: false);
            side.Show();
            Idle();

            expect(Math.Abs(side.TitleBar.ActualHeight - 26) < 0.01 && side.TitleBar.Background == System.Windows.Media.Brushes.DimGray, "its title bar is drawn 26 DIPs tall, on the title bar's brush (TitleBarBrush)");
            var (styles, close) = ((Style)main.Resources["CaptionButton"], (Style)main.Resources["CaptionCloseButton"]);
            expect(side.CaptionButtons.All(WindowChrome.GetIsHitTestVisibleInChrome) && WindowChrome.GetIsHitTestVisibleInChrome(side.ModeBox)
                   && side.CaptionButtons[0].Style == styles && side.CaptionButtons[1].Style == styles && side.CaptionButtons[2].Style == close,
                "Minimize, Maximize and Close take the CaptionButton / CaptionCloseButton styles; they and the mode box are hit-test visible in the chrome");
            expect(HitTest(side, side.CaptionButtons[2]) == HtClient && HitTest(side, side.ModeBox) == HtClient,
                "hit-tested by the frame hook, the Close button and the mode box are the window's own (HTCLIENT): they take their clicks");
            var title = (FrameworkElement)side.TitleBar.Children[0];
            expect(HitTest(side, title) == HtCaption, "the title beside them is the caption (HTCAPTION): dragging it moves the window");
            expect(HitTest(side, side.Body) == HtClient, "the body under the title bar is the window's own (never its caption)");

            var placed = side.CurrentPlacement();
            expect(placed is { Maximized: false, FullScreen: false } && placed.Value.Placement?.SequenceEqual([100, 100, 1100, 900]) == true,
                $"placed as saved (SetWindowPlacement), it reads back the same restored place: {string.Join(",", placed?.Placement ?? [])}");

            side.ModeBox.SelectedIndex = 0;
            expect(requested.SequenceEqual([SideWindowMode.LiveVideoPreview]) && side.Mode == SideWindowMode.PaneDocker && side.Body.Children().Contains(side.Host),
                "choosing Live Video Preview in the mode box asks the dock manager (ModeRequested), the window waiting to be told");
            var surfaces = 0;
            side.PreviewSurfaceChanged += _ => surfaces++;
            side.ShowMode(SideWindowMode.LiveVideoPreview);
            Idle();
            expect(side.Mode == SideWindowMode.LiveVideoPreview && side.Body.Children().Contains(side.PreviewPanel) && side.PreviewPanel.Children.OfType<VideoSurface>().Count() == 1
                   && side.PreviewHandle != IntPtr.Zero && surfaces == 1 && side.Title == "HandPeg: Side Window 2 (Live Video Preview)",
                "shown as a Live Video Preview: the body is the preview, a native surface in the aspect panel, its handle told once (PreviewSurfaceChanged)");
            side.ShowMode(SideWindowMode.PaneDocker);
            Idle();
            expect(side.Body.Children().Contains(side.Host) && side.PreviewHandle == IntPtr.Zero && side.PreviewPanel.Children.Count == 0 && surfaces == 2 && side.ModeBox.SelectedIndex == 1,
                "back to a Pane Docker: the docker's host again, the surface gone (told again, for its mirror to be let go), the mode box following");
            side.Close();

            // ----- A real dock: side windows restored after the main window's first frame -----
            var dockHost = new ContentControl { Focusable = false };
            var dockMain = NewMain(dockHost);
            opened.Add(dockMain);
            var dock = NewDock(dockMain, dockHost);
            var saved = SideWindowStates.Write(
            [
                new SideWindowState { Id = "side-1", Mode = SideWindowState.PanesMode, Tree = DockNode.Group("Media"), Placement = [140, 140, 1140, 940] },
                new SideWindowState { Id = "side-2", Mode = SideWindowState.PreviewMode, Placement = [200, 160, 1000, 760] },
            ]);
            dock.Load(Json(MainWindow.StandardLayout(TabIds)), showFirst: "UnifiedTimeline", sideWindows: saved);
            var root = typeof(DockManager).GetField("_mainRoot", Hidden)!.GetValue(dock)!;
            var tree = (Border)root.GetType().GetProperty("Tree")!.GetValue(root)!;
            var laidOut = tree.Child;
            expect(dock.SideWindows.Count == 0 && laidOut is not null, "loaded before the main window is shown: its tree laid out, no side window made yet");
            dockMain.Show();
            Idle();
            Idle();
            var windows = dock.SideWindows;
            opened.AddRange(windows);
            expect(windows.Count == 2 && windows[0] is { Mode: SideWindowMode.PaneDocker, SideId: "side-1", Number: 1, IsVisible: true } && windows[1] is { Mode: SideWindowMode.LiveVideoPreview, Number: 2 }
                   && windows[1].PreviewHandle != IntPtr.Zero,
                "after the main window's first frame both come back: Pane Docker 1, and Live Video Preview 2 with its surface");
            expect(ReferenceEquals(tree.Child, laidOut), "the main window's tree is still the one laid out before its first frame: restoring them laid it out no more");
            expect(Window.GetWindow(dock.Realize("Media")) == windows[0] && Window.GetWindow(dock.Realize("Effects")) == dockMain, "Media is in docker 1, Effects in the main window");
            expect(windows[0].CurrentPlacement()?.Placement?.SequenceEqual([140, 140, 1140, 940]) == true, "docker 1 is where it was saved");

            var preview = dock.OpenSideWindow(SideWindowMode.LiveVideoPreview);
            Idle();
            if (preview is not null)
                opened.Add(preview);
            expect(preview is { Number: 3, Mode: SideWindowMode.LiveVideoPreview } && ReferenceEquals(tree.Child, laidOut) && dock.SideWindows.Count == 3,
                "+ New Side Window: Live Video Preview 3, laid out on its own (the main window's tree untouched again)");
            var handles = new[] { dockMain, preview! }.Select(w => new WindowInteropHelper(w).Handle).ToList();
            expect(WindowOrder.Topmost(handles) == handles[1] && WindowOrder.Lowest(handles) == handles[0],
                "by the screen's order (WindowOrder), the new side window is above the main window that owns it");

            windows[0].Close();
            Idle();
            expect(Window.GetWindow(dock.Realize("Media")) == dockMain && dock.SideWindows.Count == 2 && Sides(dock).All(s => s.Id != "side-1"),
                "docker 1 closed by its own button: Media is back in the main window, the window gone from the list");
            expect(dock.SideWindows.Select(w => w.Number).SequenceEqual([1, 2]), "the side windows left are numbered 1 and 2 again");
            dock.Shutdown();
        }
        finally
        {
            foreach (var window in opened.AsEnumerable().Reverse())
            {
                if (window.IsLoaded || window.IsVisible)
                    window.Close();
            }

            app.MainWindow = mainWindow;
        }
    }

    // ----- side-window-mirror -----

    private static void Mirror(Action<bool, string> expect)
    {
        static string Region((int L, int T, int R, int B) r) => $"({r.L},{r.T})-({r.R},{r.B})";
        var cases = new (int W, int H, int Cw, int Ch, (int, int, int, int) Want, string What)[]
        {
            (1920, 1080, 1920, 1080, (0, 0, 1920, 1080), "a 16:9 canvas in a 16:9 player: all of it"),
            (1920, 1080, 1080, 1920, (656, 0, 1264, 1080), "a 9:16 canvas in a 16:9 player: the middle 608 columns, without the bars"),
            (1000, 1000, 1920, 1080, (0, 219, 1000, 781), "a 16:9 canvas in a square player: the middle 562 rows"),
            (7, 3, 1, 1, (2, 0, 5, 3), "a square canvas in 7x3: whole pixels, centred"),
            (0, 1080, 1920, 1080, (0, 0, 0, 0), "a player of no size (hidden): nothing"),
            (1920, 1080, 0, 1080, (0, 0, 0, 0), "a canvas of no size: nothing"),
        };
        foreach (var (w, h, cw, ch, want, what) in cases)
        {
            var got = GpuViewportControl.MirrorRegion(w, h, cw, ch);
            expect(got == want, $"MirrorRegion {w}x{h} output, {cw}x{ch} canvas = {Region(got)}: {what}");
        }

        var fits = new (Size Room, double Aspect, Rect Want, string What)[]
        {
            (new Size(800, 600), 16.0 / 9, new Rect(0, 75, 800, 450), "16:9 in 800x600: the full width, centred down"),
            (new Size(300, 600), 9.0 / 16, new Rect(0, 33.333, 300, 533.333), "9:16 in 300x600: the full width"),
            (new Size(1000, 400), 1, new Rect(300, 0, 400, 400), "square in 1000x400: the full height, centred across"),
            (new Size(0, 400), 1, new Rect(0, 0, 0, 0), "no room: nothing"),
        };
        foreach (var (room, aspect, want, what) in fits)
        {
            var got = AspectPanel.Fit(room, aspect);
            var same = Math.Abs(got.X - want.X) < 0.01 && Math.Abs(got.Y - want.Y) < 0.01 && Math.Abs(got.Width - want.Width) < 0.01 && Math.Abs(got.Height - want.Height) < 0.01;
            expect(same, $"AspectPanel.Fit: {what}");
        }

        var src = Path.Combine(RepositoryRoot(), "src");
        var viewport = File.ReadAllText(Path.Combine(src, "Controls", "GpuViewportControl.cs"));
        var present = viewport[viewport.IndexOf("public bool PresentMirror(", StringComparison.Ordinal)..viewport.IndexOf("private void MakeSwapChain(", StringComparison.Ordinal)];
        expect(present.Contains("_reader.CopyPresentedRegionTo(back, left, top, right, bottom, 0)", StringComparison.Ordinal)
               && present.Contains("WaitForSingleObject(_waitable, 0) != 0", StringComparison.Ordinal) && present.Contains("Dxgi.Present(_swapChain, 0, 0)", StringComparison.Ordinal)
               && present.IndexOf("CopyPresentedRegionTo", StringComparison.Ordinal) < present.IndexOf("WaitForSingleObject", StringComparison.Ordinal),
            "the mirror's present never waits: the frame copied with a 0 ms keyed-mutex wait, then presented only if its swap chain is ready now (a 0 ms wait), at sync interval 0");
        expect(viewport.Contains("_reader = player._reader;", StringComparison.Ordinal) && viewport.Contains("if (_ownsReader)", StringComparison.Ordinal),
            "a mirror shares the player's reader (and so its device), and never disposes it");

        var engine = File.ReadAllText(Path.Combine(src, "Services", "GpuPreviewEngine.cs"));
        var pump = engine[engine.IndexOf("private void Pump()", StringComparison.Ordinal)..engine.IndexOf("private long CanvasFrameOf(", StringComparison.Ordinal)];
        expect(pump.Split("PresentMirrors();").Length - 1 == 4 && pump.Contains("TakeMirrorRequests();", StringComparison.Ordinal),
            "the pump takes the attach and detach requests, and gives the mirrors their turn after each of the player's presents (four places)");
        expect(engine.Contains("_mirrorRequests.Enqueue((true, window));", StringComparison.Ordinal) && engine.Contains("_mirrorRequests.Enqueue((false, window));", StringComparison.Ordinal)
               && engine.Contains("foreach (var mirror in _mirrors)\r\n            mirror.Dispose();", StringComparison.Ordinal),
            "AttachMirror and DetachMirror are queued to the pump's thread; the engine disposes its mirrors as it goes");

        var preview = File.ReadAllText(Path.Combine(src, "MainWindow.GpuPreview.cs"));
        expect(preview.Contains("_gpu = engine;", StringComparison.Ordinal) && preview.IndexOf("ReattachMirrors();", StringComparison.Ordinal) > preview.IndexOf("_gpu = engine;", StringComparison.Ordinal)
               && preview.Split("ForgetMirrors();").Length - 1 == 2,
            "a new engine has every preview attached afresh; an engine stopped or closed forgets them (its mirrors went with it)");
        var surface = File.ReadAllText(Path.Combine(src, "Controls", "VideoSurface.cs"));
        expect(surface.Contains("Docking.WindowOrder.Lowest(sides)", StringComparison.Ordinal) && surface.Contains("if (msg == WmNcHitTest && PassesPointer)", StringComparison.Ordinal),
            "the player's overlay goes under the lowest side window; a mirror's surface lets the pointer through (double-click: full screen)");
    }

    private static IEnumerable<UIElement> Children(this FrameworkElement element) => element is Panel panel ? panel.Children.OfType<UIElement>() : [];
}
