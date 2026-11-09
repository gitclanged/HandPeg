using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4D (Subagent A): the dock splitter's range and its double-click reset, the unified Layout selector (Default pill,
/// footer), the default startup layout and the Vertical Creator Split.
/// dock-vertical-creator-layout (window-free): the fifth built-in layout's shape and shares, its Filmstrip Then, its place in the built-in
/// list and the first-run window. dock-default-startup-layout: MainWindow.StartupLayoutJson on a bare window (its fields put in by hand, a
/// dock never shown): the Default layout by name or "saved:N", the arrangement as it was left kept while it has that shape (dragged borders,
/// a pane put in, a studio opened or closed, a Pane Docker), an unknown, empty or deleted Default falling back to it, Then run only when the
/// layout is taken up; the one Load before the first frame; settings from before 5.4D (AppSettings.Load's step 9) keeping their arrangement,
/// the built-in layout of its shape then named the Default; and the Default's place written over staying the Default. layout-selector-unified
/// (source and XAML): the click's menu retired, the Layout Selector opened by a click too (from the keyboard, on its first card), its cards
/// four to a row with the Default pill and a saved card's "×", the footer card, Reset Layout in the Views menu only, the keyboard let go after
/// a click with the pointer, a preview put back before a side window opens or closes, no opening mid-switch, closed when the window is not
/// active. dock-splitter-range (on the WPF Application): a real shown window 1280 x 760 DIPs, its panes laid out by a real DockManager with the
/// least widths MainWindow gives them, in Standard Editing, the Vertical &amp; Shorts Studio and the Vertical Creator Split: every border that
/// shares the room drags both ways, a drag cascades to the part beyond a neighbour at its least, a step (Nudge) moves it 16 DIPs (the borders
/// take no keyboard focus), a double-click evens the two parts beside it (or puts the scrubber's height back), the Media Browser's Filmstrip
/// view raises its column's least width in place, the arrangement from before a drag loaded lays the window out again, and a drag after Reset
/// twice is saved; the joined header's real width printed.
/// </summary>
public static class LayoutSelectorTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The settings tabs' pane ids after 5.4B, as the other suites have them.
    private static readonly IReadOnlyList<string> TabIds = ["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];

    /// <summary>The window-free cases (and those on a window of their own), before the UI-scale tests.</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("dock-vertical-creator-layout", "# The Vertical Creator Split: the player the whole height at the left, the tools over the playback controls and the Timeline-first tabs in the middle, the header over the media (Filmstrip) at the right.", VerticalCreator);
        yield return Case("dock-default-startup-layout", "# The Default layout at start-up: by name or saved:N, the arrangement as it was left kept while it has that shape (a studio open or not), an unknown or deleted one falling back to it, none (settings from before) named from it, in the one Load.", DefaultStartup);
        yield return Case("layout-selector-unified", "# The Layout Selector is the one Layout UI: a click opens it too, the cards four to a row with the Default pill, the footer card, the menu retired.", SelectorSource);
    }

    /// <summary>The cases that need the WPF Application the UI-scale tests make: run last, after header-flyouts-placement.</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("dock-splitter-range", "# The dock's borders at 1280 x 760 DIPs: each drags both ways, cascading past a part at its least; a double-click evens it; the Filmstrip view's least width set in place.", SplitterRange);
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), "src", .. parts]));

    /// <summary>A method's text in a source file: from its signature to the first line that closes it at its own indent.</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static string Num(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);

    // ----- dock-vertical-creator-layout -----

    private static void VerticalCreator(Action<bool, string> expect)
    {
        var main = MainWindow.VerticalCreatorLayout(TabIds).Main;
        const string shape = "H([Player];V([Effects,Keyframes,Layout,SubtitleStudio];[Playback];[Timeline];[Presets];[Tab:Audio,Tab:Command Preview,Tab:Export,Tab:Filters,UnifiedTimeline]);V([Header];[Media]))";
        expect(DockManager.Shape(main) == shape, "its shape: " + DockManager.Shape(main));
        expect(DockManager.Shape(DockTree.Tidy(MainWindow.VerticalCreatorLayout(TabIds).Main)!) == shape, "tidying leaves it as it is (no split inside a split of the same way)");

        expect(main is { Vertical: false } && main.Sizes.SequenceEqual([28.0, 46, 26]) && main.Children[0].Panes.SequenceEqual(["Player"]),
            "side by side: the player 28 of the width, the whole height (a 9:16 frame stands tall), the middle 46, the right 26");
        var center = main.Children[1];
        var tools = center.Children[0];
        expect(center is { Vertical: true } && tools.Panes.SequenceEqual(["Effects", "Keyframes", "Layout", MainWindow.SubtitleStudioId]) && tools.Active == "Effects"
               && center.Sizes[0] == 48 && center.Sizes[^1] == 52,
            "the middle: Effects | Keyframes | Canvas | Subtitle Studio as tabs (the effects shown) 48 of its height, the tabs below 52");
        expect(center.Children[1].Panes.SequenceEqual(["Playback"]) && center.Children[2].Panes.SequenceEqual(["Timeline"]) && center.Children[3].Panes.SequenceEqual(["Presets"]),
            "under the tools the playback controls, the Sequence Overview Scrubber and the Preset Bar (rows as tall as their content)");
        var bottom = center.Children[4];
        expect(bottom.Panes[0] == "UnifiedTimeline" && bottom.Active == "UnifiedTimeline" && TabIds.All(bottom.Panes.Contains) && bottom.Panes.Count == TabIds.Count + 1,
            "then the timeline, first and shown among every settings tab");
        var right = main.Children[2];
        expect(right is { Vertical: true } && right.Children[0].Panes.SequenceEqual(["Header"]) && right.Children[1].Panes.SequenceEqual(["Media"]),
            "the right column: the header joined over the media");
        expect(!main.AllPanes().Contains("Meter") && !main.AllPanes().Any(p => p is MainWindow.VoiceoverStudioId or MainWindow.RenderQueueId or MainWindow.VideoScopesId),
            "the Master Meter and the other studios are left out (the meter goes in with the effects when shown)");

        var states = MainWindow.BuiltInLayoutStates(TabIds);
        expect(states.Count == 5 && states[^1].Name == "Vertical Creator Split" && MainWindow.VerticalCreatorName == "Vertical Creator Split"
               && DockManager.Shape(states[^1].Layout.Main) == shape && states.Select(s => DockManager.Shape(s.Layout.Main)).Distinct().Count() == 5,
            "the fifth of five built-in layouts, named Vertical Creator Split, each of a shape of its own: " + string.Join(", ", states.Select(s => s.Name)));

        var layouts = Read("MainWindow.Layouts.cs");
        expect(Regex.IsMatch(layouts, @"new\(VerticalCreatorName, ""[^""]*Subtitle Studio[^""]*"", VerticalCreatorLayout\(TabIds\), null, ShowShortsMedia\)"),
            "its choice in BuiltInLayouts tells of the Subtitle Studio and sets the Media Browser to Filmstrip when taken up (ShowShortsMedia, its Then)");

        var firstRun = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Dialogs", "FirstRunWindow.xaml"));
        var card = firstRun.Descendants().FirstOrDefault(e => e.Attribute(X + "Name")?.Value == "VerticalCreatorLayoutCard");
        var firstRunCode = Read("Dialogs", "FirstRunWindow.xaml.cs");
        expect(card?.Attribute("Tag")?.Value == "Vertical Creator Split" && card.Attribute("Checked")?.Value == "Workspace_Checked"
               && Method(firstRunCode, "private string ChosenWorkspace()").Contains("VerticalCreatorLayoutCard", StringComparison.Ordinal),
            "the first-run window offers it as a fifth card (its Tag the layout's name), and ChosenWorkspace knows it");
    }

    // ----- dock-default-startup-layout -----

    /// <summary>A dock manager whose window is never shown, with every pane MainWindow registers (the studios as it registers them).</summary>
    private static DockManager NewDock()
    {
        var dock = new DockManager(new Window(), new ContentControl(), () => MainWindow.StandardLayout(TabIds));
        foreach (var id in new[] { "Player", "Playback", "Timeline", "UnifiedTimeline", "Layout", "Header", "Media", "Keyframes", "Effects", "Meter", "Presets" }.Concat(TabIds))
        {
            var content = new Border();
            dock.Register(new DockPane { Id = id, Title = id, Content = () => content, Handle = id.StartsWith("Tab:", StringComparison.Ordinal) ? PaneHandle.Tab : PaneHandle.TitleSlot });
        }

        foreach (var (id, _, _, _) in MainWindow.Studios)
            dock.Register(MainWindow.StudioPane(id, () => new Border(), null, 180, 140));
        return dock;
    }

    private static string Json(DockLayoutState state) => System.Text.Json.JsonSerializer.Serialize(state, AppSettingsJsonContext.Default.DockLayoutState);

    private static void DefaultStartup(Action<bool, string> expect)
    {
        UpgradedDefault(expect);

        var settings = AppSettings.Current;
        var kept = (settings.DefaultStartupLayout, settings.DockLayout, settings.SavedLayouts, settings.SideWindows, settings.BrowserView, settings.FirstRunLayout);
        var slot = PaneTitleSlot.Of("Media");
        MediaBrowserPane? media = null;
        MainViewModel? viewModel = null;
        try
        {
            // A bare window (none of its XAML read): the settings tabs, a dock never shown, a Media Browser and a view model (to save the
            // Default it adopts) put in by hand.
            var window = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            var type = typeof(MainWindow);
            var dock = NewDock();
            type.GetField("_tabPanes", Hidden)!.SetValue(window, TabIds.Select(id => (id, new TabItem())).ToList());
            type.GetField("_dock", Hidden)!.SetValue(window, dock);
            viewModel = new MainViewModel();
            type.GetField("_viewModel", Hidden)!.SetValue(window, viewModel);
            settings.BrowserView = "Grid";
            media = new MediaBrowserPane();
            type.GetField("MediaBrowser", Hidden)!.SetValue(window, media);
            string? Startup() => (string?)type.GetMethod("StartupLayoutJson", Hidden)!.Invoke(window, null);
            bool Shaped(string? json, DockLayoutState layout) => DockLayoutState.Read(json) is { } read && DockManager.SameShape(dock.Prepared(read), dock.Prepared(layout));

            var standard = MainWindow.StandardLayout(TabIds);
            var shorts = MainWindow.ShortsLayout(TabIds);
            var creator = MainWindow.VerticalCreatorLayout(TabIds);
            expect(settings.GetType().GetProperty(nameof(AppSettings.DefaultStartupLayout)) is not null && new AppSettings().DefaultStartupLayout == MainWindow.DefaultLayoutName
                   && MainWindow.DefaultLayoutName == "Standard Editing",
                "AppSettings.DefaultStartupLayout starts as Standard Editing (MainWindow.DefaultLayoutName)");

            // Standard Editing as it was left: its borders dragged, the Master Meter put in by the dock, saved as the dock saves it.
            var dragged = dock.Prepared(standard);
            dragged.Main.Children[0].Sizes[0] = 40;
            dragged.Main.Children[0].Sizes[2] = 38;
            var left = Json(dragged);
            (settings.DefaultStartupLayout, settings.DockLayout, settings.SideWindows, settings.SavedLayouts) = ("Standard Editing", left, null, []);
            expect(Startup() == left, "the Default Standard Editing, the arrangement left in Standard Editing (borders dragged, the Master Meter put in): kept as it was");

            settings.DockLayout = Json(dock.Prepared(shorts));
            var json = Startup();
            expect(json != settings.DockLayout && Shaped(json, standard) && media.ViewMode == MediaBrowserView.Grid,
                "the arrangement left in the Vertical & Shorts Studio: Standard Editing instead (its Then, none, leaves the Media Browser on Grid)");

            (settings.DefaultStartupLayout, settings.DockLayout) = ("Vertical Creator Split", left);
            json = Startup();
            expect(Shaped(json, creator) && media.ViewMode == MediaBrowserView.Filmstrip && settings.BrowserView == "Filmstrip",
                "the Default Vertical Creator Split over a Standard arrangement: the Vertical Creator Split, its Then run first (the Media Browser on Filmstrip)");

            media.ViewMode = MediaBrowserView.Grid;
            var creatorLeft = dock.Prepared(creator);
            creatorLeft.Main.Sizes[0] = 33;
            settings.DockLayout = Json(creatorLeft);
            expect(Startup() == settings.DockLayout && media.ViewMode == MediaBrowserView.Grid,
                "the arrangement left in the Vertical Creator Split, its player dragged wider: kept, and its Then not run (the view the user left stays)");

            // A Pane Docker holding the Media pane: the main window's arrangement lacks it, and is still Standard Editing's shape.
            var docked = dock.Prepared(standard);
            docked.Main = DockTree.Remove(docked.Main, "Media")!;
            var sides = SideWindowStates.Write([new SideWindowState { Id = "s1", Mode = SideWindowState.PanesMode, Tree = DockNode.Group("Media") }]);
            (settings.DefaultStartupLayout, settings.DockLayout, settings.SideWindows) = ("Standard Editing", Json(docked), sides);
            expect(Startup() == settings.DockLayout, "with the Media pane in a Pane Docker (AppSettings.SideWindows), Standard Editing's arrangement without it is still kept");
            settings.SideWindows = null;

            // A studio opened (DockPane.Optional: the arrangement holds it while it is shown) or closed is no other layout.
            var withQueue = dock.Prepared(standard);
            withQueue.Main.GroupOf("Effects")!.Panes.Add(MainWindow.RenderQueueId);
            withQueue.Main.Children[0].Sizes[0] = 41;
            settings.DockLayout = Json(withQueue);
            expect(Startup() == settings.DockLayout && dock.LoadsAlike(settings.DockLayout, standard, null),
                "Standard Editing left with the Render Queue open and a border dragged: kept as it was (LoadsAlike leaves the studios out of both)");
            var shortsClosed = dock.Prepared(shorts);
            shortsClosed.Main = DockTree.Remove(shortsClosed.Main, MainWindow.SubtitleStudioId)!;
            (settings.DefaultStartupLayout, settings.DockLayout) = ("Vertical & Shorts Studio", Json(shortsClosed));
            media.ViewMode = MediaBrowserView.Grid;
            expect(Startup() == settings.DockLayout && media.ViewMode == MediaBrowserView.Grid,
                "the Vertical & Shorts Studio left with its Subtitle Studio closed: kept as it was, its Then not run (the Media Browser stays on Grid)");
            var builtIns = MainWindow.BuiltInLayoutStates(TabIds);
            expect(Enumerable.Range(0, builtIns.Count).All(i => Enumerable.Range(0, builtIns.Count).All(j => i == j || !dock.LoadsAlike(Json(dock.Prepared(builtIns[i].Layout)), builtIns[j].Layout, null))),
                "with the studios left out, no two of the five built-in layouts load alike (so one arrangement names at most one of them)");
            (settings.DefaultStartupLayout, settings.DockLayout) = ("Standard Editing", left);

            // The user's own layouts, by place.
            var assembly = MainWindow.AssemblyLayout(TabIds);
            settings.SavedLayouts = [new SavedDockLayout { Slot = 2, Name = "Mine", Layout = Json(assembly) }];
            (settings.DefaultStartupLayout, settings.DockLayout) = ("saved:2", left);
            expect(Shaped(Startup(), assembly), "saved:2: the user's own layout in place 2 (here Assembly & Media's arrangement)");

            // Place 2 written over (Save Current Layout): the place is the key, so the layout now there is the Default, and the status says so.
            var inspector = MainWindow.InspectorLayout(TabIds);
            settings.SavedLayouts = [new SavedDockLayout { Slot = 2, Name = "Temp", Layout = Json(inspector) }];
            var save = Method(Read("MainWindow.Layouts.cs"), "private void SaveCurrentLayout_Click(");
            expect(Shaped(Startup(), inspector) && settings.DefaultStartupLayout == "saved:2"
                   && save.Contains("AppSettings.Current.DefaultStartupLayout == $\"saved:{dialog.Slot}\"", StringComparison.Ordinal)
                   && save.Contains("$\"Layout saved as \\\"{dialog.LayoutName}\\\": your Default layout", StringComparison.Ordinal),
                "the Default's place 2 written over with another layout: still the Default (saved:2, now that layout), and Save Current Layout says \"Layout saved as \"Temp\": your Default layout\"");
            settings.SavedLayouts = [new SavedDockLayout { Slot = 2, Name = "Mine", Layout = Json(assembly) }];
            settings.DefaultStartupLayout = "saved:3";
            expect(Startup() == left, "saved:3 with no layout in place 3 (deleted): the arrangement as it was left");
            settings.DefaultStartupLayout = "Mine";
            expect(Startup() == left, "a saved layout's name is no key (only its place is): the arrangement as it was left");

            settings.DefaultStartupLayout = "Nonexistent Layout";
            expect(Startup() == left && settings.DefaultStartupLayout == "Nonexistent Layout", "the Default \"Nonexistent Layout\": the arrangement as it was left");

            // No Default at all (null: settings from before 5.4D; or empty): the arrangement as it was left, and the built-in layout of its shape
            // named the Default (saved), so its card has the star.
            foreach (var none in new[] { "", null })
            {
                settings.DefaultStartupLayout = none;
                expect(Startup() == left && settings.DefaultStartupLayout == "Standard Editing",
                    $"the Default {(none is null ? "null" : "\"\"")}: the arrangement as it was left (Standard Editing's, dragged), and Standard Editing named the Default");
            }

            var colorLeft = dock.Prepared(MainWindow.InspectorLayout(TabIds));
            colorLeft.Main.Children[0].Sizes[0] = 61;
            (settings.DefaultStartupLayout, settings.DockLayout) = (null, Json(colorLeft));
            expect(Startup() == settings.DockLayout && settings.DefaultStartupLayout == "Color & Effects",
                "a 5.3 user's Color & Effects arrangement, dragged, with no Default: kept as it was, and Color & Effects named the Default");
            var odd = dock.Prepared(standard);
            odd.Main = DockTree.Remove(odd.Main, "Media")!;
            odd.Main = DockTree.InsertBeside(odd.Main, odd.Main, DockZone.Left, DockNode.Group("Media"), 0.2);
            (settings.DefaultStartupLayout, settings.DockLayout) = (null, Json(odd));
            expect(Startup() == settings.DockLayout && settings.DefaultStartupLayout is null,
                "an arrangement of no built-in layout's shape (the media moved to the far left), with no Default: kept as it was, and no Default named");
            settings.DockLayout = left;

            (settings.DefaultStartupLayout, settings.DockLayout) = ("Standard Editing", null);
            expect(Shaped(Startup(), standard), "no arrangement saved yet: the Default layout");
            settings.DockLayout = "{ not json";
            expect(Shaped(Startup(), standard), "an arrangement that cannot be read: the Default layout");

            // The dock's own check, window-free: the shapes the dock would lay out, each taken up as Load and Apply take it.
            expect(dock.LoadsAlike(left, standard, null) && !dock.LoadsAlike(left, shorts, null) && !dock.LoadsAlike(null, standard, null) && !dock.LoadsAlike(Json(dock.Prepared(shorts)), standard, null),
                "DockManager.LoadsAlike: Standard's arrangement loads alike Standard Editing, not the Shorts Studio; nothing saved, or another shape, is not alike");

            // The one dock build before the first frame (Law 1), and the first-run choice.
            var docking = Read("MainWindow.Docking.cs");
            var layouts = Read("MainWindow.Layouts.cs");
            expect(Regex.Matches(docking, @"_dock\.Load\(").Count == 1
                   && docking.Contains("_dock.Load(sideWindows: AppSettings.Current.SideWindows, saved: TakeFirstRunLayout() ?? StartupLayoutJson(), showFirst: TimelinePaneId);", StringComparison.Ordinal),
                "InitializeDocking has one Load: the first-run choice, else StartupLayoutJson, opening on the Timeline");
            var startup = Method(layouts, "private string? StartupLayoutJson()");
            expect(startup.Contains("AppSettingsJsonContext.Default.DockLayoutState", StringComparison.Ordinal) && !startup.Contains("_dock.Load(", StringComparison.Ordinal)
                   && !startup.Contains("_dock.Apply", StringComparison.Ordinal),
                "StartupLayoutJson serialises through the generated metadata and builds nothing (no Load, no Apply)");
            var firstRun = Method(layouts, "private string? TakeFirstRunLayout()");
            expect(firstRun.Contains("AppSettingsJsonContext.Default.DockLayoutState", StringComparison.Ordinal) && firstRun.Contains("s.DefaultStartupLayout", StringComparison.Ordinal),
                "TakeFirstRunLayout serialises through the generated metadata and makes the first-run choice the Default layout too");
            var reset = Method(layouts, "private void ResetToDefaultLayout()");
            var main = Read("MainWindow.xaml.cs");
            expect(reset.Contains("_dock.Reset(choice?.Layout);", StringComparison.Ordinal) && Method(main, "private void ResetPanesLayout_Click(").Contains("ResetToDefaultLayout();", StringComparison.Ordinal)
                   && docking.Contains("ResetLayout = () => ResetPanesLayout_Click(", StringComparison.Ordinal),
                "Reset Layout (the Views menu, and a tab's menu) puts back the Default layout, not always Standard Editing");
        }
        finally
        {
            // The adopted Default's save, still waiting (MainViewModel.SaveSettingsSoon), is let go: the test's settings never reach the file.
            (typeof(MainViewModel).GetField("_settingsSave", Hidden)?.GetValue(viewModel) as CancellationTokenSource)?.Cancel();
            media?.Release();
            PaneTitleSlot.Restore("Media", slot);
            (settings.DefaultStartupLayout, settings.DockLayout, settings.SavedLayouts, settings.SideWindows, settings.BrowserView, settings.FirstRunLayout) = kept;
        }
    }

    /// <summary>
    /// AppSettings.Load's step 9 (5.4D) on settings files of the test's own, in the test's data folder (HANDPEG_DATA_DIR): what was there is
    /// put back after. A file from before 5.4D that kept an arrangement (no DefaultStartupLayout key) has no Default; a first start's, one with
    /// no arrangement, and one whose Default was chosen keep theirs.
    /// </summary>
    private static void UpgradedDefault(Action<bool, string> expect)
    {
        var file = AppSettings.FilePath;
        var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        var load = typeof(AppSettings).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes)!;
        var arrangement = Json(MainWindow.InspectorLayout(TabIds));
        AppSettings Loaded(int version, string? dockLayout, string? defaultLayout)
        {
            var old = new AppSettings { ViewDefaultsVersion = version, FirstRunComplete = true, DockLayout = dockLayout, DefaultStartupLayout = defaultLayout };
            var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(old, AppSettingsJsonContext.Default.AppSettings))!.AsObject();
            if (defaultLayout is null)
                node.Remove(nameof(AppSettings.DefaultStartupLayout));
            File.WriteAllText(file, node.ToJsonString());
            return (AppSettings)load.Invoke(null, null)!;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var upgraded = Loaded(6, arrangement, null);
            expect(upgraded is { ViewDefaultsVersion: >= 9, DefaultStartupLayout: null } && upgraded.DockLayout == arrangement,
                "a 5.3 settings file (ViewDefaultsVersion 6, no DefaultStartupLayout) that kept an arrangement: no Default (null), the arrangement kept");
            expect(Loaded(0, arrangement, null).DefaultStartupLayout == MainWindow.DefaultLayoutName,
                "a first start's file (version 0): Standard Editing, as a fresh install has");
            expect(Loaded(6, null, null).DefaultStartupLayout == MainWindow.DefaultLayoutName,
                "a file from before with no arrangement kept: Standard Editing");
            expect(Loaded(8, arrangement, "Color & Effects").DefaultStartupLayout == "Color & Effects" && Loaded(9, arrangement, "Standard Editing").DefaultStartupLayout == "Standard Editing",
                "a Default chosen (Color & Effects), or a file of version 9: kept as it is");

            // Step 10 (5.4D, the user's call): the status bar hidden once for everyone; one shown again since (version 10) stays shown.
            AppSettings WithStatusBar(int version)
            {
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new AppSettings { ViewDefaultsVersion = version, FirstRunComplete = true, ShowStatusBar = true }, AppSettingsJsonContext.Default.AppSettings));
                return (AppSettings)load.Invoke(null, null)!;
            }
            expect(!WithStatusBar(9).ShowStatusBar && WithStatusBar(10).ShowStatusBar && !new AppSettings().ShowStatusBar,
                "the status bar: hidden once for a file from before step 10 (the title bar carries the work), kept shown when turned on since, off for a fresh install");
        }
        finally
        {
            if (original is not null)
                File.WriteAllBytes(file, original);
            else
                File.Delete(file);
        }
    }

    // ----- layout-selector-unified -----

    private static XElement? Named(XDocument xaml, string name) => xaml.Descendants().FirstOrDefault(e => e.Attribute(X + "Name")?.Value == name);

    private static void SelectorSource(Action<bool, string> expect)
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "MainWindow.xaml"));
        string[] retired = ["LayoutFlyout", "LayoutFlyoutPanel", "SideWindowCheck", "BuiltInLayoutRows", "SavedLayoutRows", "SavedLayoutsTitle"];
        expect(retired.All(n => Named(xaml, n) is null), "the click's menu is retired from MainWindow.xaml: " + string.Join(", ", retired));

        var layouts = Read("MainWindow.Layouts.cs");
        var main = Read("MainWindow.xaml.cs");
        var docking = Read("MainWindow.Docking.cs");
        string[] gone = ["OpenLayoutMenu", "CloseLayoutMenu", "FillLayoutRows", "LayoutFlyout_KeyDown", "_layoutMenuFrom", "TakeUpLayout", "LayoutFlyout"];
        expect(gone.All(g => !layouts.Contains(g, StringComparison.Ordinal) && !main.Contains(g, StringComparison.Ordinal) && !docking.Contains(g, StringComparison.Ordinal)),
            "and from the code: " + string.Join(", ", gone));
        expect(main.Contains("CanOpen = () => !_switchingTab,", StringComparison.Ordinal) && !main.Contains("PreviewMouseDown += CloseLayout", StringComparison.Ordinal),
            "resting on the Layout button opens nothing while a project tab is being switched (CanOpen); no press in the window closes it");
        var deactivated = Method(layouts, "private void CloseLayoutSelectorOnDeactivated()");
        expect(main.Contains("Deactivated += (_, _) => CloseLayoutSelectorOnDeactivated();", StringComparison.Ordinal)
               && deactivated.IndexOf("RestoreLayoutBeforePreview();", StringComparison.Ordinal) is var putBack and >= 0 && putBack < deactivated.IndexOf("_layoutHover.Close();", StringComparison.Ordinal),
            "the window deactivated (Alt+Tab): a preview put back, then the selector closed (it is topmost)");

        var click = Method(layouts, "private void LayoutButton_Click(");
        var keys = Method(layouts, "private void LayoutButton_PreviewKeyDown(");
        var onFirst = Method(layouts, "private void OpenLayoutSelectorOnFirstCard(");
        expect(click.Contains("if (!LayoutSelector.IsOpen", StringComparison.Ordinal) && click.Contains("_layoutHover.Open(button);", StringComparison.Ordinal) && !click.Contains("Close", StringComparison.Ordinal),
            "a click on the Layout button opens the Layout Selector when it is not open, and never closes it");
        expect(keys.Contains("Key.Down or Key.Apps", StringComparison.Ordinal) && keys.Contains("OpenLayoutSelectorOnFirstCard(button);", StringComparison.Ordinal)
               && onFirst.Contains("LayoutCards.Children.OfType<Button>().FirstOrDefault()?.Focus()", StringComparison.Ordinal)
               && click.Contains("if (FromKeyboard)", StringComparison.Ordinal) && click.Contains("OpenLayoutSelectorOnFirstCard(button);", StringComparison.Ordinal),
            "Down, the menu key, Enter or Space (a click from the keyboard) opens it with the first card chosen");
        expect(Regex.IsMatch(keys, @"Key\.Escape && LayoutSelector\.IsOpen\)\s*\{\s*_layoutHover\.Close\(\);"),
            "Escape on the Layout button closes the selector it opened");
        expect(Regex.IsMatch(layouts, @"FromKeyboard => InputManager\.Current\.MostRecentInputDevice is KeyboardDevice;"),
            "what came from the keyboard: InputManager's most recent device is the keyboard (not the pointer, a touch or a pen)");

        // A click with the pointer on a pill, a "×" or the footer's CheckBox lets the keyboard go (HoverFlyout holds the selector open while it
        // has it): back where it was before the selector opened, the selector let go, so the pointer leaving it closes it.
        var remake = Method(layouts, "private void RemakeLayoutCards()");
        var leave = Method(layouts, "private void LeaveSelectorToPointer()");
        expect(Regex.IsMatch(remake, @"if \(!FromKeyboard\)\s*LeaveSelectorToPointer\(\);\s*else if \(focused >= 0") && Method(layouts, "private void PerTabLayoutsBox_Click(").Contains("LeaveSelectorToPointer();", StringComparison.Ordinal)
               && leave.Contains("_focusBeforeSelector is UIElement", StringComparison.Ordinal) && leave.Contains("Keyboard.ClearFocus();", StringComparison.Ordinal) && leave.Contains("_layoutHover.Release();", StringComparison.Ordinal)
               && Method(layouts, "private void OpeningLayoutSelector()").Contains("_focusBeforeSelector = Keyboard.FocusedElement;", StringComparison.Ordinal),
            "a pill, a × or the CheckBox clicked with the pointer: the keyboard back where it was as the selector opened, the selector released; from the keyboard, the card keeps it");
        var button = Named(xaml, "LayoutButton");
        expect(button?.Attribute("ToolTip")?.Value is { } tip && tip.Contains("or click", StringComparison.Ordinal) && tip.Contains("Save Current Layout", StringComparison.Ordinal)
               && tip.Contains("Reset Layout is in the Views menu", StringComparison.Ordinal),
            "the Layout button's tooltip tells of the click, the selector's footer and Reset Layout's place");

        // Four cards to a row: 8 cards (five built-in, three of the user's own) in two rows, so the selector fits a 1280 x 800 DIP work area.
        var cards = Named(xaml, "LayoutCards");
        var width = double.Parse(cards?.Attribute("Width")?.Value ?? "0", CultureInfo.InvariantCulture);
        var picture = (double)typeof(MainWindow).GetField("CardPictureWidth", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var card = picture + 4 + 12 + 3 + 8; // the picture's padding, the card's padding, border and margin (MainWindow.xaml's LayoutCard)
        var perRow = (int)Math.Floor(width / card);
        var rows = (int)Math.Ceiling((5 + AppSettings.SavedLayoutSlots) / (double)perRow);
        expect(perRow == 4 && rows == 2 && Named(xaml, "ThemeCards")?.Attribute("Width")?.Value == cards?.Attribute("Width")?.Value,
            $"the cards {Num(card)} DIPs wide in {Num(width)}: {perRow} to a row, {5 + AppSettings.SavedLayoutSlots} cards in {rows} rows; the themes as wide");

        var footer = Named(xaml, "LayoutFooter");
        expect(footer is not null && footer.Attribute("Background")?.Value == "{DynamicResource InspectorCardBrush}" && footer.Attribute("BorderBrush")?.Value == "{DynamicResource InspectorCardBorderBrush}"
               && footer.Attribute("CornerRadius")?.Value == "6" && footer.Attribute("Padding")?.Value == "12" && footer.Attribute("BorderThickness")?.Value == "1"
               && footer.Ancestors().Any(a => a.Attribute(X + "Name")?.Value == "LayoutSelectorPanel")
               && footer.ElementsBeforeSelf().Any(e => e.Attribute(X + "Name")?.Value == "ThemeCards"),
            "the footer: a card under the themes (InspectorCardBrush edged InspectorCardBorderBrush, 1 px, 6 px corners, 12 px inside: CLAUDE.md 10.3)");
        var inFooter = footer?.Descendants().ToList() ?? [];
        bool Has(string attribute, string value) => inFooter.Any(e => e.Attribute(attribute)?.Value == value);
        expect(inFooter.Any(e => e.Attribute(X + "Name")?.Value == "PerTabLayoutsBox" && e.Name.LocalName == "CheckBox" && e.Attribute("Content")?.Value == "Each project tab remembers its own layout")
               && inFooter.Any(e => e.Attribute(X + "Name")?.Value == "SideWindowRows") && Has("Click", "SideWindowRow_Click") && Has("Click", "SaveCurrentLayout_Click")
               && !Has("Click", "ResetPanesLayout_Click"),
            "in it: Each project tab remembers its own layout, + New Side Window, the side windows' rows (SideWindowRows) and Save Current Layout; no Reset Layout");
        var resets = xaml.Descendants().Where(e => e.Attribute("Click")?.Value == "ResetPanesLayout_Click").ToList();
        expect(resets.Count == 1 && resets[0].Ancestors().Any(a => a.Attribute(X + "Name")?.Value == "ViewsPopup"),
            "Reset Layout has one home, the Views menu (CLAUDE.md 10.1)");

        var perTab = Method(layouts, "private void PerTabLayoutsBox_Click(");
        var opening = Method(layouts, "private void OpeningLayoutSelector()");
        expect(perTab.Contains("_viewModel.SavePaneLayout(s => s.PerTabWindowLayouts = on);", StringComparison.Ordinal)
               && opening.Contains("PerTabLayoutsBox.IsChecked = AppSettings.Current.PerTabWindowLayouts;", StringComparison.Ordinal) && opening.Contains("UpdateSideWindowCheck();", StringComparison.Ordinal),
            "the CheckBox saves AppSettings.PerTabWindowLayouts at once and shows it as it is each time the selector opens (Settings sets it too)");
        var save = Method(layouts, "private void SaveCurrentLayout_Click(");
        var dialogAt = save.IndexOf("ShowDialog", StringComparison.Ordinal);
        expect(save.IndexOf("RestoreLayoutBeforePreview();", StringComparison.Ordinal) is var restore and >= 0 && restore < dialogAt
               && save.IndexOf("_layoutHover.Close();", StringComparison.Ordinal) is var close and >= 0 && close < dialogAt && save.Contains("_dock.Save()", StringComparison.Ordinal),
            "Save Current Layout puts a preview back and closes the selector before its modal window, then saves the arrangement through the dock");
        var newSide = Method(docking, "private void SideWindowRow_Click(");
        var sideRows = Method(docking, "private void UpdateSideWindowCheck()");
        expect(newSide.IndexOf("RestoreLayoutBeforePreview();", StringComparison.Ordinal) is var restored and >= 0 && restored < newSide.IndexOf("_layoutHover.Close();", StringComparison.Ordinal)
               && newSide.IndexOf("_layoutHover.Close();", StringComparison.Ordinal) < newSide.IndexOf("_dock.OpenSideWindow", StringComparison.Ordinal),
            "+ New Side Window puts a preview back, then closes the selector, before the window opens (and saves the arrangement)");
        expect(Regex.IsMatch(sideRows, @"RestoreLayoutBeforePreview\(\);\s*_layoutHover\.Close\(\);\s*_dock\.BringSideWindowForward\(id\);") && Regex.IsMatch(sideRows, @"RestoreLayoutBeforePreview\(\);\s*_dock\.CloseSideWindow\(id\);"),
            "a side window's row and its close button put a preview back first too");
        var cardsElement = Named(xaml, "LayoutCards");
        expect(cardsElement?.Attribute("IsKeyboardFocusWithinChanged")?.Value == "LayoutCards_IsKeyboardFocusWithinChanged"
               && Regex.IsMatch(Method(layouts, "private void LayoutCards_IsKeyboardFocusWithinChanged("), @"e\.NewValue is false && LayoutSelector\.IsOpen && !_remakingCards && !LayoutCards\.IsMouseOver\)\s*RestoreLayoutBeforePreview\(\);"),
            "the keyboard leaving the cards (Tab to the footer) with the selector open puts a preview back, as the pointer leaving them does");

        // The cards' pills.
        var make = Method(layouts, "private void MakeLayoutCards()");
        expect(make.Contains("\"★ Default\"", StringComparison.Ordinal) && make.Contains("\"☆ Set Default\"", StringComparison.Ordinal) && make.Contains("\"×\"", StringComparison.Ordinal)
               && Regex.Matches(make, @"e\.Handled = true;").Count == 2 && make.Contains("SetDefaultLayout(choice)", StringComparison.Ordinal) && make.Contains("DeleteSavedLayout(place)", StringComparison.Ordinal),
            "each card has its pill (★ Default, else ☆ Set Default) and a saved card its ×, their clicks handled so the card's own (KeepLayout) never fires");
        expect(make.Contains("pill.Visibility = isDefault || pointed", StringComparison.Ordinal) && make.Contains("card.IsMouseOver || card.IsKeyboardFocusWithin", StringComparison.Ordinal),
            "★ Default always shown; ☆ Set Default and × while the card is pointed at or has the keyboard");
        expect(make.Contains("+ \"|\" + defaultKey", StringComparison.Ordinal), "the Default layout is part of what the cards are drawn from (_cardsKey)");
        var set = Method(layouts, "private void SetDefaultLayout(");
        expect(set.Contains("s.DefaultStartupLayout = LayoutKey(choice)", StringComparison.Ordinal) && set.Contains("RemakeLayoutCards();", StringComparison.Ordinal) && set.Contains("StatusText", StringComparison.Ordinal),
            "setting the Default saves it at once (a built-in layout by name, the user's own as saved:N), draws the cards again and says so");
        var delete = Method(layouts, "private void DeleteSavedLayout(");
        expect(delete.IndexOf("RestoreLayoutBeforePreview();", StringComparison.Ordinal) is 0 or > 0 && delete.Contains("s.DefaultStartupLayout = DefaultLayoutName;", StringComparison.Ordinal)
               && delete.Contains("RemakeLayoutCards();", StringComparison.Ordinal) && !delete.Contains("_layoutHover.Close", StringComparison.Ordinal),
            "deleting one of the user's own puts the preview back first, leaves the selector open, and when it was the Default makes Standard Editing the Default again");
        expect(Method(layouts, "private static string LayoutKey(").Contains("$\"saved:{slot}\"", StringComparison.Ordinal),
            "AppSettings.DefaultStartupLayout keys: a built-in layout's name, saved:N for the user's own in place N");
        expect(Named(xaml, "LayoutSelector")?.Attributes().All(a => a.Name.LocalName is not ("Placement" or "PlacementTarget" or "HorizontalOffset" or "VerticalOffset")) == true
               && Method(layouts, "private void PlaceLayoutSelector(").Contains("PlaceHeaderFlyout(LayoutSelector", StringComparison.Ordinal),
            "the selector is still placed by PlaceLayoutSelector (UiScale.PlaceAgainst, CLAUDE.md 9)");
    }

    // ----- dock-splitter-range -----

    private static void Idle()
    {
        for (var i = 0; i < 4; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<T> Under<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
                yield return found;
            foreach (var deeper in Under<T>(child))
                yield return deeper;
        }
    }

    // The dock's border grips (DockManager.SeamThumb), in the order they are laid out.
    private static List<Thumb> Borders(DependencyObject root) => Under<Thumb>(root).Where(t => t.GetType().Name == "SeamThumb").ToList();

    private static T? Get<T>(Thumb border, string property) where T : class => border.GetType().GetProperty(property)?.GetValue(border) as T;

    private static bool IsVertical(Thumb border) => (bool)border.GetType().GetProperty("Vertical")!.GetValue(border)!;

    // A border that shares the room (its keyboard moves it): not one that only makes the scrubber taller.
    private static bool Shares(Thumb border) => Get<Action<double>>(border, "Nudged") is not null;

    // Where a border is along its split, in its grid.
    private static double At(Thumb border) => IsVertical(border)
        ? border.TranslatePoint(new Point(0, 0), (UIElement)VisualTreeHelper.GetParent(border)).Y
        : border.TranslatePoint(new Point(0, 0), (UIElement)VisualTreeHelper.GetParent(border)).X;

    private static void Drag(Thumb border, double by)
    {
        border.RaiseEvent(new DragStartedEventArgs(0, 0));
        border.RaiseEvent(IsVertical(border) ? new DragDeltaEventArgs(0, by) : new DragDeltaEventArgs(by, 0));
        border.UpdateLayout();
        border.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
        border.UpdateLayout();
    }

    // The parts beside a border in its grid (the columns or rows either side of its own): their sizes against their least.
    private static string Sides(Thumb border)
    {
        var grid = (Grid)VisualTreeHelper.GetParent(border);
        if (IsVertical(border))
        {
            var row = Grid.GetRow(border);
            var (a, b) = (grid.RowDefinitions[row - 1], grid.RowDefinitions[row + 1]);
            return $"above {Num(a.ActualHeight)}/{Num(a.MinHeight)}, below {Num(b.ActualHeight)}/{Num(b.MinHeight)}";
        }

        var column = Grid.GetColumn(border);
        var (l, r) = (grid.ColumnDefinitions[column - 1], grid.ColumnDefinitions[column + 1]);
        return $"left {Num(l.ActualWidth)}/{Num(l.MinWidth)}, right {Num(r.ActualWidth)}/{Num(r.MinWidth)}";
    }

    /// <summary>
    /// The least width of the column of the header joined over the Media pane (DockManager.MinWidthOf): the header's own, or the Media
    /// pane's least, raised by the joined bar up to DockManager.JoinedLeast.
    /// </summary>
    private static double MediaLeast(DockManager dock, double media) =>
        Math.Max(dock.Pane("Header")!.MinWidth(), Math.Max(media, Math.Min(dock.JoinedWidth, Math.Max(media, DockManager.JoinedLeast))));

    private static void SplitterRange(Action<bool, string> expect)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = AppSettings.Current;
        var kept = (settings.BrowserView, settings.BrowserFilmstripListWidth);
        var slot = PaneTitleSlot.Of("Media");
        settings.BrowserView = "Grid";
        var media = new MediaBrowserPane();

        // The header as the editor's: four icon buttons (Add Media and Layout an 18 DIP picture, Project and Settings a glyph).
        var header = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (name, glyph) in new[] { ("Add Media", ""), ("Layout", ""), ("Project", ""), ("Settings", "") })
        {
            object content = glyph.Length == 0 ? new Border { Width = 18, Height = 18 } : new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 };
            header.Children.Add(new Button { Content = content, Padding = glyph.Length == 0 ? new Thickness(9, 6, 9, 6) : new Thickness(10, 6, 10, 6), Margin = new Thickness(6, 0, 0, 6), ToolTip = name });
        }

        var host = new ContentControl { Focusable = false };
        var window = new Window { Left = 20, Top = 20, Width = 1280, Height = 760, Content = host, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        var dock = new DockManager(window, host, () => MainWindow.StandardLayout(TabIds));
        var resets = 0;
        static FrameworkElement Pane(string name) => new Border { Background = Brushes.DimGray, Child = new TextBlock { Text = name } };

        // The least widths MainWindow gives its panes (MainWindow.Docking.cs, 5.4D): 180 unless said otherwise.
        void Add(string id, FrameworkElement content, Func<double>? minWidth = null, double minHeight = 60, PaneHandle handle = PaneHandle.TitleSlot, bool fits = false,
            Func<bool>? shown = null, Action<double>? resize = null, Action? reset = null, bool joins = false, bool floats = false, Action<PaneShape>? reshape = null) =>
            dock.Register(new DockPane
            {
                Id = id, Title = id, Content = () => content, Handle = handle, FitsContent = fits, IsShown = shown ?? (() => true), MinWidth = minWidth ?? (() => 180),
                MinHeight = minHeight, ResizeBy = resize, ResetSize = reset, JoinsPaneBelow = joins, Floats = floats, Reshape = reshape,
            });
        Add("Player", Pane("Player"), () => 320, 180, PaneHandle.Hover, floats: true);
        Add("Playback", new Border { Height = 40, Background = Brushes.DarkSlateGray }, () => 300, handle: PaneHandle.Hover, fits: true);
        Add("Timeline", new Border { Height = 48, Background = Brushes.DarkSlateGray }, () => 300, handle: PaneHandle.Hover, fits: true, resize: _ => { }, reset: () => resets++);
        Add("UnifiedTimeline", Pane("Timeline"), () => 300, 140);
        Add("Layout", Pane("Canvas"), shown: () => false);
        Add("Header", header, () =>
        {
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(Math.Ceiling(header.DesiredSize.Width), header.Orientation == Orientation.Vertical ? 0 : 140);
        }, handle: PaneHandle.Hover, fits: true, joins: true, reshape: shape => header.Orientation = shape == PaneShape.Column ? Orientation.Vertical : Orientation.Horizontal);
        Add("Media", media, () => media.ViewMode == MediaBrowserView.Filmstrip ? 280 : 180, 140);
        Add("Keyframes", Pane("Keyframes"), minHeight: 110);
        Add("Effects", Pane("Effects"), minHeight: 110);
        Add("Meter", Pane("Meter"), () => 104, 180, shown: () => false);
        Add("Presets", new Border { Height = 30, Background = Brushes.DarkSlateGray }, handle: PaneHandle.Hover, fits: true);
        foreach (var id in TabIds)
            Add(id, Pane(id), () => 300, 140, PaneHandle.Tab);
        foreach (var (id, _, _, _) in MainWindow.Studios)
            dock.Register(MainWindow.StudioPane(id, () => Pane(id), null, id == MainWindow.VideoScopesId ? 180 : 240, 140));
        media.ViewModeChanged += _ => dock.RefreshMinSizes();

        var mainWindow = app.MainWindow;
        app.MainWindow = window;
        try
        {
            window.Show();
            var layouts = MainWindow.BuiltInLayoutStates(TabIds).Where(l => l.Name is "Standard Editing" or "Vertical & Shorts Studio" or "Vertical Creator Split").ToList();
            foreach (var (name, layout) in layouts)
            {
                void TakeUp()
                {
                    dock.Apply(dock.Prepared(layout));
                    Idle();
                }

                TakeUp();
                var borders = Borders(host);
                var sharing = borders.Where(Shares).ToList();
                expect(sharing.Count > 0, $"{name} at 1280 x 760: {borders.Count} borders dragged, {sharing.Count} of them sharing the room; the joined header {Num(dock.JoinedWidth)} DIPs wide "
                    + $"with its tab (capped at {Num(DockManager.JoinedLeast)}), so the Media column's least is {Num(MediaLeast(dock, 180))}");

                for (var i = 0; i < sharing.Count; i++)
                {
                    TakeUp();
                    var border = Borders(host).Where(Shares).ElementAt(i);
                    var sides = Sides(border);
                    var from = At(border);
                    Drag(border, 24);
                    var ahead = At(border) - from;
                    var mid = At(border);
                    Drag(border, -48);
                    var back = At(border) - mid;
                    var way = IsVertical(border) ? "row" : "column";
                    expect(ahead > 0.5 && back < -0.5,
                        $"  {way} border {i + 1} ({sides}): dragged 24 one way it moves {Num(ahead)}, 48 the other way {Num(-back)}");
                }

                // The top split side by side (Standard Editing, the Vertical Creator Split: three parts): its last border dragged far to the left
                // takes the middle part to its least, then the first part gives the rest (a cascade).
                TakeUp();
                var lastColumn = Borders(host).Where(b => Shares(b) && !IsVertical(b)).Select(b => (Border: b, Grid: (Grid)VisualTreeHelper.GetParent(b)))
                    .Where(b => b.Grid.ColumnDefinitions.Count == 5).Select(b => b.Border).LastOrDefault();
                if (lastColumn is not null)
                {
                    var grid = (Grid)VisualTreeHelper.GetParent(lastColumn);
                    var (first, middle, last) = (grid.ColumnDefinitions[0], grid.ColumnDefinitions[2], grid.ColumnDefinitions[4]);
                    var (firstBefore, middleBefore, lastBefore) = (first.ActualWidth, middle.ActualWidth, last.ActualWidth);
                    var extra = Math.Min(60, Math.Floor(firstBefore - first.MinWidth));
                    var reach = middleBefore - middle.MinWidth + extra;
                    Drag(lastColumn, -reach);
                    expect(extra > 0 && Math.Abs(middle.ActualWidth - middle.MinWidth) < 1 && Math.Abs(firstBefore - first.ActualWidth - extra) < 1 && Math.Abs(last.ActualWidth - lastBefore - reach) < 1,
                        $"  its last column border dragged {Num(reach)} to the left: the middle part down to its least ({Num(middle.MinWidth)}), the first part gives the other {Num(extra)} "
                        + $"({Num(firstBefore)} to {Num(first.ActualWidth)}, its least {Num(first.MinWidth)}), the last part gains all {Num(reach)}");

                    var saved = DockLayoutState.Read(dock.Save())!.Main;
                    var top = name == "Standard Editing" ? saved.Children[0] : saved;
                    expect(Math.Abs(top.Sizes[0] - first.ActualWidth) < 1 && Math.Abs(top.Sizes[2] - last.ActualWidth) < 1,
                        "  the drag's end keeps the parts' widths as the arrangement's shares (saved)");

                    // A step (Nudge): 16 DIPs, kept. The borders take no keyboard focus, as before 5.4D (the arrow keys stay the timeline's).
                    var before = At(lastColumn);
                    Get<Action<double>>(lastColumn, "Nudged")!(1);
                    lastColumn.UpdateLayout();
                    var savedAfter = DockLayoutState.Read(dock.Save())!.Main;
                    var topAfter = name == "Standard Editing" ? savedAfter.Children[0] : savedAfter;
                    expect(Math.Abs(At(lastColumn) - before - 16) < 0.5 && Math.Abs(topAfter.Sizes[2] - last.ActualWidth) < 1 && !lastColumn.Focusable,
                        "  a border nudged by 16 DIPs (Nudge) keeps its shares; borders take no keyboard focus, as before");

                    // A double-click: the two parts beside it each half of what they have together, kept at once.
                    Get<Action>(lastColumn, "DoubleClicked")!();
                    lastColumn.UpdateLayout();
                    var even = DockLayoutState.Read(dock.Save())!.Main;
                    var evenTop = name == "Standard Editing" ? even.Children[0] : even;
                    expect(Math.Abs(middle.ActualWidth - last.ActualWidth) < 1 && Math.Abs(evenTop.Sizes[1] - evenTop.Sizes[2]) < 1,
                        $"  a double-click evens the two parts beside it ({Num(middle.ActualWidth)} and {Num(last.ActualWidth)}), kept as their shares");
                }

                // The Sequence Overview Scrubber's lower edge (a plain drag makes it taller, so the keyboard shares nothing there): a double-click
                // puts its height back (DockPane.ResetSize), never evens the parts.
                TakeUp();
                var edges = Borders(host).Where(b => !Shares(b) && Get<Action>(b, "DoubleClicked") is not null).ToList();
                var (counted, sizes) = (resets, dock.Save());
                foreach (var edge in edges)
                    Get<Action>(edge, "DoubleClicked")!();
                expect(edges.Count == 1 && resets == counted + 1 && dock.Save() == sizes,
                    "  a double-click on the scrubber's lower edge puts its height back (its ResetSize), the parts' shares untouched");
            }

            // The joined header at the Media column's least (its bar capped, DockManager.JoinedLeast): the header's buttons whole, inside the column.
            dock.Apply(dock.Prepared(MainWindow.StandardLayout(TabIds)));
            Idle();
            var mediaBorder = Borders(host).Where(b => Shares(b) && !IsVertical(b)).Last();
            Drag(mediaBorder, 2000);
            var topGrid = (Grid)VisualTreeHelper.GetParent(mediaBorder);
            var mediaColumn = topGrid.ColumnDefinitions[^1];
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var natural = header.DesiredSize.Width;
            var headerRight = header.TranslatePoint(new Point(header.ActualWidth, 0), topGrid).X;
            var columnRight = topGrid.ColumnDefinitions.Sum(c => c.ActualWidth);
            expect(Math.Abs(mediaColumn.ActualWidth - mediaColumn.MinWidth) < 1 && header.ActualWidth >= natural - 0.5 && headerRight <= columnRight + 0.5,
                $"Standard Editing, the Media column dragged to its least ({Num(mediaColumn.ActualWidth)}): the joined header whole ({Num(header.ActualWidth)} of its {Num(natural)} DIPs) and inside it (the strip of tabs beside it gives way)");

            // The Media Browser's Filmstrip view raises its column's least width in place: no layout again, the same grid.
            dock.Apply(dock.Prepared(MainWindow.StandardLayout(TabIds)));
            Idle();
            DependencyObject at = media;
            (Grid Grid, ColumnDefinition Column)? column = null;
            while (VisualTreeHelper.GetParent(at) is { } parent)
            {
                if (parent is Grid { ColumnDefinitions.Count: > 1 } grid && at is UIElement part && grid.Children.OfType<Thumb>().Any(t => t.GetType().Name == "SeamThumb"))
                {
                    column = (grid, grid.ColumnDefinitions[Grid.GetColumn(part)]);
                    break;
                }

                at = parent;
            }

            var thumbs = Borders(host);
            var least = column?.Column.MinWidth ?? 0;
            media.ViewMode = MediaBrowserView.Filmstrip;
            var filmstrip = column?.Column.MinWidth ?? 0;
            var inPlace = Borders(host).SequenceEqual(thumbs) && column is { } c && c.Grid.IsDescendantOf(host);
            media.ViewMode = MediaBrowserView.Grid;
            var back2 = column?.Column.MinWidth ?? 0;
            expect(column is not null && least == MediaLeast(dock, 180) && filmstrip == MediaLeast(dock, 280) && filmstrip == 280 && back2 == least && inPlace,
                $"Standard Editing: the Media column's least {Num(least)}; the Media Browser switched to Filmstrip, {Num(filmstrip)} at once, in place (the same grid and borders); back to Grid, {Num(back2)}");

            // A drag keeps the window counted as laid out from the arrangement (its sizes in it), so the arrangement from before the drag,
            // loaded again (a project tab brought back), lays the window out again: its columns as they were, not the dragged ones.
            dock.Apply(dock.Prepared(MainWindow.StandardLayout(TabIds)));
            Idle();
            List<double> TopColumns() => ((Grid)VisualTreeHelper.GetParent(Borders(host).Where(b => Shares(b) && !IsVertical(b)).Last())).ColumnDefinitions.Select(c => c.ActualWidth).ToList();
            var undragged = dock.Save();
            var widths = TopColumns();
            Drag(Borders(host).Where(b => Shares(b) && !IsVertical(b)).Last(), -120);
            var draggedWidths = TopColumns();
            dock.Load(undragged);
            Idle();
            var loadedBack = TopColumns();
            expect(Math.Abs(draggedWidths[^1] - widths[^1] - 120) < 1 && loadedBack.Count == widths.Count && loadedBack.Zip(widths).All(p => Math.Abs(p.First - p.Second) < 1) && dock.Save() == undragged,
                $"Standard Editing, its Media border dragged 120 to the left ({Num(widths[^1])} to {Num(draggedWidths[^1])}), then the arrangement from before loaded: the columns back as they were ({string.Join(", ", loadedBack.Select(Num))})");

            // Reset twice (the second with nothing to lay out: the tree taken up in place), then a drag: the drag is in the arrangement saved.
            dock.Reset(MainWindow.StandardLayout(TabIds));
            Idle();
            dock.Reset(MainWindow.StandardLayout(TabIds));
            Idle();
            Drag(Borders(host).Where(b => Shares(b) && !IsVertical(b)).Last(), -80);
            var afterReset = TopColumns();
            var savedTop = DockLayoutState.Read(dock.Save())!.Main.Children[0];
            expect(Math.Abs(savedTop.Sizes[0] - afterReset[0]) < 1 && Math.Abs(savedTop.Sizes[2] - afterReset[4]) < 1,
                $"Reset Layout twice, then the Media border dragged 80 to the left: the arrangement saved has the dragged widths ({Num(savedTop.Sizes[2])} for {Num(afterReset[4])} on screen)");
        }
        finally
        {
            window.Close();
            app.MainWindow = mainWindow;
            media.Release();
            PaneTitleSlot.Restore("Media", slot);
            (settings.BrowserView, settings.BrowserFilmstripListWidth) = kept;
        }
    }
}
