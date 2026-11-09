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
/// Pass 5.4A: browser-style project tabs in the title bar, suspended to the cache and resumed. project-tabs-suspend-resume: on a real view
/// model, a tab with two edits is written to the cache (a format 6 project with its two undo steps), a new tab pastes what the first copied,
/// and the first comes back as it was (its Sequence, Undo, its unsaved changes, its file, its header's numbers) while a clean tab comes back
/// clean; a closed tab's files go. project-tabs-lru: the tabs alone (Services/ProjectTabs.cs): only the three most recent keep a still, a
/// header is under a kilobyte (5.4D: under 4 KB with its window layout), Ctrl+Tab goes round, tabs.json and the headers read back (and tolerate damage), a session's leftovers are found.
/// titlebar-tab-hittest: a real window with the main window's chrome and the real strip, hit-tested by the main window's frame hook (drawn
/// by the card and by the processor): the tabs, their names and ×, and + are the window's own; the gaps and the bar right of + are the
/// caption. keybind-project-tabs: Ctrl+T, Ctrl+W, Ctrl+Tab and Ctrl+Shift+Tab, legal, taken before a text box, and no preset's other keys.
/// The review's fixes: project-tabs-sessions (a folder per HandPeg running: a live session's lock keeps its tabs its own, a dead one's are
/// taken in; a clean close keeps only what it is told to; adopted tabs rank below this session's), project-tabs-fresh-and-per-tab (only a
/// fresh tab is let go as empty; the New Project name and folder go with their tab), project-tabs-unresumed (new work in front of a tab not
/// brought back gets a tab of its own, the old one's file untouched), titlebar-tab-overflow (the strip never wider than its room),
/// project-tabs-save-path (where a tab behind is saved, as the view model's save would put it) and project-tabs-never-lost (a project file
/// without a header is taken in under a made-up one, one that cannot come across is kept, a clean close deletes only its own tabs' files).
/// Pass 5.4D-TabPerf: project-tabs-late-edit is gone with what it checked (a tab put behind is detached at once, so no edit can land on it
/// after its write: TabPerfTests' tab-perf-hot-swap).
/// </summary>
public static class ProjectTabsTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string First = "D:/media/first.mkv", Second = "D:/media/second.mkv";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("project-tabs-suspend-resume", "# A project tab put behind (its project and undo history in the cache) and brought back as it was; the clipboard crosses tabs.", SuspendResume);
        yield return Case("project-tabs-lru", "# The project tabs alone: three hot stills at most, headers small (under 4 KB with their layout), Ctrl+Tab round the strip, tabs.json and leftovers read back.", Lru);
        yield return Case("titlebar-tab-hittest", "# The title bar's tabs, their × and + take their clicks; the gaps and the bar right of + stay the caption (the main window's frame hook).", HitTest);
        yield return Case("keybind-project-tabs", "# Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+Shift+Tab: the project tabs' keys, legal, taken while typing, and nobody else's in any preset.", Keys);
        yield return Case("project-tabs-sessions", "# A cache folder per HandPeg running: a live session's tabs are never taken or deleted, a dead one's are taken in; a clean close keeps only the tabs it is told to.", Sessions);
        yield return Case("project-tabs-fresh-and-per-tab", "# Only a fresh tab is let go as empty (a studio project with no files, or one whose clips were all deleted, is kept); the New Project name and folder are each tab's own.", FreshAndPerTab);
        yield return Case("project-tabs-unresumed", "# New work in front of a tab that could not be brought back gets a tab of its own; the old tab keeps its file as it was.", Unresumed);
        yield return Case("titlebar-tab-overflow", "# The strip shares its room, narrows its tabs to their initials, then shows a run round the one in front and a count of the rest: never wider than its room.", Overflow);
        yield return Case("project-tabs-save-path", "# Where a tab behind the one in front is saved: its own file, a portable folder, the studio's folder, the Projects folder (as the view model's save).", SavePath);
        yield return Case("project-tabs-never-lost", "# A project file left without a header is taken in under a made-up one, one that cannot come across is kept, and a clean close never deletes a tab it does not know.", NeverLost);
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

    // ----- project-tabs-suspend-resume -----

    /// <summary>A view model holding a Sequence of dropped files (no file opened, so nothing is probed as it is put back): two clips of one file on track 1, one of another on track 2.</summary>
    private static MainViewModel Open()
    {
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports, settings.EnableExtraAnimations, settings.DroppedClipsJoinTrack) = (false, false, false, false);
        var vm = new MainViewModel();
        var document = new TimelineDocument { Width = 1920, Height = 1080, NaturalWidth = 1920, NaturalHeight = 1080, FrameRate = FrameRate.Fps30 };
        var one = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        one.Clips.AddRange([DogfoodTests.FileClip(First, 0, 4), DogfoodTests.FileClip(First, 4, 8, 4)]);
        var two = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 2 };
        two.Clips.Add(DogfoodTests.FileClip(Second, 0, 6));
        document.Tracks.AddRange([one, two]);
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [document]);
        return vm;
    }

    /// <summary>Every clip of the Sequence, track by track: what must come back exactly.</summary>
    private static string Shape(TimelineDocument document) =>
        string.Join(" | ", document.Tracks.Select(t => string.Join(", ", t.Clips.Select(c => FormattableString.Invariant($"{c.Id} {Path.GetFileName(c.SourcePath)} {c.Start:0.###}-{c.End:0.###}")))));

    // The window's thread, turned until the task is done (or 30 s have gone): the view model's continuations come back to it.
    private static void PumpUntil(Task task)
    {
        var until = Environment.TickCount64 + 30_000;
        while (!task.IsCompleted && Environment.TickCount64 < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            if (!task.IsCompleted)
                Thread.Sleep(5);
        }
    }

    /// <summary>A tab brought back in the view model, from the window's thread's own queue (as a click would), waited for.</summary>
    private static void Resume(MainViewModel vm, ProjectState state, string? path, bool dirty, string? saved, string newProjectName = "", string? newProjectFolder = null)
    {
        var resuming = Dispatcher.CurrentDispatcher.InvokeAsync(() => vm.ResumeTabAsync(state, path, dirty, saved, newProjectName, newProjectFolder)).Task.Unwrap();
        PumpUntil(resuming);
        if (!resuming.IsCompleted)
            throw new TimeoutException("the tab did not come back within 30 s");
        resuming.GetAwaiter().GetResult();
    }

    private static void SuspendResume(Action<bool, string> expect)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var tabs = new ProjectTabs(Fresh("project-tabs-resume"), "one", "session");
        var vm = Open();
        vm.MarkSaved();
        expect(vm.HasSource && !vm.HasUnsavedChanges, "tab 1: a Sequence of dropped files, as it was last saved");

        // Two edits (each one step of Undo): the first clip trimmed, the second deleted. Then the clip on track 2 copied.
        var track = vm.Document.Tracks[0];
        var (a, b, c) = (vm.ViewOf(track.Clips[0])!, vm.ViewOf(track.Clips[1])!, vm.ViewOf(vm.Document.Tracks[1].Clips[0])!);
        var copiedId = vm.Document.Tracks[1].Clips[0].Id;
        expect(vm.TrimClip(a, start: false, seconds: 3) && vm.DeleteClip(b), "two edits: the first clip trimmed to 3 s, the second deleted");
        expect(vm.CopyClips([c]) == 1, "the clip on track 2 copied (Ctrl+C)");
        vm.PositionMs = 2500;
        var dirty = vm.HasUnsavedChanges;
        expect(dirty, "tab 1 has unsaved changes");
        var trimmed = vm.Document.Tracks[0].Clips.Count;

        // ----- Tab 1 put behind -----
        const string projectPath = "D:/projects/Tab One.hproj";
        var one = tabs.Active;
        (one.Title, one.ProjectPath, one.Dirty, one.Empty) = ("Tab One", projectPath, dirty, false);
        (one.PlayheadMs, one.ViewStart, one.ViewSpan, one.ViewFit, one.VerticalOffset) = (vm.PositionMs, 1.5, 4, false, 24);
        var before = Shape(vm.Document);
        var state = vm.CaptureStateForTab(one.Title);
        expect(state.UndoHistory?.Count == 2 && state.RedoHistory is null, "its state carries the two undo steps and nothing to redo");
        tabs.WriteTab(one.Id, state, vm.SavedFingerprintForTab, ProjectTabs.HeaderJson(one));
        var file = tabs.StateFile(one.Id);
        expect(file.StartsWith(Path.Combine(AppPaths.Temp, "project-tabs-resume"), StringComparison.OrdinalIgnoreCase) && file.EndsWith(".hptab", StringComparison.Ordinal),
            "written to <folder>\\<session>\\<id>.hptab (the window's is %Cache%\\project-tabs\\<session>)");
        expect(File.Exists(file) && ProjectStore.PeekHeader(file) is { Version: ProjectFile.CurrentVersion }, "the .hptab is a project file of this version's format (6)");
        expect(File.ReadAllText(file).Contains("\"undoHistory\"", StringComparison.Ordinal) && tabs.ReadState(one.Id).UndoHistory?.Count == 2,
            "with its undo history (two Brotli-packed steps), read back as an open reads it");
        expect(File.Exists(tabs.SavedFile(one.Id)) && File.Exists(tabs.HeaderFile(one.Id)), "and its saved state's fingerprint (it has unsaved changes) and its header beside it");

        // ----- Tab 2: a new project, the copied clip pasted into it -----
        var two = tabs.Add("Tab Two", "two");
        tabs.Activate(two);
        expect(vm.NewProject() && !vm.HasSource, "tab 2: a new, empty project (no prompt)");
        expect(vm.PasteClips(0) && vm.Document.Tracks.SelectMany(t => t.Clips).ToList() is [{ } pasted] && pasted.Id != copiedId && pasted.SourcePath == Second,
            "Ctrl+V in tab 2 pastes the clip copied in tab 1: a new id, the same file (the clipboard crosses tabs)");
        vm.MarkSaved();
        (two.Empty, two.Dirty) = (false, vm.HasUnsavedChanges);
        expect(!two.Dirty, "tab 2 saved: no unsaved changes");
        var twoShape = Shape(vm.Document);
        tabs.WriteTab(two.Id, vm.CaptureStateForTab(two.Title), null, ProjectTabs.HeaderJson(two));
        expect(!File.Exists(tabs.SavedFile(two.Id)), "a clean tab keeps no saved fingerprint");

        // ----- Tab 1 brought back -----
        var header = ProjectTabs.ReadHeader(File.ReadAllText(tabs.HeaderFile(one.Id)));
        tabs.Activate(one);
        expect(header is not null && header with { LastActive = one.LastActive } == one, "its header read back as it was written (title, file, dirty, playhead 2.5 s, view 1.5 s + 4 s, scrolled 24)");
        Resume(vm, tabs.ReadState(one.Id), header?.ProjectPath, header?.Dirty ?? false, tabs.ReadSaved(one.Id));
        expect(Shape(vm.Document) == before, "its Sequence is back exactly: every clip, its id, its file and its times");
        expect(vm.HasUnsavedChanges, "its changes are still unsaved (the open took it as saved; the tab put that right)");
        expect(vm.CurrentProjectPath == projectPath, "Save writes its project file again");
        vm.Undo();
        expect(vm.Document.Tracks[0].Clips.Count == trimmed + 1, "Undo goes on where it left off: the deleted clip is back");
        expect(vm.HasUnsavedChanges, "and the tab still has unsaved changes after it");

        // ----- Tab 2 brought back: clean -----
        tabs.Activate(two);
        Resume(vm, tabs.ReadState(two.Id), null, false, tabs.ReadSaved(two.Id));
        expect(Shape(vm.Document) == twoShape && !vm.HasUnsavedChanges && vm.CurrentProjectPath.Length == 0,
            "a clean tab comes back clean, with its pasted clip, and no project file");

        // ----- Tab 1 closed (from tab 2): its files go -----
        tabs.Close(one);
        expect(tabs.Count == 1 && !File.Exists(tabs.StateFile(one.Id)) && !File.Exists(tabs.HeaderFile(one.Id)) && !File.Exists(tabs.SavedFile(one.Id)),
            "a closed tab's .hptab, header and saved fingerprint are deleted");
        tabs.ForgetSession();
    }

    // ----- project-tabs-lru -----

    private static BitmapSource Still() => BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);

    private static void Lru(Action<bool, string> expect)
    {
        var root = Fresh("project-tabs-lru");
        var tabs = new ProjectTabs(root, "tab-1", "session-1");
        var folder = tabs.FolderPath;
        expect(tabs.Count == 1 && tabs.Active.Id == "tab-1" && !Directory.Exists(root) && !tabs.HoldsSession, "made with one tab in front, in memory: nothing written and no lock taken (the window's first frame)");
        expect(folder == Path.Combine(root, "session-1"), "its files go in a folder of the session's own under the root");

        var all = new List<ProjectTabHeader> { tabs.Active };
        for (var i = 2; i <= 5; i++)
            all.Add(tabs.Add($"Project {i}", $"tab-{i}"));
        foreach (var tab in all)
        {
            tabs.Activate(tab);
            tabs.SetStill(tab, Still());
        }

        expect(tabs.StillCount == ProjectTabs.MaxHotTabs && all.Skip(2).All(t => tabs.StillOf(t) is { IsFrozen: true }) && all.Take(2).All(t => tabs.StillOf(t) is null),
            "five tabs in front in turn: only the three most recent (3, 4, 5) keep a still, frozen; 1 and 2 are their headers alone");
        tabs.Activate(all[0]);
        expect(tabs.StillOf(all[2]) is null && tabs.StillOf(all[3]) is not null && tabs.StillOf(all[4]) is not null && tabs.StillCount == 2,
            "tab 1 back in front: tab 3, now the fourth most recent, lets its still go");
        tabs.SetStill(all[1], Still());
        expect(tabs.StillOf(all[1]) is null, "a cold tab is given no still");

        // A header with a long name and path: still well under a kilobyte, and read back whole. 5.4D: with the main window's layout in
        // it (AppSettings.PerTabWindowLayouts, most of it, nested as a string), a few kilobytes: under 4 KB.
        var header = all[3];
        (header.Title, header.ProjectPath, header.Dirty, header.Empty) = (new string('N', 120), @"C:\Users\someone\Documents\HandPeg\Projects\" + new string('p', 120) + ".hproj", true, false);
        (header.PlayheadMs, header.ViewStart, header.ViewSpan, header.ViewFit, header.VerticalOffset) = (123456.5, 98.25, 12.5, false, 48);
        var json = ProjectTabs.HeaderJson(header);
        expect(Encoding.UTF8.GetByteCount(json) < 1024, "a header with a 120-character name and a long path is under 1 KB");
        header.LayoutJson = System.Text.Json.JsonSerializer.Serialize(MainWindow.StandardLayout(["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"]),
            AppSettingsJsonContext.Default.DockLayoutState);
        json = ProjectTabs.HeaderJson(header);
        expect(Encoding.UTF8.GetByteCount(json) < 4096, "with Standard Editing's layout in it (5.4D), under 4 KB");
        expect(ProjectTabs.ReadHeader(json) == header, "and reads back as it was");
        expect(ProjectTabs.ReadHeader("{ not json") is null && ProjectTabs.ReadHeader("{}") is null && ProjectTabs.ReadHeader("[1, 2]") is null,
            "a damaged header, one with no id and one of another shape read as none");

        // Ctrl+Tab and Ctrl+Shift+Tab round the strip.
        var (forward, back, at) = (new List<string>(), new List<string>(), tabs.Tabs[0]);
        for (var i = 0; i < 6; i++)
            forward.Add((at = tabs.Next(at, 1)!).Id);
        at = tabs.Tabs[0];
        for (var i = 0; i < 3; i++)
            back.Add((at = tabs.Next(at, -1)!).Id);
        expect(string.Join(" ", forward) == "tab-2 tab-3 tab-4 tab-5 tab-1 tab-2", "Ctrl+Tab from tab 1: 2, 3, 4, 5, round to 1, 2");
        expect(string.Join(" ", back) == "tab-5 tab-4 tab-3", "Ctrl+Shift+Tab from tab 1: round to 5, then 4, 3");

        // tabs.json: none, written, read back, damaged.
        expect(ProjectTabs.ReadManifest(folder) is null, "no tabs.json (no folder yet): none read");
        Directory.CreateDirectory(folder);
        File.WriteAllText(tabs.ManifestFile, tabs.ManifestJson());
        expect(ProjectTabs.ReadManifest(folder) is { } manifest && manifest.Order.SequenceEqual(tabs.Tabs.Select(t => t.Id)) && manifest.Active == "tab-1",
            "tabs.json reads back: the order, and tab 1 in front");
        File.WriteAllText(tabs.ManifestFile, "{ \"Order\": [ \"tab-");
        expect(ProjectTabs.ReadManifest(folder) is null, "a damaged tabs.json reads as none (nothing thrown)");

        // A session that did not close cleanly: tabs 4 and 5 with their project files, tab 2 a header alone.
        var state = new ProjectState { Name = "Left behind" };
        tabs.WriteTab("tab-4", state, "fingerprint of tab 4", ProjectTabs.HeaderJson(all[3]));
        tabs.WriteTab("tab-5", state, null, ProjectTabs.HeaderJson(all[4]));
        File.WriteAllText(tabs.HeaderFile("tab-2"), ProjectTabs.HeaderJson(all[1]));
        File.WriteAllText(tabs.ManifestFile, new ProjectTabManifestWriter(["tab-5", "tab-4", "tab-2"]).Json);
        var left = ProjectTabs.LeftBehind(folder, ["tab-1"]);
        expect(string.Join(" ", left.Select(t => t.Id)) == "tab-5 tab-4", "its leftovers found: the tabs with a project file, in tabs.json's order (tab 2's header alone is not one)");
        expect(tabs.ReadSaved("tab-4") == "fingerprint of tab 4" && tabs.ReadSaved("tab-5") is null && tabs.ReadState("tab-5").Name == "Left behind",
            "a leftover's saved fingerprint and project read back");
        var next = new ProjectTabs(Fresh("project-tabs-lru-next"), "fresh", "session-2");
        expect(next.Adopt(left[0]) && !next.Adopt(left[0]) && next.Count == 2 && next.Active.Id == "fresh" && next.StillOf(left[0]) is null,
            "the next session offers a leftover once, behind the tab in front, cold");

        // Closing: files deleted, the tab in front never closed first, the neighbour that comes to the front.
        tabs.Close(all[3]);
        expect(tabs.Find("tab-4") is null && !File.Exists(tabs.StateFile("tab-4")) && !File.Exists(tabs.HeaderFile("tab-4")) && !File.Exists(tabs.SavedFile("tab-4")),
            "closing tab 4 deletes its three files");
        var refused = false;
        try
        {
            tabs.Close(tabs.Active);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        expect(refused, "the tab in front is never closed before another is in front");
        expect(tabs.NeighbourOf(all[4]) == all[2] && tabs.NeighbourOf(all[1]) == all[2], "closing the last tab brings the one before it to the front; any other, the one after it");
        expect(tabs.HoldsSession, "writing a tab took the session's lock");
        tabs.ForgetSession();
        expect(!Directory.Exists(folder) && !tabs.HoldsSession, "a clean close deletes every file of the session's folder, its lock and the folder");
    }

    /// <summary>A tabs.json of a given order (as a session that did not close cleanly left it).</summary>
    private sealed record ProjectTabManifestWriter(IReadOnlyList<string> Order)
    {
        public string Json => System.Text.Json.JsonSerializer.Serialize(new ProjectTabManifest(Order, Order[0]));
    }

    // ----- titlebar-tab-hittest -----

    private const int WmNcHitTest = 0x0084, HtClient = 1, HtCaption = 2;

    private static void HitTest(Action<bool, string> expect)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var software in new[] { false, true })
        {
            RenderOptions.ProcessRenderMode = software ? RenderMode.SoftwareOnly : RenderMode.Default;
            try
            {
                HitTestIn(software ? "drawn by the processor" : "drawn by the card", expect);
            }
            finally
            {
                RenderOptions.ProcessRenderMode = RenderMode.Default;
            }
        }

        // The XAML: the strip in the main window's title bar, right after its name; its tabs and + take their clicks; nothing taller than the bar.
        var src = Path.Combine(RepositoryRoot(), "src");
        var main = XDocument.Load(Path.Combine(src, "MainWindow.xaml"));
        var strip = XDocument.Load(Path.Combine(src, "Controls", "ProjectTabStrip.xaml"));
        var bar = Named(main, "TitleBar");
        var placed = Named(main, "ProjectTabStrip");
        expect(placed is { Name.LocalName: "ProjectTabStrip" } && placed.Parent == bar && (placed.ElementsBeforeSelf().LastOrDefault() is { } name && Attr(name, X + "Name") == "TitleText"),
            "MainWindow.xaml: ProjectTabStrip sits in TitleBar, right after TitleText");
        string? StyleSetter(string key, string property) =>
            strip.Descendants().FirstOrDefault(e => e.Name.LocalName == "Style" && Attr(e, X + "Key") == key)?.Elements()
                .FirstOrDefault(s => s.Name.LocalName == "Setter" && Attr(s, "Property") == property) is { } setter ? Attr(setter, "Value") : null;
        expect(StyleSetter("TabButton", "WindowChrome.IsHitTestVisibleInChrome") == "True" && StyleSetter("GlyphButton", "WindowChrome.IsHitTestVisibleInChrome") == "True",
            "ProjectTabStrip.xaml: a tab, its ×, and + are IsHitTestVisibleInChrome");
        var heights = new[] { StyleSetter("TabButton", "Height"), StyleSetter("GlyphButton", "Height"), Attr(Named(strip, "NewTabButton"), "Height") };
        expect(heights.All(h => double.TryParse(h, System.Globalization.CultureInfo.InvariantCulture, out var v) && v <= 26), "and none of them is taller than the caption (26)");
        var names = strip.Descendants().Select(e => Attr(e, "AutomationProperties.Name")).OfType<string>().ToList();
        expect(names.Contains("{Binding AutomationName}") && names.Contains("Close Project Tab") && names.Contains("New Project Tab")
               && new ProjectTabItem { Id = "x", Title = "Montage" }.AutomationName == "Project Tab: Montage",
            "automation names: \"Project Tab: <name>\", \"Close Project Tab\", \"New Project Tab\"");

        // The numbers: the status stays between the ends (the tabs count as the left end); the tabs share the room between 72 and 200 DIPs.
        expect(MainWindow.TitleStatusPlace(1000, 150, 200, 300) == (350, 300) && MainWindow.TitleStatusPlace(1000, 450, 200, 300) == (450, 300)
               && MainWindow.TitleStatusPlace(1000, 600, 200, 300) == (600, 200) && MainWindow.TitleStatusPlace(1000, 750, 200, 300).Width == 0
               && Enumerable.Range(0, 2000).All(i => MainWindow.TitleStatusPlace(1440.1 + i / 7.0, 292.1333333333332 + i / 3.0, 138.6, 4000) is var (l, w)
                                                     && w >= 0 && l >= 292.1333333333332 + i / 3.0),
            "the status (5.4C): 300 wide in a bar of 1000 with buttons of 200 sits in the middle (350) while the tabs end at 150; tabs to 450 push it right to 450;"
            + " tabs to 600 leave it the 200 between them, trimmed; tabs to 750 leave under 64 and it hides");
        expect(ProjectTabStrip.LayoutFor(1000, 2, 0) == new ProjectTabLayout(200, false, 0, 2) && ProjectTabStrip.LayoutFor(624, 5, 0) == new ProjectTabLayout(118, false, 0, 5)
               && ProjectTabStrip.LayoutFor(500, 0, 0) == new ProjectTabLayout(200, false, 0, 0),
            "tabs share the room with their names: 2 in 1000 are 200 (the widest), 5 in 624 (600 after +) are 118 each with their gaps");
    }

    private static void HitTestIn(string drawn, Action<bool, string> expect)
    {
        var background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        background.Freeze();
        var window = new Window
        {
            Left = 100, Top = 100, Width = 900, Height = 300, Background = background, ShowActivated = false, ShowInTaskbar = false,
            Title = "HandPeg golden: title bar tabs",
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 26, GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = default, UseAeroCaptionButtons = false,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
        });

        // The main window's title bar, as MainWindow.xaml lays it: the name, then the strip, left in the stretching column.
        var bar = new Grid { Height = 26, Background = background };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        var title = new TextBlock { Text = "HandPeg", Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var strip = new ProjectTabStrip { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(strip, 1);
        strip.Tabs.Add(new ProjectTabItem { Id = "a", Title = "First Project", IsActive = true });
        strip.Tabs.Add(new ProjectTabItem { Id = "b", Title = "Second Project", IsDirty = true });
        bar.Children.Add(title);
        bar.Children.Add(strip);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Border { Background = background });
        window.Content = root;
        try
        {
            window.Show();
            Idle();
            var hwnd = new WindowInteropHelper(window).Handle;
            int Hit(UIElement element, Point at)
            {
                var screen = element.PointToScreen(at);
                var handled = false;
                var answer = MainWindow.FrameHook(hwnd, WmNcHitTest, IntPtr.Zero, (IntPtr)(((int)Math.Floor(screen.Y) << 16) | ((int)Math.Floor(screen.X) & 0xFFFF)), ref handled);
                return handled ? (int)answer : -1;
            }

            var buttons = Descendants<Button>(strip).ToList();
            var tabA = buttons.First(b => AutomationName(b) == "Project Tab: First Project");
            var tabB = buttons.First(b => AutomationName(b) == "Project Tab: Second Project");
            var closeA = Descendants<Button>(tabA).First(b => AutomationName(b) == "Close Project Tab");
            var plus = buttons.First(b => AutomationName(b) == "New Project Tab");
            var nameA = Descendants<TextBlock>(tabA).First(t => t.Text == "First Project");
            expect(Hit(tabA, new Point(4, tabA.ActualHeight / 2)) == HtClient, $"{drawn}: a tab's own face is the window's (HTCLIENT)");
            expect(Hit(nameA, new Point(nameA.ActualWidth / 2, nameA.ActualHeight / 2)) == HtClient, $"{drawn}: so is the tab's name on it (the property is inherited)");
            expect(Hit(closeA, new Point(closeA.ActualWidth / 2, closeA.ActualHeight / 2)) == HtClient, $"{drawn}: and its ×");
            expect(Hit(tabB, new Point(tabB.ActualWidth / 2, tabB.ActualHeight / 2)) == HtClient, $"{drawn}: the second tab (with its unsaved dot)");
            expect(Hit(plus, new Point(plus.ActualWidth / 2, plus.ActualHeight / 2)) == HtClient, $"{drawn}: + is the window's");
            expect(Hit(tabA, new Point(tabA.ActualWidth + 1.5, tabA.ActualHeight / 2)) == HtCaption, $"{drawn}: the gap between two tabs is the caption (HTCAPTION)");
            expect(Hit(plus, new Point(plus.ActualWidth + 40, plus.ActualHeight / 2)) == HtCaption, $"{drawn}: the bar just right of + is the caption");
            expect(Hit(bar, new Point(bar.ActualWidth - 120, bar.ActualHeight / 2)) == HtCaption, $"{drawn}: and so is the bar far right of +, to drag the window and maximize it");
        }
        finally
        {
            window.Close();
            Idle();
        }
    }

    private static void Idle()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(150);
    }

    private static string? AutomationName(DependencyObject element) => System.Windows.Automation.AutomationProperties.GetName(element);

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

    // ----- keybind-project-tabs -----

    private static void Keys(Action<bool, string> expect)
    {
        (string Id, string Chord, string Label)[] tabKeys =
        [
            ("tab.new", "Ctrl+T", "New Project Tab"), ("tab.close", "Ctrl+W", "Close Project Tab"),
            ("tab.next", "Ctrl+Tab", "Next Project Tab"), ("tab.prev", "Ctrl+Shift+Tab", "Previous Project Tab"),
        ];
        foreach (var (id, chord, label) in tabKeys)
        {
            var action = KeybindManager.Action(id);
            expect(action is { Category: KeybindManager.Project } && action.Label == label && action.Defaults.SequenceEqual([chord]) && action.Has(KeyActionFlags.WorksInText),
                $"{id}: \"{label}\" in Project, on {chord} by default, taken even while a text box has the keyboard");
            var parsed = KeybindManager.Parse(chord);
            expect(parsed is { } c && KeybindManager.Validate(c, id) is null && KeybindManager.Format(c) == chord, $"{id}: {chord} is a legal shortcut for it");
            foreach (var preset in KeybindManager.PresetNames)
            {
                var chords = KeybindManager.PresetChords(preset);
                var others = chords.Where(p => p.Key != id && parsed is { } own && p.Value.Contains(own)).Select(p => p.Key).ToList();
                expect(parsed is { } mine && chords[id].SequenceEqual([mine]) && others.Count == 0,
                    $"{id} in {preset}: {chord} alone, and no other action has it{(others.Count > 0 ? " (" + string.Join(", ", others) + " do)" : "")}");
            }
        }

        var defaults = KeybindManager.PresetChords(AppSettings.DefaultKeybindPreset);
        expect(defaults["key.toggle"].Contains(KeybindManager.Parse("T")!.Value) && defaults["ui.xray"].Contains(KeybindManager.Parse("Tab")!.Value),
            "T alone still keys and Tab alone still holds X-Ray: only the Ctrl chords are the tabs'");
        var problems = KeybindManager.CheckPresets();
        expect(problems.Count == 0, $"KeybindManager.CheckPresets(): no preset gives a chord to two actions or binds one that cannot be (the clip keys Alt+Up and Alt+Down too){(problems.Count > 0 ? ": " + string.Join("; ", problems) : "")}");
    }

    // ----- project-tabs-sessions -----

    private static ProjectTabHeader Header(string id, string title, bool dirty) => new() { Id = id, Title = title, Dirty = dirty, Empty = false };

    private static void Sessions(Action<bool, string> expect)
    {
        var root = Fresh("project-tabs-sessions");
        var state = new ProjectState { Name = "Left behind" };

        // A HandPeg running (its lock held), a tab with unsaved changes behind the one in front.
        var live = new ProjectTabs(root, "live-front", "live");
        var liveTab = live.Add("Live Tab", "live-tab");
        (liveTab.Dirty, liveTab.Empty) = (true, false);
        live.WriteTab(liveTab.Id, state, "fingerprint of the live tab", ProjectTabs.HeaderJson(liveTab));
        expect(live.HoldsSession && File.Exists(Path.Combine(live.FolderPath, ProjectTabs.LockName)), "a session that writes a tab holds session.lock in a folder of its own");

        // A session that ended without closing: two tabs (one with unsaved changes) and its tabs.json; its lock let go (its process gone).
        var dead = new ProjectTabs(root, "dead-front", "dead");
        var deadDirty = dead.Add("Dead Dirty", "dead-dirty");
        (deadDirty.Dirty, deadDirty.Empty, deadDirty.LastActive) = (true, false, 55);
        var deadClean = dead.Add("Dead Clean", "dead-clean");
        (deadClean.Empty, deadClean.LastActive) = (false, 56);
        dead.WriteTab(deadDirty.Id, state, "fingerprint of the dead tab", ProjectTabs.HeaderJson(deadDirty));
        dead.WriteTab(deadClean.Id, state, null, ProjectTabs.HeaderJson(deadClean));
        dead.WriteManifest(dead.ManifestJson());
        dead.ReleaseSession();

        // A folder with no lock file (a session making its folder this moment): left alone.
        var making = Path.Combine(root, "making");
        Directory.CreateDirectory(making);
        File.WriteAllText(Path.Combine(making, "made" + ProjectTabs.HeaderExtension), ProjectTabs.HeaderJson(Header("made", "Being Made", false)));
        ProjectStore.SaveTo(state, Path.Combine(making, "made" + ProjectTabs.StateExtension));

        // A second window starts.
        var me = new ProjectTabs(root, "me-front", "me");
        var taken = me.TakeLeftBehind(["me-front"]);
        expect(string.Join(" ", taken.Select(t => t.Id)) == "dead-dirty dead-clean", "the new session takes the dead session's two tabs, in its tabs.json's order");
        expect(taken.All(t => File.Exists(me.StateFile(t.Id)) && File.Exists(me.HeaderFile(t.Id))) && me.ReadSaved("dead-dirty") == "fingerprint of the dead tab" && me.ReadSaved("dead-clean") is null,
            "their project files, headers and saved fingerprint are moved into its own folder");
        expect(!Directory.Exists(dead.FolderPath), "the dead session's folder goes once all its tabs came across");
        expect(File.Exists(live.StateFile("live-tab")) && File.Exists(live.SavedFile("live-tab")) && File.Exists(live.HeaderFile("live-tab")) && taken.All(t => t.Id != "live-tab"),
            "the live session's tab is neither offered nor touched: its lock is held");
        expect(File.Exists(Path.Combine(making, "made" + ProjectTabs.StateExtension)) && taken.All(t => t.Id != "made"), "a folder with no lock file is left alone");

        // Adopted, they rank below this session's tabs: the tab in front keeps its still.
        var adopted = taken.Count(me.Adopt) + (me.Adopt(Header("dead-x", "Another", false) with { LastActive = 57 }) ? 1 : 0);
        expect(adopted == 3 && me.Tabs.Where(t => !ReferenceEquals(t, me.Active)).All(t => t.LastActive < me.Active.LastActive),
            "three tabs adopted rank below the tab in front, whatever their last session counted (55, 56, 57)");
        me.SetStill(me.Active, Still());
        expect(me.IsHot(me.Active) && me.StillOf(me.Active) is not null, "so the tab in front keeps its paused still after three tabs were adopted");
        expect(me.TakeLeftBehind(me.Tabs.Select(t => t.Id).ToList()).Count == 0, "scanning again takes nothing: the live session's tabs stay its own");

        // A clean close with the prompt to save on exit off keeps the files of the tabs behind with unsaved changes.
        me.ForgetSession(t => t.Dirty && !t.Empty);
        expect(File.Exists(me.StateFile("dead-dirty")) && File.Exists(me.SavedFile("dead-dirty")) && !File.Exists(me.StateFile("dead-clean")) && !me.HoldsSession
               && File.Exists(Path.Combine(me.FolderPath, ProjectTabs.LockName)),
            "closing with the prompt off keeps the dirty tab's files (and the lock file, let go) and deletes the clean one's");
        var after = new ProjectTabs(root, "after-front", "after");
        var offered = after.TakeLeftBehind(["after-front"]);
        expect(offered.Select(t => t.Id).SequenceEqual(["dead-dirty"]) && offered.All(after.Adopt) && !Directory.Exists(me.FolderPath),
            "the next start offers that dirty tab again (taken in as one of its tabs), and the folder it came from goes");

        // The live session closes cleanly: its folder goes, nothing else.
        live.ForgetSession();
        expect(!Directory.Exists(live.FolderPath) && Directory.Exists(after.FolderPath), "a clean close deletes only its own session's folder");
        after.ForgetSession();
        expect(!Directory.Exists(after.FolderPath), "and the last session's too");
    }

    // ----- project-tabs-fresh-and-per-tab -----

    private static void FreshAndPerTab(Action<bool, string> expect)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var tabs = new ProjectTabs(Fresh("project-tabs-fresh"), "studio", "session");

        // Dropped files, then every clip deleted: nothing on the Sequence, but Undo can bring them back.
        var dropped = Open();
        expect(!dropped.HoldsNothingForTab, "a tab of dropped files holds something");
        foreach (var clip in dropped.Document.Tracks.SelectMany(t => t.Clips).ToList())
        {
            if (dropped.ViewOf(clip) is { } view)
                dropped.DeleteClip(view);
        }

        expect(!dropped.HasSource && !dropped.HoldsNothingForTab, "every clip deleted: no source, but its undo history is kept, so the tab is not let go as empty");
        expect(dropped.NewProject() && dropped.HoldsNothingForTab, "a new project is fresh: the only kind let go as empty");

        // The New Project studio with no files: a 9:16 frame chosen, a name and a folder.
        var vm = new MainViewModel();
        expect(vm.HoldsNothingForTab, "a view model as HandPeg starts holds nothing");
        expect(vm.TakeNewProject(new NewProjectRequest("Foo", "D:/Work", 1080, 1920, FrameRate.Fps30, ProjectColorSpace.Sdr709, null, [])),
            "the studio makes \"Foo\" in D:/Work, 1080 x 1920, no files");
        expect(!vm.HasSource && !vm.HoldsNothingForTab && vm.Document.ExplicitFrame && vm.NewProjectName == "Foo",
            "no source, yet not empty: its frame was chosen and it has a name (written to the cache like any tab, never reset by a switch)");
        var foo = tabs.Active;
        (foo.Title, foo.Empty, foo.NewProjectName, foo.NewProjectFolder, foo.WaitingTemplate) = ("Foo", false, vm.NewProjectName, vm.NewProjectFolder, new object());
        tabs.WriteTab(foo.Id, vm.CaptureStateForTab(foo.Title), null, ProjectTabs.HeaderJson(foo));
        var json = File.ReadAllText(tabs.HeaderFile(foo.Id));
        var header = ProjectTabs.ReadHeader(json);
        expect(header is { NewProjectName: "Foo", NewProjectFolder: "D:/Work", WaitingTemplate: null } && !json.Contains("WaitingTemplate", StringComparison.Ordinal),
            "its header carries the studio's name and folder; a waiting Game Preset stays in memory, never written");

        // Another tab (a new, blank project) in front, then "Foo" back, then the blank one again: the name and folder are each tab's own.
        expect(vm.NewProject() && vm.NewProjectName.Length == 0 && vm.HoldsNothingForTab, "a new project in the next tab: no name");
        var blank = vm.CaptureStateForTab("New Project");
        Resume(vm, tabs.ReadState(foo.Id), null, false, null, header?.NewProjectName ?? "", header?.NewProjectFolder);
        expect(vm.NewProjectName == "Foo" && vm.NewProjectFolder == "D:/Work" && vm.Document.ExplicitFrame && !vm.HasSource && !vm.HoldsNothingForTab,
            "\"Foo\" brought back: its name, folder and chosen frame are its own again");
        Resume(vm, blank, null, false, null);
        expect(vm.NewProjectName.Length == 0 && vm.NewProjectFolder is null && vm.TemplateWaitingForTab is null && !vm.Document.ExplicitFrame,
            "the blank tab brought back after it: \"Foo\"'s name, folder and frame do not follow (its first save never goes to D:/Work)");
        tabs.ForgetSession();
    }

    // ----- project-tabs-unresumed -----

    private static void Unresumed(Action<bool, string> expect)
    {
        var tabs = new ProjectTabs(Fresh("project-tabs-unresumed"), "front", "session");
        var stuck = tabs.Add("Offline Drive Project", "stuck");
        (stuck.Dirty, stuck.Empty) = (true, false);
        tabs.WriteTab(stuck.Id, new ProjectState { Name = "Offline Drive Project" }, "fingerprint of its saved state", ProjectTabs.HeaderJson(stuck));
        var before = File.ReadAllText(tabs.StateFile(stuck.Id));

        // It comes to the front but cannot be brought back (its media drive is not there); then a video is opened in front of it.
        tabs.Activate(stuck);
        var work = tabs.TakeWorkOff("gameplay");
        expect(!ReferenceEquals(work, stuck) && ReferenceEquals(tabs.Active, work) && tabs.Tabs.Contains(stuck) && tabs.Count == 3,
            "the new work is a new tab, in front; the tab not brought back stays on the strip");
        expect(stuck is { Title: "Offline Drive Project", Dirty: true, Empty: false }, "that tab is still what its file holds: its name and its unsaved changes");

        // The new work put behind: written under its own id, never over the old tab's file.
        tabs.WriteTab(work.Id, new ProjectState { Name = "gameplay" }, null, ProjectTabs.HeaderJson(work));
        expect(File.ReadAllText(tabs.StateFile(stuck.Id)) == before && tabs.ReadSaved(stuck.Id) == "fingerprint of its saved state" && tabs.ReadState(stuck.Id).Name == "Offline Drive Project",
            "the old tab's .hptab and saved fingerprint are as they were (switching to it tries again)");
        tabs.ForgetSession();
    }

    // ----- titlebar-tab-overflow -----

    private static void Overflow(Action<bool, string> expect)
    {
        var (gap, plus, more) = (ProjectTabStrip.TabGap, ProjectTabStrip.NewTabWidth, ProjectTabStrip.OverflowWidth);
        double Width(ProjectTabLayout layout, int count) => layout.Shown * (layout.TabWidth + gap) + plus + (layout.Shown < count ? more : 0);

        // The review's window: 630 DIPs for the tabs (a 960-DIP window, less the name, the buttons and the caption kept free).
        expect(ProjectTabStrip.LayoutFor(630, 8, 0) == new ProjectTabLayout(73, false, 0, 8), "8 tabs in 630: all shown with their names (73 each)");
        expect(ProjectTabStrip.LayoutFor(630, 9, 8) == new ProjectTabLayout(65, true, 0, 9), "9 tabs in 630: all shown, narrowed to their dot and initial (65 each, under the 72 a name needs)");
        expect(ProjectTabStrip.LayoutFor(630, 30, 25) == new ProjectTabLayout(28, true, 11, 19) && ProjectTabStrip.LayoutFor(630, 30, 0) == new ProjectTabLayout(28, true, 0, 19),
            "30 tabs in 630: 19 of 28 shown round the one in front (tabs 12 to 30 with the 26th in front; 1 to 19 with the first), the other 11 behind the count");
        expect(Width(ProjectTabStrip.LayoutFor(630, 30, 25), 30) <= 630, "and the strip, its count and + take no more than the 630");

        var wrong = new List<string>();
        foreach (var available in new double[] { 60, 90, 120, 200, 333, 630, 777, 1000, 1500 })
        {
            for (var count = 1; count <= 40; count++)
            {
                for (var active = 0; active < count; active += Math.Max(1, count / 4))
                {
                    var layout = ProjectTabStrip.LayoutFor(available, count, active);
                    var fits = Width(layout, count) <= available;
                    var holdsActive = layout.Shown == 0 || (active >= layout.First && active < layout.First + layout.Shown);
                    var sane = layout.TabWidth >= ProjectTabStrip.CompactTabWidth && layout.TabWidth <= ProjectTabStrip.MaxTabWidth
                               && layout.Compact == layout.TabWidth < ProjectTabStrip.MinTabWidth && layout.First >= 0 && layout.First + layout.Shown <= count;
                    if (!fits || !holdsActive || !sane)
                        wrong.Add(FormattableString.Invariant($"{available}/{count}/{active}"));
                }
            }
        }

        expect(wrong.Count == 0, $"from 60 to 1500 DIPs and 1 to 40 tabs: never wider than the room, the tab in front always shown, compact exactly below 72{(wrong.Count > 0 ? ": " + string.Join(", ", wrong.Take(8)) : "")}");

        // The numbers are the XAML's: + is 24 wide, the count 28, each tab's gap 2.
        var strip = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Controls", "ProjectTabStrip.xaml"));
        var tabStyle = strip.Descendants().First(e => e.Name.LocalName == "Style" && Attr(e, X + "Key") == "TabButton");
        var margin = tabStyle.Elements().FirstOrDefault(e => Attr(e, "Property") == "Margin");
        expect(Attr(Named(strip, "NewTabButton"), "Width") == "24" && Attr(Named(strip, "OverflowButton"), "Width") == "28" && Attr(margin, "Value") == "0,0,2,0"
               && Attr(Named(strip, "OverflowButton"), "Height") is "22",
            "ProjectTabStrip.xaml draws + 24 wide, the count 28 (22 high, under the caption) and a gap of 2 after each tab, as the layout counts them");
    }

    // ----- project-tabs-save-path -----

    private static void SavePath(Action<bool, string> expect)
    {
        var folder = Path.Combine(AppPaths.Temp, "project-tabs-save-path-none");
        var saved = Path.Combine(folder, "Tab One" + ProjectStore.Extension);
        var autosave = Path.Combine(folder, "Tab One" + ProjectStore.AutosaveSuffix + ProjectStore.Extension);
        expect(MainWindow.TabSavePath(saved, null, "Ignored", false) == (saved, false), "a saved tab goes to its own project file");
        expect(MainWindow.TabSavePath(saved, null, "Ignored", true) == (PortableProject.PathFor("Tab One"), true),
            "with fully portable saves on, to a portable folder of its name (its file is not in one yet), as Ctrl+S on the tab in front does");
        expect(MainWindow.TabSavePath(autosave, null, "Ignored", true) == (autosave, false), "an autosave is never made portable");
        expect(MainWindow.TabSavePath("", null, "My: Montage", false) == (Path.Combine(ProjectStore.Folder, "My_ Montage" + ProjectStore.Extension), false),
            "a tab never saved goes to the Projects folder, its name made fit for a file");
        expect(MainWindow.TabSavePath("", "D:/Work", "Foo", false) == (Path.Combine("D:/Work", "Foo" + ProjectStore.Extension), false), "or into the folder the New Project studio chose for it");
        expect(MainWindow.TabSavePath("", "D:/Work", "Foo", true) == (PortableProject.PathFor("Foo"), true), "or, with fully portable saves on, a portable folder of its name");
        expect(MainWindow.TabSavePath("", null, "  ", false).Path.EndsWith(Path.DirectorySeparatorChar + "Project" + ProjectStore.Extension, StringComparison.Ordinal), "a blank name is saved as \"Project\"");
    }

    // ----- project-tabs-never-lost -----

    private static string[] FilesIn(string folder) =>
        Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)] : [];

    private static void NeverLost(Action<bool, string> expect)
    {
        var root = Fresh("project-tabs-never-lost");

        // A session that ended without closing: a whole tab; a project file without a header (it ended between writing the two); a
        // project file whose header is damaged; a header with no project file; a half-written file. Its lock let go (its process gone).
        var dead = new ProjectTabs(root, "dead-front", "dead");
        var whole = dead.Add("Whole Tab", "whole");
        whole.Empty = false;
        dead.WriteTab(whole.Id, new ProjectState { Name = "Whole" }, null, ProjectTabs.HeaderJson(whole));
        ProjectStore.SaveTo(new ProjectState { Name = "Orphan" }, dead.StateFile("orphan"));
        ProjectStore.SaveTo(new ProjectState { Name = "Damaged" }, dead.StateFile("damaged"));
        File.WriteAllText(dead.HeaderFile("damaged"), "{ \"Id\": \"dam");
        File.WriteAllText(dead.HeaderFile("ghost"), ProjectTabs.HeaderJson(Header("ghost", "Ghost", false)));
        File.WriteAllText(dead.StateFile("whole") + ".tmp", "{ half");
        dead.ReleaseSession();

        var left = ProjectTabs.LeftBehind(dead.FolderPath, []);
        expect(string.Join(" ", left.Select(t => t.Id).Order(StringComparer.Ordinal)) == "damaged orphan whole",
            "its leftovers: the whole tab, and the two project files without a header that reads (the header alone is none)");
        expect(left.Where(t => t.Id != "whole").All(t => t is { Title: ProjectTabs.RecoveredTitle, Dirty: true, Empty: false }) && left.Single(t => t.Id == "whole").Title == "Whole Tab",
            "those two under a made-up header: \"Recovered Project\", with unsaved changes (closing it asks first), not empty");

        // Another dead session whose project file cannot come across (its id is a tab of the new session's already).
        var clash = new ProjectTabs(root, "clash-front", "clash");
        clash.ClaimSession();
        ProjectStore.SaveTo(new ProjectState { Name = "Clash" }, clash.StateFile("keep-out"));
        clash.ReleaseSession();

        // A new session takes them in.
        var me = new ProjectTabs(root, "me-front", "me");
        var taken = me.TakeLeftBehind(["me-front", "keep-out"]);
        expect(string.Join(" ", taken.Select(t => t.Id).Order(StringComparer.Ordinal)) == "damaged orphan whole" && taken.All(t => File.Exists(me.StateFile(t.Id)) && File.Exists(me.HeaderFile(t.Id))),
            "the new session takes all three in, each with its project file and a header in its own folder");
        expect(me.ReadState("orphan").Name == "Orphan" && me.ReadState("damaged").Name == "Damaged" && ProjectTabs.ReadHeader(File.ReadAllText(me.HeaderFile("orphan"))) is { Title: ProjectTabs.RecoveredTitle, Dirty: true },
            "the project file without a header is the project it was, its made-up header written beside it");
        expect(!Directory.Exists(dead.FolderPath), "the dead folder goes once every project file came across (its old headers, the lone header and the half-written file with it)");
        expect(string.Join(" ", FilesIn(clash.FolderPath)) == "keep-out.hptab session.lock" && taken.All(t => t.Id != "keep-out"),
            "a project file that could not come across is never deleted: its folder stays, with its lock file, for the next start");

        // HandPeg closes before the recovered orphan reached the strip (the window waits for it; the cache does not rely on that).
        foreach (var tab in taken.Where(t => t.Id != "orphan"))
            me.Adopt(tab);
        me.WriteManifest(me.ManifestJson());
        File.WriteAllText(me.StateFile("whole") + ".tmp", "{ half");
        me.ForgetSession();
        expect(string.Join(" ", FilesIn(me.FolderPath)) == "orphan.hptab orphan.tab.json session.lock" && !me.HoldsSession,
            "a clean close deletes only its own tabs' files, tabs.json and half-written files: a project file of no tab of its own stays, with its header and the lock file, let go");

        var next = new ProjectTabs(root, "next-front", "next");
        var again = next.TakeLeftBehind(["next-front", "keep-out"]);
        expect(again.Count == 1 && again[0] is { Id: "orphan", Title: ProjectTabs.RecoveredTitle, Dirty: true } && !Directory.Exists(me.FolderPath),
            "the next start offers it again, and the folder it was left in goes");
        next.Adopt(again[0]);
        next.ForgetSession();
        expect(!Directory.Exists(next.FolderPath) && Directory.Exists(clash.FolderPath), "a clean close that knows every tab deletes its folder, and nothing of another's");
    }
}
