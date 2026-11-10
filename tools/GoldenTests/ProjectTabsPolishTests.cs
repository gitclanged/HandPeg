using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4B: the title bar's project tabs drawn only once a project is started or opened, and their polish. project-tabs-lazy-strip: the
/// main window's strip starts collapsed (its own root never is), the pure predicate's truth table (one blank tab hides it; a second tab,
/// a file, a project file, a tab not brought back or Ctrl+T shows it), a real view model's blank and started states, and a collapsed strip
/// that generates no tab even when it holds one. project-tabs-reorder: ProjectTabs.Move keeps the tab in front and the stills, Ctrl+Tab,
/// the neighbour a close brings forward, tabs.json and the leftovers' order follow it; the pure drop index among the tabs shown.
/// project-tabs-duplicate: a tab inserted after its original, a tab behind's file copied whole without its saved fingerprint, the tab in
/// front's capture written as a never-saved copy. project-tabs-header-frame: a header's frame and frame rate round-trip and keep it under a
/// kilobyte (under 4 KB with its window layout, 5.4D), an older header reads them as unknown, the card's line. project-tabs-menu: the
/// right-click menu's lines and keys, the rename rules, the tooltip and saved line, and the hover card (an inspector card, placed by
/// UiScale.PlaceAgainst). titlebar-tab-drag: a real strip in a window with the main window's chrome, a tab dragged along it: the order and
/// events, and afterwards the gaps are still the caption, nothing has a background and the tab's margin is as the layout counts it.
/// project-tabs-tabperf-review (Pass 5.4D-TabPerf review, the window's side): a TabWrite claimed by one writer at a time (eight threads at
/// once), a failed one still wanted and tried again, a written or cancelled one never; a session let go refuses every write after it (its
/// folder never made again) and takes its uniquely named half-written files with it; the pure tests of a still and an overview sheet kept
/// with a tab (within a frame of its playhead and in its view; drawn at the undo mark it is at, nothing newer coming); the pictures a tab
/// keeps counted once each; and the wiring of the crash writer, the clean close, the retries, a save from memory and the still's drops.
/// </summary>
public static class ProjectTabsPolishTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("project-tabs-lazy-strip", "# No project tabs are drawn until a project is started or opened: the strip collapsed and empty on the first frame, shown by work, a project file or Ctrl+T.", LazyStrip);
        yield return Case("project-tabs-reorder", "# A tab dragged along the strip: the order changes, the tab in front and the stills stay; Ctrl+Tab, closing and tabs.json follow the new order.", Reorder);
        yield return Case("project-tabs-duplicate", "# Duplicate Project in New Tab: a never-saved copy right after its original, from its cache file or from the tab in front, its saved state not copied.", Duplicate);
        yield return Case("project-tabs-header-frame", "# A tab's header keeps its frame and frame rate for its hover card: round-trips, stays small (under 4 KB with its layout), and reads an older header as unknown.", HeaderFrame);
        yield return Case("project-tabs-menu", "# A tab's right-click menu (its lines, keys and rules), the rename rules, and the hover card of a tab behind (an inspector card placed against its tab).", Menu);
        yield return Case("titlebar-tab-drag", "# A tab dragged along the real strip in the main window's chrome: moved once per place, told to the window, and the gaps round the tabs still the caption.", Drag);
        yield return Case("project-tabs-tabperf-review", "# The 5.4D-TabPerf review, the window's side: a tab's cache file written by one writer at a time and kept listed until written, nothing written after a clean close, a still or sheet kept only when current, the pictures counted once.", TabPerfReview);
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

    private static string Fresh(string name)
    {
        var folder = Path.Combine(AppPaths.Temp, name);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        return folder;
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

    private static XElement? Named(XContainer xaml, string name) => xaml.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == name);

    private static string? Attr(XElement? element, XName name) => element?.Attribute(name)?.Value;

    private static void Idle()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(150);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                yield return found;
            foreach (var deeper in Descendants<T>(child))
                yield return deeper;
        }
    }

    private static string? AutomationName(DependencyObject element) => System.Windows.Automation.AutomationProperties.GetName(element);

    /// <summary>A view model holding a Sequence of one dropped file (as ProjectTabsTests.Open does, smaller).</summary>
    private static MainViewModel Started()
    {
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports, settings.EnableExtraAnimations, settings.DroppedClipsJoinTrack) = (false, false, false, false);
        var vm = new MainViewModel();
        var document = new TimelineDocument { Width = 1920, Height = 1080, NaturalWidth = 1920, NaturalHeight = 1080, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        track.Clips.Add(DogfoodTests.FileClip("D:/media/first.mkv", 0, 4));
        document.Tracks.Add(track);
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [document]);
        return vm;
    }

    // ----- project-tabs-lazy-strip -----

    private static void LazyStrip(Action<bool, string> expect)
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var main = XDocument.Load(Path.Combine(src, "MainWindow.xaml"));
        var strip = XDocument.Load(Path.Combine(src, "Controls", "ProjectTabStrip.xaml"));
        expect(Attr(Named(main, "ProjectTabStrip"), "Visibility") == "Collapsed",
            "MainWindow.xaml: the title bar's ProjectTabStrip starts collapsed (nothing of it measured, arranged or drawn on the first frame)");
        expect(strip.Root is { } root && Attr(root, "Visibility") is null,
            "ProjectTabStrip.xaml: its own root sets no Visibility (a strip made anywhere else, as titlebar-tab-hittest makes one, is drawn)");

        // The predicate: (count, unresumed, holds nothing, project file, revealed by Ctrl+T or +).
        expect(MainWindow.StripHidden(1, false, true, "", false), "one blank tab, opened from no file, not revealed: no tabs drawn");
        expect(!MainWindow.StripHidden(2, false, true, "", false), "a second tab (Ctrl+T or + while shown, or tabs left by a dead session): drawn");
        expect(!MainWindow.StripHidden(1, false, false, "", false), "the one tab holds work (a clip dropped, a New Project made, a frame chosen): drawn");
        expect(!MainWindow.StripHidden(1, false, true, @"D:\Projects\Montage.hproj", false), "the one tab was opened from a project file: drawn");
        expect(!MainWindow.StripHidden(1, true, true, "", false), "the one tab could not be brought back (its work is in its cache file, the view model empty): drawn");
        expect(!MainWindow.StripHidden(1, false, true, "", true), "Ctrl+T or + while hidden reveals the blank tab itself, [ New Project x ] [ + ]: drawn");
        expect(!MainWindow.StripHidden(0, false, true, "", false) && !MainWindow.StripHidden(3, false, true, "", false), "no count but one hides it");

        // A real view model: blank, then started.
        var blank = new MainViewModel();
        expect(MainWindow.StripHidden(1, false, blank.HoldsNothingForTab, blank.CurrentProjectPath, false), "a fresh view model (the start-up's blank project): the strip hidden");
        var started = Started();
        expect(!MainWindow.StripHidden(1, false, started.HoldsNothingForTab, started.CurrentProjectPath, false), "the same with a clip on the Sequence: the strip shown");
        started.NewProject();
        expect(MainWindow.StripHidden(1, false, started.HoldsNothingForTab, started.CurrentProjectPath, false), "and emptied again (the last tab closed: a new project in it): hidden again");

        // A collapsed strip holding a tab: no tab is made or measured until it is shown.
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var live = new ProjectTabStrip { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        live.Tabs.Add(new ProjectTabItem { Id = "a", Title = "First Project", IsActive = true });
        var window = new Window { Left = 100, Top = 100, Width = 600, Height = 120, ShowActivated = false, ShowInTaskbar = false, Title = "HandPeg golden: lazy tabs", Content = live };
        try
        {
            window.Show();
            Idle();
            expect(live.Items.ItemContainerGenerator.ContainerFromIndex(0) is null && !Descendants<Button>(live).Any() && live.ActualWidth == 0,
                "collapsed with a tab in it: no tab container generated, no button templated, no width (zero elements laid out)");
            live.Visibility = Visibility.Visible;
            Idle();
            expect(live.Items.ItemContainerGenerator.ContainerFromIndex(0) is not null && Descendants<Button>(live).Any(b => AutomationName(b) == "Project Tab: First Project") && live.ActualWidth > 0,
                "shown: the tab is made and drawn, with + after it");
        }
        finally
        {
            window.Close();
            Idle();
        }
    }

    // ----- project-tabs-reorder -----

    private static BitmapSource Still() => BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);

    private static string Order(ProjectTabs tabs) => string.Join(" ", tabs.Tabs.Select(t => t.Id));

    private static void Reorder(Action<bool, string> expect)
    {
        var root = Fresh("project-tabs-reorder");
        var tabs = new ProjectTabs(root, "tab-1", "session-1");
        var all = new List<ProjectTabHeader> { tabs.Active };
        for (var i = 2; i <= 4; i++)
            all.Add(tabs.Add($"Project {i}", $"tab-{i}"));
        foreach (var tab in all.Skip(1))
        {
            tabs.Activate(tab);
            tabs.SetStill(tab, Still());
        }

        tabs.Activate(all[1]);
        var (front, stills) = (tabs.Active, tabs.StillCount);
        tabs.Move(all[0], 2);
        expect(Order(tabs) == "tab-2 tab-3 tab-1 tab-4", "tab 1 dragged to the third place: 2, 3, 1, 4");
        expect(ReferenceEquals(tabs.Active, front) && tabs.StillCount == stills && tabs.StillOf(all[3]) is not null && tabs.Count == 4,
            "the tab in front, the stills and the count are as they were (a move is the order alone)");
        expect(tabs.Next(all[2], 1) == all[0] && tabs.Next(all[0], 1) == all[3] && tabs.Next(all[1], -1) == all[3],
            "Ctrl+Tab follows the new order: after 3 comes 1, after 1 comes 4; Ctrl+Shift+Tab from 2 goes round to 4");
        expect(tabs.NeighbourOf(all[2]) == all[0] && tabs.NeighbourOf(all[3]) == all[0], "closing 3 brings 1 (now after it) forward; closing 4, at the end, the one before it (1)");

        tabs.Move(all[3], -5);
        expect(Order(tabs) == "tab-4 tab-2 tab-3 tab-1", "a place before the first is the first");
        tabs.Move(all[3], 99);
        expect(Order(tabs) == "tab-2 tab-3 tab-1 tab-4", "a place past the last is the last");
        tabs.Move(new ProjectTabHeader { Id = "stranger" }, 0);
        tabs.Move(all[1], 0);
        expect(Order(tabs) == "tab-2 tab-3 tab-1 tab-4", "a tab not on the strip, or one moved to where it is, changes nothing");

        // tabs.json keeps the new order, and a session that ended offers its tabs back in it.
        var state = new ProjectState { Name = "Behind" };
        foreach (var tab in all.Where(t => !ReferenceEquals(t, tabs.Active)))
            tabs.WriteTab(tab.Id, state, null, ProjectTabs.HeaderJson(tab));
        tabs.WriteManifest(tabs.ManifestJson());
        expect(ProjectTabs.ReadManifest(tabs.FolderPath) is { } manifest && string.Join(" ", manifest.Order) == "tab-2 tab-3 tab-1 tab-4" && manifest.Active == "tab-2",
            "tabs.json written on the drop holds the new order and the tab in front");
        var left = ProjectTabs.LeftBehind(tabs.FolderPath, ["tab-2"]);
        expect(string.Join(" ", left.Select(t => t.Id)) == "tab-3 tab-1 tab-4", "a session that ended there offers its tabs back in the order they were dragged to");
        tabs.ForgetSession();

        // The drop place among the tabs shown (index, middle): three tabs 100 wide at 50, 150, 250.
        (int, double)[] three = [(0, 50), (1, 150), (2, 250)];
        expect(ProjectTabStrip.ReorderIndex(0, three, 120) == 0 && ProjectTabStrip.ReorderIndex(0, three, 151) == 1 && ProjectTabStrip.ReorderIndex(0, three, 260) == 2,
            "the first tab dragged right: stays short of the second's middle, takes its place past it, the last place past the third's");
        expect(ProjectTabStrip.ReorderIndex(2, three, 240) == 2 && ProjectTabStrip.ReorderIndex(2, three, 149) == 1 && ProjectTabStrip.ReorderIndex(2, three, 10) == 0,
            "the last tab dragged left: the same, the other way");
        (int, double)[] run = [(3, 50), (4, 150), (5, 250)];
        expect(ProjectTabStrip.ReorderIndex(4, run, -500) == 3 && ProjectTabStrip.ReorderIndex(4, run, 5000) == 5,
            "with tabs in the overflow list, a drag stays within the run shown (3 to 5), never past it");
        expect(ProjectTabStrip.ReorderIndex(0, [(0, 50)], 400) == 0, "a single tab has nowhere to go");
    }

    // ----- project-tabs-duplicate -----

    private static void Duplicate(Action<bool, string> expect)
    {
        var root = Fresh("project-tabs-duplicate");
        var tabs = new ProjectTabs(root, "front", "session-1");
        var original = tabs.Add("Montage", "original");
        tabs.Add("Last", "last");
        var copy = tabs.Insert(tabs.IndexOf(original) + 1, "Montage (Copy)", "copy");
        expect(Order(tabs) == "front original copy last" && ReferenceEquals(tabs.Active, tabs.Find("front")), "the copy goes right after its original, not in front yet");
        expect(tabs.Insert(-3, "x", "first").Id == tabs.Tabs[0].Id && tabs.Insert(99, "y", "end").Id == tabs.Tabs[^1].Id, "an insert before the first or past the last is clamped to the strip");

        // A tab behind, kept in its file with unsaved changes: copied whole, its saved fingerprint not.
        (original.Dirty, original.Empty, original.ProjectPath) = (true, false, @"D:\Projects\Montage.hproj");
        var state = new ProjectState { Name = "Montage", SourcePath = "D:/media/first.mkv" };
        tabs.WriteTab(original.Id, state, "fingerprint of the original", ProjectTabs.HeaderJson(original));
        File.WriteAllText(tabs.SavedFile(copy.Id), "a stale fingerprint");
        (copy.Empty, copy.Dirty, copy.ProjectPath, copy.NewProjectName, copy.NewProjectFolder) = (false, true, "", copy.Title, @"D:\Projects");
        tabs.CopyState(original.Id, copy.Id, ProjectTabs.HeaderJson(copy));
        expect(File.ReadAllBytes(tabs.StateFile(copy.Id)).AsSpan().SequenceEqual(File.ReadAllBytes(tabs.StateFile(original.Id))) && tabs.ReadState(copy.Id).SourcePath == "D:/media/first.mkv",
            "the copy's project file is the original's, byte for byte, and reads back");
        expect(tabs.ReadSaved(copy.Id) is null && tabs.ReadSaved(original.Id) == "fingerprint of the original",
            "the copy has no saved fingerprint (it was never saved: always unsaved), the original keeps its own");
        expect(File.Exists(tabs.HeaderFile(copy.Id)) && ProjectTabs.ReadHeader(File.ReadAllText(tabs.HeaderFile(copy.Id))) is { Dirty: true, ProjectPath: "", NewProjectName: "Montage (Copy)", Empty: false } header
               && header == copy,
            "its header is written last: unsaved, no project file, its first save named \"Montage (Copy)\"");
        expect(!Directory.EnumerateFiles(tabs.FolderPath, "*.tmp").Any(), "nothing half-copied is left beside it");

        // The tab in front: captured from the view model, written as a never-saved copy.
        var vm = Started();
        var captured = vm.CaptureStateForTab("Montage (Copy 2)");
        var second = tabs.Insert(1, "Montage (Copy 2)", "copy-2");
        tabs.WriteTab(second.Id, captured, null, ProjectTabs.HeaderJson(second));
        var back = tabs.ReadState(second.Id);
        expect(back.Name == "Montage (Copy 2)" && tabs.ReadSaved(second.Id) is null && tabs.HasState(second.Id),
            "the tab in front's capture is written under the copy's name with no saved fingerprint (it comes back unsaved)");
        tabs.ForgetSession();
        expect(!Directory.Exists(tabs.FolderPath), "a clean close deletes the copies with the session's other files");
    }

    // ----- project-tabs-header-frame -----

    private static void HeaderFrame(Action<bool, string> expect)
    {
        expect(ProjectTabs.FrameOf(1920, 1080, false) == (1920, 1080) && ProjectTabs.FrameOf(1920, 1080, true) == (1080, 1920),
            "the frame as Project Settings draws it: 1920 x 1080, or 1080 x 1920 for a vertical project");
        expect(ProjectTabs.DescribeFrame(1920, 1080, 60) == "1920 x 1080 \u00b7 60 fps" && ProjectTabs.DescribeFrame(1080, 1920, 59.94) == "1080 x 1920 \u00b7 59.94 fps"
               && ProjectTabs.DescribeFrame(3840, 2160, 30000 / 1001.0) == "3840 x 2160 \u00b7 29.97 fps",
            "the hover card's line: \"1920 x 1080 \u00b7 60 fps\", fractional rates to three places at most");
        expect(ProjectTabs.DescribeFrame(0, 0, 0) == "" && ProjectTabs.DescribeFrame(1920, 1080, 0) == "1920 x 1080" && ProjectTabs.DescribeFrame(0, 1080, 25) == "25 fps",
            "what is not known is left out (nothing at all for an older header)");

        var header = new ProjectTabHeader { Id = "tab-1", Title = new string('N', 120), ProjectPath = @"C:\Users\someone\Documents\HandPeg\Projects\" + new string('p', 120) + ".hproj" };
        (header.Dirty, header.Empty, header.PlayheadMs, header.ViewStart, header.ViewSpan, header.ViewFit, header.VerticalOffset) = (true, false, 123456.5, 98.25, 12.5, false, 48);
        (header.NewProjectName, header.NewProjectFolder) = ("Studio Name", @"D:\Projects");
        (header.FrameWidth, header.FrameHeight, header.FrameRate) = (1080, 1920, 59.94005994005994);
        var json = ProjectTabs.HeaderJson(header);
        expect(ProjectTabs.ReadHeader(json) == header, "a header with its frame and frame rate reads back as it was (record equality)");
        expect(Encoding.UTF8.GetByteCount(json) < 1024, "with a 120-character name, a long path, a studio name and folder, still under 1 KB");

        // 5.4D: the main window's layout as the tab was put behind is most of a header now (nested as a string): under 4 KB with it.
        (header.ExplodedOpen, header.LayoutJson) = (true, System.Text.Json.JsonSerializer.Serialize(MainWindow.StandardLayout(["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"]),
            AppSettingsJsonContext.Default.DockLayoutState));
        json = ProjectTabs.HeaderJson(header);
        expect(ProjectTabs.ReadHeader(json) == header && Encoding.UTF8.GetByteCount(json) < 4096, "with its 3D view and Standard Editing's layout too (5.4D), it reads back as it was and is under 4 KB");
        var older = "{\"Id\":\"old\",\"Title\":\"From 5.4A\",\"ProjectPath\":\"\",\"Dirty\":true,\"Empty\":false}";
        expect(ProjectTabs.ReadHeader(older) is { FrameWidth: 0, FrameHeight: 0, FrameRate: 0, Title: "From 5.4A" }, "a header written by 5.4A (no frame) reads with its frame unknown");

        var vm = Started();
        var settings = vm.CurrentProjectSettings;
        expect(ProjectTabs.FrameOf(settings.NaturalWidth, settings.NaturalHeight, settings.Vertical) == (1920, 1080) && settings.FrameRate.Fps == 30,
            "a 1920 x 1080 30 fps project's settings give the header 1920 x 1080 at 30 fps (what TakeHeaderForTab writes)");
    }

    // ----- project-tabs-menu -----

    private static void Menu(Action<bool, string> expect)
    {
        string Lines(IReadOnlyList<ProjectTabMenuLine> lines) =>
            string.Join(" | ", lines.Select(l => l.Command is null ? "-" : l.Enabled ? l.Header : $"({l.Header})"));

        var front = new ProjectTabItem { Id = "a", Title = "Montage", IsActive = true, ProjectPath = @"D:\Projects\Montage.hproj" };
        var expected = string.Join(" | ",
            KeybindManager.WithGesture("Save Project", "file.save"), KeybindManager.WithGesture("Save Project As...", "file.saveAs"), "Rename Project...",
            "Duplicate Project in New Tab", "Open Containing Folder", "-", KeybindManager.WithGesture("Close Tab", "tab.close"), "Close Other Tabs");
        expect(Lines(ProjectTabStrip.MenuFor(front, 2)) == expected,
            "the tab in front's menu: Save Project, Save Project As..., Rename Project..., Duplicate Project in New Tab, Open Containing Folder, a line, Close Tab, Close Other Tabs, with their keys");
        expect(KeybindManager.WithGesture("Save Project", "file.save") != "Save Project" && KeybindManager.WithGesture("Close Tab", "tab.close") != "Close Tab",
            "its keys are read from the key map as it opens (Save Project and Close Tab have keys)");
        var behind = new ProjectTabItem { Id = "b", Title = "Never Saved" };
        expect(Lines(ProjectTabStrip.MenuFor(behind, 1)) == "Save Project | Save Project As... | Rename Project... | Duplicate Project in New Tab | (Open Containing Folder) | - | Close Tab | (Close Other Tabs)",
            "a tab behind: no keys (they act on the tab in front); never saved, no folder to open; alone, no other tabs to close");

        expect(MainWindow.RenameRefusal("My Cut") is null && MainWindow.RenameRefusal("  Spaced Out  ") is null, "a plain name will do (spaces round it trimmed)");
        expect(MainWindow.RenameRefusal("   ") is not null && MainWindow.RenameRefusal("a/b") is not null && MainWindow.RenameRefusal("what?") is not null
               && MainWindow.RenameRefusal("...") is not null && MainWindow.RenameRefusal("name.") is not null,
            "refused: nothing typed, a character no file name holds, dots alone, a trailing dot");
        expect(MainWindow.RenamedPath(@"D:\Projects\Old.hproj", " New ") == @"D:\Projects\New.hproj" && MainWindow.RenamedPath(@"E:\Bundles\Old.hpb", "New") == @"E:\Bundles\New.hpb",
            "a saved project renamed stays where it is under the new name, its extension kept");

        expect(behind.FrontToolTip is null && front.FrontToolTip == front.ToolTipText && front.ToolTipText.Contains(@"D:\Projects\Montage.hproj", StringComparison.Ordinal),
            "the tab in front keeps its text tooltip; a tab behind has none (its hover card instead)");
        behind.IsDirty = true;
        expect(behind.SavedText == "Not saved as a project yet" && front.SavedText == "Saved\n" + @"D:\Projects\Montage.hproj" && !behind.HasFile && front.HasFile,
            "the card's last line: not saved yet, or saved with its file");
        front.IsDirty = true;
        expect(front.SavedText.StartsWith("Unsaved changes\n", StringComparison.Ordinal), "or unsaved changes, with its file");

        // The hover card: in the strip's XAML, an inspector card that takes no clicks, placed against its tab.
        var src = Path.Combine(RepositoryRoot(), "src");
        var strip = XDocument.Load(Path.Combine(src, "Controls", "ProjectTabStrip.xaml"));
        var card = Named(strip, "HoverCard");
        var surface = card?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Border" && Attr(e, "Background") == "{DynamicResource InspectorCardBrush}");
        expect(card is { Name.LocalName: "Popup" } && Attr(card, "IsHitTestVisible") == "False" && Attr(card, "AllowsTransparency") == "True",
            "ProjectTabStrip.xaml: HoverCard is a Popup that takes no clicks");
        expect(surface is not null && Attr(surface, "BorderBrush") == "{DynamicResource InspectorCardBorderBrush}" && Attr(surface, "BorderThickness") == "1"
               && Attr(surface, "CornerRadius") == "6" && Attr(surface, "Padding") is null,
            "its surface is an inspector card (the theme's card brushes, a 1 px border, 6 px corners), nothing inside its edge");
        var picture = Named(strip, "HoverPicture");
        var brush = picture?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ImageBrush");
        expect(Attr(picture, "Height") == "{Binding PictureHeight}" && Attr(brush, "ImageSource") == "{Binding Preview}" && Attr(brush, "Stretch") == "UniformToFill",
            "its picture fills the card (5.4C), the tab's still or thumbnail");
        expect(ProjectTabItem.CardFit(1920, 1080) == new Size(318, 179) && ProjectTabItem.CardFit(1080, 1920) == new Size(191, 340)
               && ProjectTabItem.CardFit(1080, 1080) == new Size(318, 318) && ProjectTabItem.CardFit(0, 0) == new Size(318, 0),
            "in its own shape: 16:9 is 318 x 179, 9:16 191 x 340 (never cropped to 16:9), square 318 x 318; with no picture the card is 318 wide");
        var shade = Named(strip, "HoverShade");
        expect(shade is not null && Attr(shade, "VerticalAlignment") == "Bottom" && shade.Descendants().Any(e => e.Name.LocalName == "LinearGradientBrush")
               && shade.Descendants().Any(e => Attr(e, "Text") == "{Binding Title}") && shade.Descendants().Any(e => Attr(e, "Text") == "{Binding SavedText}"),
            "its title, frame and saved line lie over the picture's foot on a dark band fading up into it");
        var tab = Named(strip, "Tab");
        expect(Attr(tab, "ToolTip") == "{Binding FrontToolTip}", "a tab's tooltip is the tab in front's text alone");
        var code = File.ReadAllText(Path.Combine(src, "Controls", "ProjectTabStrip.xaml.cs"));
        expect(code.Contains("UiScale.PlaceAgainst(HoverCard, tab, UiScale.FlyoutSide.BelowStart, 4)", StringComparison.Ordinal)
               && !code.Contains("PlacementMode.Bottom", StringComparison.Ordinal) && !code.Contains("PlacementMode.Relative", StringComparison.Ordinal),
            "the card is placed under its tab by UiScale.PlaceAgainst, never by a Bottom or Relative placement");
    }

    // ----- titlebar-tab-drag -----

    private const int WmNcHitTest = 0x0084, HtClient = 1, HtCaption = 2;

    private static void Drag(Action<bool, string> expect)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        background.Freeze();
        var window = new Window
        {
            Left = 100, Top = 100, Width = 900, Height = 300, Background = background, ShowActivated = false, ShowInTaskbar = false,
            Title = "HandPeg golden: title bar tab drag",
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 26, GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = default, UseAeroCaptionButtons = false,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
        });

        var bar = new Grid { Height = 26, Background = background };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        var title = new TextBlock { Text = "HandPeg", Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var strip = new ProjectTabStrip { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(strip, 1);
        var (a, b, c) = (new ProjectTabItem { Id = "a", Title = "First Project", IsActive = true }, new ProjectTabItem { Id = "b", Title = "Second Project" },
            new ProjectTabItem { Id = "c", Title = "Third Project", IsDirty = true });
        strip.Tabs.Add(a);
        strip.Tabs.Add(b);
        strip.Tabs.Add(c);
        bar.Children.Add(title);
        bar.Children.Add(strip);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Border { Background = background });
        window.Content = root;

        var moved = new List<string>();
        var (ended, chosen) = (0, 0);
        strip.TabMoved += (item, index) => moved.Add($"{item.Id}->{index}");
        strip.ReorderEnded += () => ended++;
        strip.TabChosen += _ => chosen++;
        try
        {
            window.Show();
            Idle();
            FrameworkElement Face(ProjectTabItem item) => Descendants<Button>(strip).First(x => AutomationName(x) == $"Project Tab: {item.Title}");
            double Middle(ProjectTabItem item)
            {
                var face = Face(item);
                return face.TranslatePoint(new Point(face.ActualWidth / 2, 0), strip.Items).X;
            }

            // The first tab dragged past the second's middle, then past the third's, then let go.
            expect(strip.BeginReorder(a) && strip.IsReordering && a.IsDragged && !strip.BeginReorder(b), "a drag begun on the first tab: the strip is reordering, the tab drawn faded, no second drag at once");
            strip.ReorderTo(a, Middle(b) - 4);
            expect(moved.Count == 0, "short of the second tab's middle: nothing moves");
            strip.ReorderTo(a, Middle(b) + 4);
            Idle();
            expect(string.Join(" ", strip.Tabs.Select(t => t.Id)) == "b a c" && moved.SequenceEqual(["a->1"]), "past it: the first tab takes the second place, told to the window once (a to 1)");
            strip.ReorderTo(a, Middle(a) + 2);
            expect(moved.Count == 1, "the pointer resting over the dragged tab's new place moves nothing (no back and forth)");
            strip.ReorderTo(a, Middle(c) + 4);
            Idle();
            expect(string.Join(" ", strip.Tabs.Select(t => t.Id)) == "b c a" && moved.SequenceEqual(["a->1", "a->2"]), "past the third's middle: the last place");
            expect(ended == 0, "nothing is written down while the tab is held");
            strip.EndReorder();
            Idle();
            expect(!strip.IsReordering && !a.IsDragged && ended == 1 && chosen == 0, "let go: the drag over, the tab drawn as the others, the order written down once, and no tab chosen by it");
            strip.ReorderTo(b, 0);
            expect(moved.Count == 2, "a move asked for with no drag under way does nothing");

            // Afterwards: still nothing with a background, the tab's gap as the layout counts it, and the gaps still the caption.
            expect(Descendants<StackPanel>(strip).All(p => p.Background is null) && strip.Items.Background is null && strip.Background is null,
                "the strip, its panels and its tabs' ItemsControl have no background (the gaps between the tabs stay the window's caption)");
            expect(strip.Tabs.All(t => Face(t).Margin == new Thickness(0, 0, ProjectTabStrip.TabGap, 0)), "every tab keeps its 0,0,2,0 margin (the gap the layout counts)");

            var hwnd = new WindowInteropHelper(window).Handle;
            int Hit(UIElement element, Point at)
            {
                var screen = element.PointToScreen(at);
                var handled = false;
                var answer = MainWindow.FrameHook(hwnd, WmNcHitTest, IntPtr.Zero, (IntPtr)(((int)Math.Floor(screen.Y) << 16) | ((int)Math.Floor(screen.X) & 0xFFFF)), ref handled);
                return handled ? (int)answer : -1;
            }

            var (first, second) = (Face(strip.Tabs[0]), Face(strip.Tabs[1]));
            expect(Hit(first, new Point(4, first.ActualHeight / 2)) == HtClient && Hit(second, new Point(second.ActualWidth / 2, second.ActualHeight / 2)) == HtClient,
                "after the drag: the tabs' faces are still the window's (HTCLIENT)");
            expect(Hit(first, new Point(first.ActualWidth + 1.5, first.ActualHeight / 2)) == HtCaption && Hit(second, new Point(second.ActualWidth + 1.5, second.ActualHeight / 2)) == HtCaption,
                "and the gaps between them are still the caption (HTCAPTION), to drag the window by");
            expect(Hit(bar, new Point(bar.ActualWidth - 120, bar.ActualHeight / 2)) == HtCaption, "so is the bar far right of +");
        }
        finally
        {
            window.Close();
            Idle();
        }
    }

    // ----- project-tabs-tabperf-review -----

    /// <summary>A source file under src, its line endings made \n.</summary>
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), "src", .. parts])).ReplaceLineEndings("\n");

    /// <summary>A member's text: from its signature to the end of its body (the next line that closes at its indent).</summary>
    private static string MemberOf(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..(end + 6)];
    }

    /// <summary>Whether every one of <paramref name="parts"/> is in <paramref name="text"/>, each after the one before.</summary>
    private static bool Ordered(string text, params string[] parts)
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

    private static bool Refused(Action write)
    {
        try
        {
            write();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static void TabPerfReview(Action<bool, string> expect)
    {
        // A tab's cache file write, claimed by one writer at a time (the thread pool, the crash writer, the clean close).
        var writeType = typeof(MainWindow).GetNestedType("TabWrite", BindingFlags.NonPublic)!;
        object NewWrite() => Activator.CreateInstance(writeType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            [new ProjectTabHeader { Id = "w" }, null, "{}", null], null)!;
        bool Claim(object write) => (bool)writeType.GetMethod("TryStart")!.Invoke(write, null)!;
        void Do(object write, string what, params object[] args) => writeType.GetMethod(what)!.Invoke(write, args);
        bool Is(object write, string what) => (bool)writeType.GetProperty(what)!.GetValue(write)!;

        var raced = NewWrite();
        var claims = 0;
        using (var start = new Barrier(8))
        {
            var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
            {
                start.SignalAndWait();
                if (Claim(raced))
                    Interlocked.Increment(ref claims);
            })).ToList();
            threads.ForEach(thread => thread.Start());
            threads.ForEach(thread => thread.Join());
        }

        expect(claims == 1 && Is(raced, "IsWanted") && !Is(raced, "IsDone") && !Claim(raced),
            "eight writers claiming one tab's write at once: exactly one gets it; while it is written it is still wanted, and nobody else may write it");
        Do(raced, "Fail");
        expect(Is(raced, "IsDone") && Is(raced, "Failed") && Is(raced, "IsWanted"), "a failed write is over but still wanted: the tab has no recovery copy yet, and it stays listed");
        expect(Claim(raced) && !Is(raced, "Failed") && !Is(raced, "IsDone"), "and it is claimed again for a retry (the valve's tick, the next tab put behind, the crash writer)");
        Do(raced, "Finish", true);
        Do(raced, "Fail");
        expect(Is(raced, "IsDone") && !Is(raced, "Failed") && !Is(raced, "IsWanted") && !Claim(raced) && writeType.GetProperty("Dirty")!.GetValue(raced) is true,
            "written: no longer wanted, never claimed again, and a writer's Fail on its way out changes nothing");
        var unclaimed = NewWrite();
        Do(unclaimed, "Fail");
        expect(!Is(unclaimed, "IsDone") && Is(unclaimed, "IsWanted"), "Fail without a claim does nothing");
        var cancelled = NewWrite();
        expect(Claim(cancelled), "a write claimed");
        Do(cancelled, "Fail");
        Do(cancelled, "Cancel");
        expect(!Is(cancelled, "IsWanted") && !Claim(cancelled), "a failed write whose tab closed or came back is not wanted and never written");

        // A session let go: nothing is written into its folder afterwards, and its half-written files of names of their own go with it.
        var root = Fresh("project-tabs-tabperf-review");
        var tabs = new ProjectTabs(root, "front", "session");
        var behind = tabs.Add("Behind", "behind");
        tabs.WriteTab(behind.Id, new ProjectState { Name = "Behind" }, "fingerprint", ProjectTabs.HeaderJson(behind));
        var partial = ProjectStore.PartialPath(tabs.StateFile(behind.Id));
        File.WriteAllText(partial, "{ half");
        expect(partial.StartsWith(tabs.StateFile(behind.Id) + ".", StringComparison.Ordinal) && partial.EndsWith(".tmp", StringComparison.Ordinal)
               && Path.GetFileName(partial).Length == "behind.hptab.".Length + 32 + ".tmp".Length && partial != ProjectStore.PartialPath(tabs.StateFile(behind.Id)),
            "a whole write's file beside its own has a name of its own each time (<file>.<32 hex digits>.tmp): two writers never open the same one");
        var copy = tabs.Add("Copy", "copy");
        tabs.CopyState(behind.Id, copy.Id, ProjectTabs.HeaderJson(copy));
        expect(tabs.HasState(copy.Id) && Directory.EnumerateFiles(tabs.FolderPath, "copy*.tmp").Count() == 0, "a copy is made beside under a name of its own, and nothing of it is left");
        expect(!tabs.IsForgotten, "a session is not let go until it closes");
        tabs.ForgetSession();
        expect(tabs.IsForgotten && !Directory.Exists(tabs.FolderPath) && !tabs.HoldsSession, "a clean close deletes the half-written file too, its lock and its folder");
        var state = new ProjectState { Name = "Late" };
        expect(Refused(() => tabs.WriteTab(behind.Id, state, null, ProjectTabs.HeaderJson(behind))) && Refused(tabs.ClaimSession)
               && Refused(() => tabs.WriteManifest(tabs.ManifestJson())) && !Directory.Exists(tabs.FolderPath) && !tabs.HoldsSession,
            "a write still running after the clean close is refused (an IOException): the folder, its lock and the tab's file are never made again");

        // A tab's still and overview sheet kept only when they are its picture as it comes back.
        expect(MainWindow.StillFits(1000, false, 1000, false, 30) && MainWindow.StillFits(1000, false, 1033, false, 30) && !MainWindow.StillFits(1000, false, 1040, false, 30),
            "a still within a frame of the playhead fits (33 ms at 30 fps); one more than a frame away does not");
        expect(!MainWindow.StillFits(10_000, false, 150_000, false, 60), "a still from before playback (0:10, the tab left at 2:30) covers nothing");
        expect(!MainWindow.StillFits(1000, false, 1000, true, 30) && !MainWindow.StillFits(1000, true, 1000, false, 30) && MainWindow.StillFits(1000, true, 1000, true, 30),
            "a flat still never covers a tab coming back in 3D, nor a 3D one a flat tab; a 3D still covers a 3D tab");
        expect(MainWindow.StillFits(0, false, 33, false, 0) && !MainWindow.StillFits(0, false, 40, false, 0) && !MainWindow.StillFits(1000, false, 1009, false, 120),
            "a frame rate not known yet is taken as 30 fps; at 120 fps a frame is 8.3 ms");
        object m1 = new byte[1], m2 = new byte[1];
        expect(MainWindow.SheetIsCurrent(true, m1, m1, false, false) && MainWindow.SheetIsCurrent(true, null, null, false, false),
            "a whole sheet drawn at the undo mark the Sequence is at is kept (none: a Sequence with no history yet)");
        expect(!MainWindow.SheetIsCurrent(true, m1, m2, false, false) && !MainWindow.SheetIsCurrent(false, null, null, false, false),
            "one drawn before the last edit (or undo), or whose mark is not known, is not: it is drawn again as the tab comes back");
        expect(!MainWindow.SheetIsCurrent(true, m1, m1, true, false) && !MainWindow.SheetIsCurrent(true, m1, m1, false, true),
            "nor while an edit made with the scrubber hidden waits, or a request settles or a sheet is being drawn");

        // The pictures a tab keeps, each counted once.
        BitmapSource Bitmap(int width, int height) => BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4);
        var (film, wave, timeline) = (Bitmap(100, 10), Bitmap(1200, 120), Bitmap(1920, 150));
        expect(MainWindow.PictureBytes([film, wave, film, null, new DrawingImage(), timeline, film, wave]) == 4L * (100 * 10 + 1200 * 120 + 1920 * 150),
            "the pictures a tab keeps count width x height x 4 bytes each, one file's filmstrip on many clips once; nulls and drawings count nothing");

        // The wiring (the window's side).
        var window = (Source("MainWindow.ProjectTabs.cs") + Source("MainWindow.TabCache.cs") + Source("MainWindow.TabMenu.cs"));
        expect(Ordered(MemberOf(window, "private void WriteTabNow("), "if (!write.TryStart())", "frozen = frozen.ShallowCopy();", "frozen.ThumbnailJpeg = write.Jpeg;",
                   "_tabs.WriteTab(write.Id, frozen,", "catch (Exception ex) when (ex is not OutOfMemoryException)", "write.Fail();", "if (!write.IsWanted)", "_unwritten.Remove(write);")
               && !window.Contains("frozen.ThumbnailJpeg = write.Jpeg = ", StringComparison.Ordinal),
            "the pool's write is claimed first, makes its thumbnail on a copy (the frozen state is never written to), and stays listed unless written or cancelled, whatever it threw");
        expect(Ordered(MemberOf(window, "private void WriteUnwrittenTabs("), "var until = Environment.TickCount64 + CrashIoWaitMs;", "if (!write.IsWanted)", "if (_tabs.IsBusyWith(write.Id))",
                   "WaitForTabIo(write.Id, until);", "if (!write.TryStart())", "_tabs.WriteTab(write.Id, project.Frozen,")
               && window.Contains("private const int CrashIoWaitMs = 3000;", StringComparison.Ordinal),
            "falling over: a write under way is waited for (3 s at most in all) and only a write it can claim is written, never a second copy beside one");
        expect(Ordered(MemberOf(window, "private void TabWritten("), "if (write.Failed)", "live.MustStay = true;", "has no recovery copy yet", "return;", "live.MustStay = false;")
               && Ordered(MemberOf(window, "private void KeptTabsChanged("), "RetryFailedTabWrites();", "LetGoForMemory();")
               && Ordered(MemberOf(window, "private ProjectTabHeader PutActiveBehind("), "_stillAsked++;", "CaptureWindowStateForTab(tab);", "var live = DetachActive(tab, out write);", "RetryFailedTabWrites();"),
            "a failed write says the tab has no recovery copy yet, and is tried again on the valve's tick and as the next tab goes behind");
        expect(Ordered(MemberOf(window, "private bool AttachHydrated("), "live.Write?.Cancel();", "ForgetUnwritten(live.Write);", "DeleteTabFiles(tab.Id);")
               && Ordered(MemberOf(window, "private void ForgetKeptTab("), "live.Write?.Cancel();", "ForgetUnwritten(live.Write);"),
            "a write let go as its tab comes back or closes is taken off the list (a failed one would hold the tab's frozen copy otherwise)");
        expect(Ordered(MemberOf(window, "private void ForgetProjectTabs("), "FinishTabWrites();", "CancelTabWrites(_ => false);", "_tabs.ForgetSession();", "WaitForKeptTabWrites();",
                   "WriteKeptTabsNow(keep);", "CancelTabWrites(keep);", "_tabs.ForgetSession(keep);")
               && Ordered(MemberOf(window, "private void WaitForKeptTabWrites("), "\"Saving tabs...\"", "_tabs.WaitFor(tab.Id, Timeout.Infinite);", "TakeWrittenDots();")
               && MemberOf(window, "private void WriteKeptTabsNow(").Contains("!claimed.TryStart()", StringComparison.Ordinal),
            "a clean close waits for a kept tab's write to its end (Saving tabs...), writes only what it can claim, and lets every other write go before the session's files");
        expect(Ordered(MemberOf(window, "private string SaveTabInPlace("), "if (live is not null && !_tabs.WaitFor(tab.Id, IoWaitMs))", "Still writing this tab's recovery copy: try again in a moment.",
                   "live.Project.Frozen.ShallowCopy()", "(state.Name, state.SavedAt) =")
               && !window.Contains("live?.Project.Frozen ??", StringComparison.Ordinal),
            "a save from memory waits for the tab's write (refused, said so, after 5 s) and writes a copy with its name and time: the frozen state is never changed");
        expect(MemberOf(window, "private bool MayChangeTabs(").Contains("_viewModel.HasPendingAdds ? \"Adding clips: switch tabs when they are in.\"", StringComparison.Ordinal),
            "the tabs never change while clips are still being added");
        var close = MemberOf(window, "private async Task CloseProjectTabAsync(");
        expect(close.Split("ForgetLiveStill();").Length == 3 && !close.Contains("(_liveStill, _liveStillMark) = (null, default);", StringComparison.Ordinal),
            "closing the tab in front drops a still being taken, as putting it behind does (a switch generation)");
        expect(Ordered(MemberOf(window, "private bool AttachHydrated("), "StillFits(live.StillMark.PositionMs, live.StillMark.Shown3D, tab.PlayheadMs, tab.ExplodedOpen, tab.FrameRate)",
                   "ShowTabStill(live.Still);", "HideTabStill();", "_viewModel.AttachTab(project)", "(_liveStill, _liveStillMark) = stillFits ? (live.Still, live.StillMark) : (null, default);"),
            "a tab kept live shows its still only when it fits, and keeps it only then");
        expect(Ordered(MemberOf(window, "private static long ApproxBytesOf("), "view.Filmstrip, view.Waveform", "project.TrackWaveforms.Values", "project.TimelineWaveform", "live.Still", "live.ProgramSheet?.Picture"),
            "the valve counts a kept tab's views' filmstrips and waveforms, its tracks' and the timeline's waveforms, its still and sheet");

        var still = Source("MainWindow.TabStill.cs");
        var exploded = Source("MainWindow.Exploded.cs");
        expect(Ordered(MemberOf(still, "private void KeepTabStillFresh("), "nameof(MainViewModel.IsPlaying) && _viewModel.IsPlaying", "ForgetLiveStill();")
               && Ordered(MemberOf(exploded, "private void SetExplodedView("), "_explodedUi = true;", "NoteStillStale();", "_explodedUi = false;", "NoteStillStale();")
               && exploded.Split("NoteStillStale();").Length - 1 == 8,
            "playback forgets the still; entering or leaving the 3D view and moving its camera take it again");
        var strip = Source("MainWindow.ProgramStrip.cs");
        var drawer = Source("Services", "GpuProgramStrip.cs");
        expect(strip.Contains("ProgramStrip().Request(state, _viewModel.UndoMark);", StringComparison.Ordinal)
               && strip.Contains("_programSheetDrawnAt = sheet.IsPartial ? null : new ProgramSheetKept(_programStrip?.SheetMark);", StringComparison.Ordinal)
               && Ordered(MemberOf(strip, "private ThumbnailSheet? SheetToKeep("), "SheetIsCurrent(", "_programStale, _programStrip is { IsPending: true }")
               && Ordered(MemberOf(drawer, "private async void Draw("), "SheetMark = mark;", "SheetReady?.Invoke(sheet);"),
            "a sheet carries the undo mark its request was made at, and a tab keeps it only when current");
        var store = Source("Services", "ProjectStore.cs");
        expect(store.Split("var partial = PartialPath(path);").Length == 3 && !store.Contains("path + \".tmp\"", StringComparison.Ordinal),
            "both whole writes write beside under a name of their own");
        expect(Ordered(MemberOf(Source("Services", "ProjectTabs.cs"), "public void WriteTab("), "ClaimSession();", "ProjectStore.SaveTo(state, StateFile(id));", "if (IsForgotten)", "TryDelete(StateFile(id));"),
            "a write that was under way as the session was let go takes its project file out again");
    }
}
