using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.5A lane C: closing HandPeg with tabs (kept for the next start, or one dialog for every unsaved tab), Close Other Tabs, and the restored
/// tabs sliding out at start. tabs-exit-plan: the plan's table (kept, today's way, forgotten and asked), the tabs kept for the next start
/// written exactly (the tab in front included, its lagged recovery file overwritten on its queue) and taken back in tabs.json's order with
/// their dots, and the close's wiring. tabs-exit-dialog: the one dialog's rows, its Save rule, its buttons' words for each purpose and its
/// answers (never shown on screen). project-tabs-slide-out: a real strip off screen: the restored tabs start at the tab in front and slide to
/// their places with + (180 ms, cubic ease out, frozen, gone at the end), the tab in front on top, nothing left behind, and only after the
/// first frame (RecoverProjectTabs). tabs-kept-file-stamp (the review's fix): a tab kept with nothing unsaved keeps its project file's stamp,
/// and one brought back after that file changed on the disk opens from the file (a missing file, or unsaved work, keeps the cache).
/// The 5.5A addenda: tabs-exit-plan asks whenever closing would drop work (two tabs holding work, saved or not; one never saved), with
/// Keep open off at first (and turned off once in a preview's settings); project-tabs-restored-front: the tabs a session left come back
/// with the one in front last time in front, and no blank start-up tab left before them.
/// The 5.5A final review: tabs-kept-file-stamp: the stamp is the one carried from the moment the project and its file were the same (an open,
/// a save, a read of the file), never looked up as the header is written (a file changed before the clean close's write, or before a rename
/// of a tab behind, is read back from the file); a changed file that cannot be read (a newer format, damaged) comes back from the cache, tied to
/// no file, with why. project-tabs-restored-front: only a close that kept the tabs on purpose brings one forward (a crash's leftovers stay
/// behind the start-up tab), a tab with unsaved changes brought back keeps its file as its crash-recovery copy, the tab brought forward keeps
/// its own layout until it is laid out, and no tab is taken in while a close is asking.
/// The 5.5A fix round 3: tabs-kept-file-stamp: an open's stamp is taken before the open reads the file (driven through OpenProjectInTabAsync), a
/// save's off the tab's queue, and only the last asked lands; project-tabs-restored-front: a kept session whose tab in front was blank brings
/// none forward, and RecoverProjectTabs put off by a close, then queued again as it is called off, is driven on the window.
/// </summary>
public static class TabExitTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string First = "D:/media/first.mkv", Second = "D:/media/second.mkv";

    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("tabs-exit-plan", "# Closing HandPeg: kept for the next start (every tab exact, the one in front too), today's way with the prompt off, or asked whenever closing would drop work.", ExitPlan);
        yield return Case("tabs-kept-file-stamp", "# A tab kept with nothing unsaved keeps its project file's stamp; brought back after that file changed on the disk, it opens from the file.", KeptFileStamp);
        yield return Case("project-tabs-restored-front", "# The tabs a session left come back with the one in front last time in front (resumed as a switch resumes it), and no blank start-up tab before them.", RestoredFront);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("tabs-exit-dialog", "# One dialog for every tab holding work: a checked row for each unsaved one (a saved one shown as saved), a name for one never saved, Save only when it can, and the words of each purpose.", ExitDialog);
        yield return Case("project-tabs-slide-out", "# Restored tabs slide out from behind the tab in front after the first frame: 180 ms, cubic ease out, frozen, the tab in front on top, nothing left behind.", SlideOut);
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

    /// <summary>A view model holding a Sequence of dropped files (as ProjectTabsTests.Open), taken as saved, with <paramref name="edits"/> edits after it.</summary>
    private static MainViewModel Holding(int edits)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports, settings.DroppedClipsJoinTrack) = (false, false, false);
        var vm = new MainViewModel();
        var document = new TimelineDocument { Width = 1920, Height = 1080, NaturalWidth = 1920, NaturalHeight = 1080, FrameRate = FrameRate.Fps30 };
        var one = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        one.Clips.AddRange([DogfoodTests.FileClip(First, 0, 4), DogfoodTests.FileClip(First, 4, 8, 4), DogfoodTests.FileClip(First, 8, 12, 8)]);
        var two = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 2 };
        two.Clips.Add(DogfoodTests.FileClip(Second, 0, 6));
        document.Tracks.AddRange([one, two]);
        document.MediaPool.AddRange([First, Second]);
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [document]);
        vm.MarkSaved();
        for (var i = 0; i < edits; i++)
            vm.TrimClip(vm.ViewOf(vm.Document.Tracks[0].Clips[i % 3]), start: false, seconds: 4 * (i % 3) + 2 + 0.5 * (i / 3));
        return vm;
    }

    private static string Shape(TimelineDocument document) =>
        string.Join(" | ", document.Tracks.Select(t => string.Join(", ", t.Clips.Select(c => FormattableString.Invariant($"{c.Id} {Path.GetFileName(c.SourcePath)} {c.Start:0.###}-{c.End:0.###}")))));

    // ----- tabs-exit-plan -----

    private static void ExitPlan(Action<bool, string> expect)
    {
        // The plan's table (pure): the tabs holding work, those of them with work not saved, and whether that one is the project in front.
        static string Plan(bool keepOpen, bool prompt, int held, int unsaved, bool inFront = true) =>
            MainWindow.ExitPlanFor(keepOpen, prompt, held, unsaved, inFront) is var (plan, ask) ? $"{plan}{(ask == MainWindow.ExitQuestion.None ? "" : $" {ask}")}" : "";
        expect(Plan(true, true, 3, 2) == "SuspendAll" && Plan(true, false, 1, 1) == "SuspendAll" && Plan(true, true, 2, 0) == "SuspendAll",
            "Keep open project tabs between sessions on: every tab kept for the next start, nothing asked (whatever the prompt says)");
        expect(Plan(false, false, 3, 2) == "KeepDirtyBehind" && Plan(false, false, 1, 1) == "KeepDirtyBehind" && Plan(false, false, 2, 0) == "KeepDirtyBehind",
            "both off (Keep open and Prompt to save before closing): as before, nothing asked (the unsaved tabs behind kept)");
        expect(Plan(false, true, 3, 1) == "Forget Tabs" && Plan(false, true, 2, 2) == "Forget Tabs" && Plan(false, true, 2, 1, inFront: false) == "Forget Tabs",
            "(addenda) the defaults, two tabs or more holding work, one of them or more not saved: asked, in one dialog listing every one of them (the saved shown as saved)");
        expect(Plan(false, true, 2, 0) == "Forget" && Plan(false, true, 5, 0) == "Forget" && Plan(false, true, 1, 0) == "Forget" && Plan(false, true, 0, 0) == "Forget",
            "(5.5.0, Q2 b) every tab holding work saved (two or more, or one), or none holding work: closed with no question");
        expect(Plan(false, true, 1, 1) == "Forget Classic",
            "(addenda) one project alone in front with work not saved (changes, or never saved at all): the classic question (MayClose, SaveOnExitDialog)");
        expect(Plan(false, true, 1, 1, inFront: false) == "Forget Tabs",
            "(addenda) one alone with work not saved behind a blank tab in front (or in front, not brought back): the dialog with its one row (the classic question saves only the project in front)");
        expect(!new AppSettings().KeepOpenTabsOnClose && new AppSettings().PromptToSaveOnExit
               && !JsonSerializer.Deserialize("{}", AppSettingsJsonContext.Default.AppSettings)!.KeepOpenTabsOnClose,
            "(addenda) Keep open project tabs is off at first (a settings file without it too) and Prompt to save before closing on: closing asks by default");

        // A project never saved, with content, counts as unsaved for both paths (the tabs' dots and the classic question).
        var never = Holding(0);
        expect(never.HasSource && !never.HoldsNothingForTab && never.CurrentProjectPath.Length == 0 && !never.HasUnsavedChanges,
            "(addenda) a Sequence of dropped files never saved as a project, untouched since it was opened: HasUnsavedChanges alone says there is nothing to save (the count before missed it)");
        expect(MainWindow.UnsavedForExit(never.MayHaveUnsavedChanges && never.HasUnsavedChanges, never.HoldsNothingForTab, never.CurrentProjectPath)
               && MainWindow.UnsavedForExit(false, false, ""),
            "(addenda) it counts as work not saved: in front (its dot from MayHaveUnsavedChanges and HasUnsavedChanges, with no project file) and behind (a clean header with no project file)");
        expect(!MainWindow.UnsavedForExit(false, false, "D:/projects/Saved.hproj") && MainWindow.UnsavedForExit(true, false, "D:/projects/Saved.hproj")
               && !MainWindow.UnsavedForExit(false, true, "") && !MainWindow.UnsavedForExit(true, true, ""),
            "(addenda) a saved project with no changes does not count; one with changes does; an empty tab never does");

        // The one-time settings step (ViewDefaultsVersion 12): a 5.5A preview saved Keep open on; it goes off once.
        SettingsStep(expect);

        // Kept for the next start: every tab holding work written exactly, in its order, the tab in front included.
        var root = Fresh("tabs-exit-plan");
        var tabs = new ProjectTabs(root, "front", "closing");
        var front = tabs.Active;
        var (b, c, blank) = (tabs.Add("Beta", "beta"), tabs.Add("Gamma", "gamma"), tabs.Add("New Project", "blank"));
        tabs.Move(front, 1);
        var (vmFront, vmBeta, vmGamma) = (Holding(3), Holding(2), Holding(0));
        (front.Title, front.Dirty, front.Empty) = ("Front", true, false);
        (b.Dirty, b.Empty, c.Dirty, c.Empty) = (true, false, false, false);

        // The tab in front had a crash-recovery file written two actions back (MainWindow.TabCache.cs): the close's exact write follows it on its queue.
        var frontSaved = vmFront.SavedFingerprintForTab;
        expect(MainWindow.WriteRecoveryFile(tabs, front.Id, vmFront.CaptureStateForTab("Front"), vmFront.TakeRecoveryLag(), frontSaved, ProjectTabs.HeaderJson(front)) == true,
            "the tab in front's crash-recovery file is there, two actions back");
        var exactFront = vmFront.CaptureStateForTab("Front");
        _ = tabs.Enqueue(front.Id, () => tabs.WriteTab(front.Id, exactFront, frontSaved, ProjectTabs.HeaderJson(front)));
        tabs.WriteTab(b.Id, vmBeta.CaptureStateForTab("Beta"), vmBeta.SavedFingerprintForTab, ProjectTabs.HeaderJson(b));
        tabs.WriteTab(c.Id, vmGamma.CaptureStateForTab("Gamma"), null, ProjectTabs.HeaderJson(c));
        expect(tabs.WaitFor(null, 10_000), "every tab's disk work done");
        tabs.WriteKeptManifest();
        tabs.ForgetSession(t => !t.Empty);
        expect(File.Exists(tabs.ManifestFile) && !File.Exists(tabs.StateFile(blank.Id)) && new[] { front, b, c }.All(t => File.Exists(tabs.StateFile(t.Id))),
            "the session's folder keeps every tab holding work and tabs.json (the blank tab keeps nothing)");

        var next = new ProjectTabs(root, "start", "next");
        var left = next.TakeLeftBehind([next.Active.Id]);
        expect(string.Join(" ", left.Select(t => t.Id)) == "beta front gamma", "the next start takes them back in tabs.json's order (the order they had on the strip)");
        expect(left.Select(t => t.Dirty).SequenceEqual([true, true, false]) && left.All(t => !t.Empty), "each with its dot: Beta and the tab in front unsaved, Gamma saved");
        var frontState = next.ReadState("front");
        expect(Shape(frontState.Timeline) == Shape(vmFront.Document) && frontState.UndoHistory?.Count == 3 && frontState.RedoHistory is null,
            "the tab in front exactly as it was (N, its three steps to undo, nothing to redo): never lagged, its recovery file overwritten");
        expect(Shape(next.ReadState("beta").Timeline) == Shape(vmBeta.Document) && Shape(next.ReadState("gamma").Timeline) == Shape(vmGamma.Document) && next.ReadSaved("beta") is { Length: > 0 },
            "and the tabs behind exactly as they were, the unsaved one with its saved state's fingerprint");
        next.ForgetSession();

        // The close's wiring (the window's side).
        var tabsSource = Source("MainWindow.ProjectTabs.cs");
        var exit = Source("MainWindow.TabExit.cs");
        var window = Source("MainWindow.xaml.cs");
        var menu = Source("MainWindow.TabMenu.cs");
        expect(Ordered(MemberOf(tabsSource, "private bool MayCloseProjectTabs("), "FinishTabWrites();", "ExitAnsweredByTabs = false;",
                   "if (settings.PromptToSaveOnExit && !settings.KeepOpenTabsOnClose)", "RefreshActiveTab();", "var held = _tabs.Tabs.Where(t => !t.Empty).ToList();",
                   "UnsavedForExit(t)", "ExitPlanFor(settings.KeepOpenTabsOnClose, settings.PromptToSaveOnExit, held.Count, unsaved.Count,",
                   "unsaved.Count == 1 && !KeptInFile(unsaved[0])", "case TabExitPlan.SuspendAll:", "SuspendFrontForExit();", "case TabExitPlan.KeepDirtyBehind:",
                   "case ExitQuestion.Tabs:", "return AskAboutTabsOnExit(held);", "case ExitQuestion.Classic:", "return true;", "default:", "ExitAnsweredByTabs = true;"),
            "the close takes its plan from the settings once every tab's disk work is done, the tab in front's dot made exact first whenever it may ask (one project alone too)");
        expect(Ordered(MemberOf(tabsSource, "private void ForgetProjectTabs("), "FinishTabWrites();", "_tabExitPlan == TabExitPlan.Forget", "_tabs.ForgetSession();",
                   "WaitForKeptTabWrites();", "_tabExitPlan == TabExitPlan.SuspendAll ? t => !t.Empty : t => KeptInFile(t) && t.Dirty && !t.Empty",
                   "WriteKeptTabsNow(keep);", "CancelTabWrites(keep);", "_tabs.WriteKeptManifest();", "_tabs.ForgetSession(keep);"),
            "and carries it out: every file forgotten; or the kept tabs written, the rest let go, tabs.json written (marked kept) when tabs are kept for the next start");
        expect(Ordered(MemberOf(window, "private bool MayClose("), "if (!MayCloseProjectTabs())", "if (ExitAnsweredByTabs)", "return true;", "PromptToSaveOnExit", "new SaveOnExitDialog("),
            "MayClose asks nothing more once the tabs answered for the tab in front; one project alone keeps the classic question");
        expect(Ordered(MemberOf(window, "private bool MayClose("), "var unsaved = _exitQuestion == ExitQuestion.Classic;", "if (!unsaved || !AppSettings.Current.PromptToSaveOnExit)", "new SaveOnExitDialog(")
               && !MemberOf(window, "private bool MayClose(").Contains("var unsaved = _viewModel.HasUnsavedChanges;", StringComparison.Ordinal)
               && MemberOf(window, "private bool MayCloseAfterFault(").Contains("!UnsavedForExit(false, _viewModel.HoldsNothingForTab, _viewModel.CurrentProjectPath)", StringComparison.Ordinal),
            "(addenda) the classic question is the plan's (work never saved counted), never HasUnsavedChanges alone; a fault's own question counts it too");
        expect(Ordered(MemberOf(window, "private bool MayClose("), "var projectPath = _viewModel.CurrentProjectPath;", "new SaveOnExitDialog(", ", projectPath)",
                   "case ExitChoice.SaveAndClose:", "projectPath.Length > 0 ? _viewModel.SaveProjectTo(projectPath) : _viewModel.SaveProject(dialog.ProjectName)"),
            "(review) the classic question's Save & Close saves a project with a file to that file, as Ctrl+S does; a name only for work never saved");
        expect(MainWindow.RowDetail("D:/projects/Alpha.hproj", 0) == "D:/projects/Alpha.hproj" && MainWindow.RowDetail("", 0) == "Never saved"
               && MainWindow.RowDetail("D:/projects/Alpha.hproj", 2) == "Recovered 2 actions back · D:/projects/Alpha.hproj" && MainWindow.RowDetail("", 2) == "Recovered 2 actions back · Never saved",
            "(review) a row's detail: its project file or Never saved, after \"Recovered 2 actions back\" for a tab whose file is a lagged crash-recovery copy");
        var suspend = MemberOf(exit, "private bool TrySuspendActiveTab(");
        expect(Ordered(suspend, "if (_unresumed.Contains(tab.Id))", "HoldsNothingForTab", "CaptureWindowStateForTab(tab);", "StopPlayingForTabs();", "TakeHeaderForTab(tab);",
                   "tab.Dirty = _viewModel.HasUnsavedChanges;", "_viewModel.CaptureStateForTab(tab.Title)", "_tabs.Enqueue(id, () => _tabs.WriteTab(id, state, saved, header)).Wait();")
               && !suspend.Contains("TakeRecoveryLag", StringComparison.Ordinal),
            "the tab in front kept exactly as it is (never lagged), on its own queue after its recovery file, a tab not brought back kept as its file is");
        expect(Ordered(MemberOf(exit, "private bool SuspendFrontForExit("), "if (TrySuspendActiveTab(out var why))", "if (!front.Dirty)", "DefaultsToCancel = true, CancelText = \"Keep HandPeg Open\"",
                   "DeleteTabFiles(front.Id);"),
            "one that could not be kept with unsaved changes asks whether to close anyway, staying by default (and keeps no half-written file in front)");
        expect(Ordered(MemberOf(tabsSource, "private bool MayCloseProjectTabs("), "RefreshActiveTab();", "ExitPlanFor(") && Ordered(MemberOf(exit, "private bool AskAboutTabsOnExit("), "var rows = RowsOf(held);", "SaveTabsOnExitDialog.Purpose.Exit", "case TabsExitChoice.SaveChecked:",
                   "SaveCheckedTabs(rows, failed);", "!CloseAnyway(failed)", "return false;", "case TabsExitChoice.KeepSuspended:", "_tabExitPlan = TabExitPlan.SuspendAll;",
                   "case TabsExitChoice.Discard:", "default:", "return false;", "ExitAnsweredByTabs = true;"),
            "the one dialog: Save Checked & Close (a failed save asks first), Keep Suspended for Next Launch, Discard & Close; Cancel stays");
        expect(Ordered(MemberOf(exit, "private List<TabExitRow> RowsOf("), "if (!UnsavedForExit(tab))", "saved: true", "continue;", "new TabExitRow(tab.Id, tab.Title, RowDetail(path, tab.StepsBack), path.Length == 0, name)"),
            "(addenda) it lists every tab holding work: a saved one as a row shown as saved, an unsaved one checked (with a name when never saved)");
        expect(MemberOf(exit, "private bool CloseAnyway(").Contains("DefaultsToCancel = true, CancelText = \"Keep HandPeg Open\"", StringComparison.Ordinal)
               && Ordered(MemberOf(exit, "private void SaveCheckedTabs("), "rows.Where(r => r.IsChecked && !r.Saved)", "SaveTabInPlace(tab, name)", "_viewModel.SaveProjectTo(_viewModel.CurrentProjectPath)", "_viewModel.SaveProject(name)"),
            "a tab behind saved from its file, the tab in front to its project file or under the row's name (a saved row never); closing after a failed save is never the default");
        expect(Ordered(MemberOf(menu, "private async Task CloseOtherProjectTabsAsync("), "TakeWrittenDots();", ">= 2", "await CloseOthersAskedOnceAsync(keep);")
               && Ordered(MemberOf(menu, "private async Task CloseOthersAskedOnceAsync("), "SaveTabsOnExitDialog.Purpose.CloseOthers", "case TabsExitChoice.SaveChecked:", "SaveTabInPlace(",
                   "staying.Add(tab.Id);", "case TabsExitChoice.KeepSuspended:", "default:", "return;", "await CloseProjectTabAsync(other, asked: true);")
               && MemberOf(tabsSource, "private async Task CloseProjectTabAsync(").Contains("if (!asked && UnsavedForExit(tab) && !await AskToSaveTabAsync(tab))", StringComparison.Ordinal),
            "Close Other Tabs with two or more unsaved: the same dialog in its own words, each tab then closed with no second question (one whose save failed stays)");
        expect(Ordered(MemberOf(tabsSource, "private async Task<bool> AskToSaveTabAsync("), "var never = !tab.Dirty && (KeptInFile(tab) ? tab.ProjectPath : _viewModel.CurrentProjectPath).Length == 0;",
                   "was never saved as a project.", "has changes that were not saved as a project.", "return FirstSaveInFront();"),
            "(5.5.0, Q3 b) closing one tab (x, Ctrl+W) asks by the exit's rule (UnsavedForExit): a never-saved tab with content asks too, in its own words, and is saved as a first save");
        var settingsXaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Dialogs", "SettingsWindow.xaml"));
        expect(Ordered(settingsXaml, "IsChecked=\"{Binding PromptToSaveOnExit}\"", "IsChecked=\"{Binding KeepOpenTabsOnClose}\"", "AutomationProperties.Name=\"Keep Open Project Tabs Between Sessions\"")
               && !File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Dialogs", "SaveOnExitDialog.xaml")).Contains("Behavior tab", StringComparison.Ordinal),
            "Settings > General > Projects and saving holds the setting under the prompt; the classic question names that page (not a Behavior tab)");
        expect(settingsXaml.Contains("ToolTip=\"Off at first. Ticked, closing HandPeg keeps every open project tab", StringComparison.Ordinal)
               && settingsXaml.Contains("Off: closing asks when a tab holds work not saved", StringComparison.Ordinal) && !settingsXaml.Contains("On at first. Closing HandPeg keeps", StringComparison.Ordinal),
            "(addenda) its tooltip says it is off at first, and that off, closing asks when a tab holds work not saved (5.5.0)");
    }

    /// <summary>AppSettings.Load on files of the test's own, in the test's data folder (as ExportPresetsTests): what was there is put back after.</summary>
    private static void SettingsStep(Action<bool, string> expect)
    {
        var file = AppSettings.FilePath;
        var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        var load = typeof(AppSettings).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes)!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AppSettings Read(int version, bool keepOpen)
            {
                File.WriteAllText(file, JsonSerializer.Serialize(new AppSettings { ViewDefaultsVersion = version, FirstRunComplete = true, KeepOpenTabsOnClose = keepOpen },
                    AppSettingsJsonContext.Default.AppSettings));
                return (AppSettings)load.Invoke(null, null)!;
            }

            var preview = Read(11, true);
            var chosen = Read(12, true);
            var first = Read(0, true);
            var off = Read(11, false);
            expect(preview is { ViewDefaultsVersion: >= 12, KeepOpenTabsOnClose: false } && off is { ViewDefaultsVersion: >= 12, KeepOpenTabsOnClose: false },
                "(addenda) a settings file a 5.5A preview saved with Keep open on (ViewDefaultsVersion 11) reads with it off, once (version 12)");
            expect(chosen.KeepOpenTabsOnClose && first is { ViewDefaultsVersion: >= 12, KeepOpenTabsOnClose: true },
                "(addenda) ticked since (a version 12 file), or in a first start's own file (version 0), it stays on");
        }
        finally
        {
            if (original is not null)
                File.WriteAllBytes(file, original);
            else
                File.Delete(file);
        }
    }

    // ----- project-tabs-restored-front -----

    private static void RestoredFront(Action<bool, string> expect)
    {
        // The rule (pure).
        expect(MainWindow.BringsRestoredForward(true, true, "", false, false, false),
            "the start-up tab alone, blank and untouched, no project to open: the tab in front last time is brought forward");
        expect(!MainWindow.BringsRestoredForward(false, true, "", false, false, false) && !MainWindow.BringsRestoredForward(true, false, "", false, false, false)
               && !MainWindow.BringsRestoredForward(true, true, "D:/projects/Open.hproj", false, false, false),
            "not with another tab beside it (Ctrl+T made one), work in it, or a project file open in it: the restored tabs stay behind it, as before");
        expect(!MainWindow.BringsRestoredForward(true, true, "", true, false, false) && !MainWindow.BringsRestoredForward(true, true, "", false, true, false)
               && !MainWindow.BringsRestoredForward(true, true, "", false, false, true),
            "nor with the blank tab shown on purpose (Ctrl+T or +), something under way in it (a switch, an open, an import, a drop, a drag, the close), or HandPeg started with a project to open");
        static ProjectTabHeader Header(string id, long last) => new() { Id = id, LastActive = last };
        expect(ProjectTabs.FrontOf([Header("a", 3), Header("b", 9), Header("c", 5)], "c") == "c" && ProjectTabs.FrontOf([Header("a", 3), Header("b", 9)], "gone") is null
               && ProjectTabs.FrontOf([Header("a", 3), Header("b", 9)], null) is null && ProjectTabs.FrontOf([], "a") is null,
            "a session's tab in front: the one its tabs.json named when it was taken; (fix round 3) none when it was not (a blank tab in front, never kept) or none was named, never the one last in front by its header; none for none");

        // A session that closed keeping its tabs (Beta in front), then the next start.
        var root = Fresh("project-tabs-restored-front");
        var state = Holding(1).CaptureStateForTab("Kept");
        var dead = new ProjectTabs(root, "dead-blank", "dead");
        var (a, b, c) = (dead.Add("Alpha", "alpha"), dead.Add("Beta", "beta"), dead.Add("Gamma", "gamma"));
        foreach (var tab in new[] { a, b, c })
            tab.Empty = false;
        (a.Dirty, c.Dirty) = (true, true);
        dead.Activate(c);
        dead.Activate(b);
        foreach (var tab in new[] { a, b, c })
            dead.WriteTab(tab.Id, state, tab.Dirty ? "fingerprint" : null, ProjectTabs.HeaderJson(tab));
        expect(!dead.ManifestJson().Contains("Kept", StringComparison.Ordinal) && dead.ManifestJson(kept: true).Contains("\"Kept\":true", StringComparison.Ordinal)
               && JsonSerializer.Deserialize<ProjectTabManifest>("{\"Order\":[\"a\"],\"Active\":\"a\"}") is { Kept: false },
            "(final review) tabs.json says Kept only as written by the close that keeps the tabs (left out otherwise, as before; one without it reads as not kept)");
        dead.WriteManifest(dead.ManifestJson());
        dead.WriteKeptManifest();
        dead.WriteManifest(dead.ManifestJson());
        expect(ProjectTabs.ReadManifest(dead.FolderPath) is { Kept: true, Active: "beta" },
            "(final review) the close's kept tabs.json is the session's last word: a write from before it still on its way never lands over it");
        dead.ForgetSession(t => !t.Empty);

        var next = new ProjectTabs(root, "start", "next");
        var known = next.Tabs.Select(t => t.Id).ToList();
        var left = next.TakeLeftBehind(known, out var front);
        expect(front == "beta" && string.Join(" ", left.Select(t => t.Id)) == "alpha beta gamma",
            "the next start takes the tabs back in their order and knows the one in front last time: Beta (tabs.json's)");
        var adopted = left.Where(next.Adopt).Select(t => t.Id).ToList();
        var blank = next.Active;
        expect(adopted.Count == 3 && MainWindow.BringsRestoredForward(known.Count == 1 && next.Count == adopted.Count + 1 && next.Active.Id == known[0], true, "", false, false, false),
            "taken in behind the blank start-up tab, the only tab but them and in front: Beta is brought forward");
        var at = MainWindow.SwapStartTabFor(next, blank, next.Find(front!)!);
        expect(next.Active.Id == "beta" && next.Count == adopted.Count && next.Find("start") is null && string.Join(" ", next.Tabs.Select(t => t.Id)) == "alpha beta gamma" && at == 0,
            "brought forward: Beta in front, no blank tab left, as many tabs as came back (3), in the order they had");
        var back = MainWindow.PutStartTabBack(next, at);
        expect(ReferenceEquals(next.Active, back) && back is { Title: "New Project", Empty: true } && next.IndexOf(back) == 0 && next.Count == 4
               && next.Find("beta") is not null && File.Exists(next.StateFile("beta")),
            "its resume failing instead: a blank tab back in front where the start-up tab was, Beta behind it with its files kept (a click tries again)");
        next.ForgetSession();

        // Two sessions left tabs, each closed keeping them: the front of the one that wrote last.
        ProjectTabs Left(string folder, string session, bool kept, params (string Id, long Order)[] tabs)
        {
            var gone = new ProjectTabs(folder, session + "-blank", session);
            foreach (var (id, order) in tabs.OrderBy(t => t.Order))
            {
                var tab = gone.Add(id, id);
                tab.Empty = false;
                gone.Activate(tab);
            }

            foreach (var tab in gone.Tabs.Where(t => !t.Empty))
                gone.WriteTab(tab.Id, state, null, ProjectTabs.HeaderJson(tab));

            // Closed keeping them (the close's tabs.json marked kept), or fallen over (its last tabs.json of the session, its lock let go by Windows).
            gone.WriteManifest(gone.ManifestJson());
            if (kept)
            {
                gone.WriteKeptManifest();
                gone.ForgetSession(t => !t.Empty);
            }
            else
            {
                gone.ReleaseSession();
            }

            return gone;
        }

        var two = Fresh("project-tabs-restored-front-two");
        var older = Left(two, "older", true, ("o1", 2), ("o2", 1));
        foreach (var id in new[] { "o1", "o2" })
            File.SetLastWriteTimeUtc(older.StateFile(id), DateTime.UtcNow.AddHours(-2));
        Left(two, "newer", true, ("n1", 2), ("n2", 1));
        var third = new ProjectTabs(two, "start-two", "third");
        var taken = third.TakeLeftBehind([third.Active.Id], out var newest);
        expect(taken.Count == 4 && newest == "n1", "the tab last in front (n1) of the session that wrote last, not of the older one (o1)");
        third.ForgetSession();

        // (final review, AD-1) A session that fell over (its tabs.json the session's own, never marked kept): its tabs are taken in all the same, with
        // none to bring forward, so they stay behind the blank start-up tab as before AD-1 and the project that may have brought HandPeg down is
        // never opened unasked (nor its crash-recovery copy let go as it opens).
        var crash = Fresh("project-tabs-restored-front-crash");
        Left(crash, "crashed", false, ("c1", 1), ("c2", 2));
        var afterCrash = new ProjectTabs(crash, "start-crash", "after");
        var crashKnown = afterCrash.Tabs.Select(t => t.Id).ToList();
        var crashLeft = afterCrash.TakeLeftBehind(crashKnown, out var crashFront);
        var crashAdopted = crashLeft.Where(afterCrash.Adopt).Count();
        expect(crashFront is null && crashLeft.Count == 2 && crashAdopted == 2 && afterCrash.Active.Id == "start-crash" && afterCrash.Count == 3,
            "(final review) a session that fell over: its two tabs taken in behind the blank start-up tab, none named to bring forward (RestoredFrontToBring then brings none)");
        afterCrash.ForgetSession();

        // Both at once: the newer fell over, the older closed keeping its tabs: the kept one's tab in front comes forward, never the crash's.
        var mixed = Fresh("project-tabs-restored-front-mixed");
        var keptOlder = Left(mixed, "kept-older", true, ("k1", 2), ("k2", 1));
        foreach (var id in new[] { "k1", "k2" })
            File.SetLastWriteTimeUtc(keptOlder.StateFile(id), DateTime.UtcNow.AddHours(-2));
        Left(mixed, "crashed-newer", false, ("x1", 2), ("x2", 1));
        var fourth = new ProjectTabs(mixed, "start-mixed", "fourth");
        expect(fourth.TakeLeftBehind([fourth.Active.Id], out var mixedFront).Count == 4 && mixedFront == "k1",
            "(final review) a kept session and a newer one that fell over: the kept one's tab in front (k1) is the one brought forward");
        fourth.ForgetSession();

        // (fix round 3) A session that closed keeping its tabs with a blank tab in front (the start-up tab, or Ctrl+T): tabs.json names it, but a blank
        // tab is never kept, so it is not taken: none is brought forward and the start-up tab stays in front, never the tab used last (a crash's
        // leftover that session took in and never opened would be opened unasked one session later).
        (ProjectTabs Gone, ProjectTabManifest? Manifest) KeptWithBlankInFront(string folder, string session, params string[] ids)
        {
            var gone = new ProjectTabs(folder, session + "-blank", session);
            var blankTab = gone.Active;
            foreach (var id in ids)
            {
                var tab = gone.Add(id, id);
                tab.Empty = false;
                gone.Activate(tab);
                gone.WriteTab(tab.Id, state, null, ProjectTabs.HeaderJson(tab));
            }

            gone.Activate(blankTab);
            gone.WriteKeptManifest();
            var manifest = ProjectTabs.ReadManifest(gone.FolderPath);
            gone.ForgetSession(t => !t.Empty);
            return (gone, manifest);
        }

        var blankFront = Fresh("project-tabs-restored-front-blank");
        var (_, namedBlank) = KeptWithBlankInFront(blankFront, "kept-blank", "u1", "u2");
        var fifth = new ProjectTabs(blankFront, "start-blank", "fifth");
        var blankTaken = fifth.TakeLeftBehind([fifth.Active.Id], out var blankFrontId);
        expect(namedBlank is { Kept: true, Active: "kept-blank-blank" } && blankTaken.Count == 2 && blankFrontId is null,
            "(fix round 3) a kept tabs.json whose tab in front was blank (never kept, so not taken): both tabs taken in, none brought forward (the start-up tab stays in front), never the one used last (u2)");
        fifth.ForgetSession();

        // And an older session kept with a tab in front as well: the session that wrote last has the word, and it named none.
        var bothKept = Fresh("project-tabs-restored-front-blank-newer");
        var keptOld = Left(bothKept, "kept-old", true, ("v1", 2), ("v2", 1));
        foreach (var id in new[] { "v1", "v2" })
            File.SetLastWriteTimeUtc(keptOld.StateFile(id), DateTime.UtcNow.AddHours(-2));
        KeptWithBlankInFront(bothKept, "kept-new", "w1");
        var sixth = new ProjectTabs(bothKept, "start-both", "sixth");
        expect(sixth.TakeLeftBehind([sixth.Active.Id], out var bothFront).Count == 3 && bothFront is null,
            "(fix round 3) with an older kept session's tab in front (v1) as well: the newer session, blank in front, has the word: none is brought forward");
        sixth.ForgetSession();

        // The window's side: after the first frame, only once the tabs are taken in, through the switch's own steps, with no layout laid out.
        var tabsSource = Source("MainWindow.ProjectTabs.cs");
        var exit = Source("MainWindow.TabExit.cs");
        var recover = MemberOf(tabsSource, "private async void RecoverProjectTabs(");
        expect(MemberOf(tabsSource, "private void InitializeProjectTabs(").Contains("ContentRendered += (_, _) => Dispatcher.BeginInvoke(RecoverProjectTabs, DispatcherPriority.ApplicationIdle);", StringComparison.Ordinal)
               && Ordered(recover, "_tabs.TakeLeftBehind(known, out var front)", "adopted.Add(tab.Id);", "if (adopted.Count == 0)", "RestoredFrontToBring(leftFront, adopted, known)",
                   "await BringRestoredTabForwardAsync(forward, Show)", "Show();", "WriteTabManifest();", "RestoredLine(broughtForward ? forward!.Title : null, adopted.Count)"),
            "Law 1: at idle after the first frame (the blank Timeline, the strip collapsed), and only once the tabs are taken in; the strip shown once, tabs.json written, the status line set");
        var bring = MemberOf(exit, "private async Task<bool> BringRestoredTabForwardAsync(");
        expect(Ordered(bring, "_switchingTab = true;", "PutActiveBehind(out var write);", "SwapStartTabFor(_tabs, blank, front);", "shown();", "await ResumeTabAsync(front, null);",
                   "RefreshActiveTab();", "finally", "PutStartTabBack(_tabs, at);", "AppLog.Write(", "_switchingTab = false;"),
            "through a switch's own steps: the blank tab put behind and gone, the tab shown in front (the slide comes out from it), then resumed from its cache file; a failure puts a blank tab back in front and is logged");
        expect(bring.Length > 0 && recover.Length > 0 && !bring.Contains("ApplyTabLayout", StringComparison.Ordinal) && !bring.Contains("_dock.Load", StringComparison.Ordinal)
               && !recover.Contains("ApplyTabLayout", StringComparison.Ordinal),
            "Law 1: no layout laid out for it (the first frame's single dock build stays the only one): it takes the window as it is");
        expect(Ordered(MemberOf(exit, "private ProjectTabHeader? RestoredFrontToBring("), "known.Count == 1", "_tabs.Count == adopted.Count + 1", "_switchingTab", "_openingName is not null",
                   "_viewModel.IsBusy", "_viewModel.HoldsNothingForTab", "_viewModel.CurrentProjectPath", "_tabsRevealed", "SingleInstance.LaunchFile() is not null"),
            "the start-up tab read as it is now: the only other tab and in front, nothing under way, holding nothing, no project file, not shown on purpose, no project to open");
        expect(MainWindow.RestoredLine("Wedding", 3) == "Wedding is open again: 3 project tabs left open when HandPeg last closed came back."
               && MainWindow.RestoredLine("Wedding", 1) == "Wedding is open again: the project tab left open when HandPeg last closed came back."
               && MainWindow.RestoredLine(null, 2) == "2 project tabs left open when HandPeg last closed are back in the title bar: click one to open it.",
            "the status line: which project is open and how many came back (as before when none was brought forward)");

        // (final review, AD-1) A tab with unsaved changes brought back keeps its files as the tab in front's crash-recovery copy: the 20 s tick
        // leaves them while its history has not moved, writes them anew once it moves, and lets them go once the tab is clean.
        DirtyResumeKeepsItsFile(expect);
        expect(Ordered(MemberOf(tabsSource, "private async Task<bool> ResumeTabAsync("), "_unresumed.Remove(id);", "if (tab.Dirty)",
                   "_frontRecovery = new FrontLook(id, _viewModel.HistoryPosition, Written: true);", "else", "DeleteTabFiles(id);", "if (tab.ExplodedOpen)")
               && Ordered(MemberOf(Source("MainWindow.TabCache.cs"), "private bool AttachHydrated("), "ForgetUnwritten(live.Write);", "if (live.Write?.Dirty == true)",
                   "_frontRecovery = new FrontLook(tab.Id, _viewModel.HistoryPosition, Written: true);", "else", "DeleteTabFiles(tab.Id);"),
            "(final review) every resume keeps the files of a tab with unsaved changes (from its cache: AD-1, a click, Ctrl+Tab; from memory once its file was written whole), a clean tab's still go");

        // (final review) The tab brought forward without its layout keeps its own as it goes behind, until it is laid out, the user changes the window's, or it closes.
        var capture = MemberOf(tabsSource, "private void CaptureWindowStateForTab(");
        expect(Ordered(capture, "if (_unresumed.Contains(tab.Id))", "return;", "if (!LayoutNotTaken(tab))", "tab.LayoutJson = TabLayoutOf(_dock, perTab);",
                   "tab.MediaView = TabMediaViewOf(MediaBrowser.ViewMode, perTab);", "TakeExplodedState(")
               && Ordered(bring, "await ResumeTabAsync(front, null);", "if (resumed)", "RefreshActiveTab();", "KeepLayoutNotTaken(front);", "finally")
               && Ordered(MemberOf(exit, "private void KeepLayoutNotTaken("), "AppSettings.Current.PerTabWindowLayouts", "tab.LayoutJson is not { Length: > 0 }", "_layoutNotTaken = tab.Id;",
                   "_dock.Changed += () => _layoutNotTaken = null;")
               && Ordered(MemberOf(tabsSource, "private void ApplyTabLayout("), "LoadTabLayout(_dock, tab, perTab)", "ForgetLayoutNotTaken(tab.Id);")
               && MemberOf(Source("MainWindow.TabCache.cs"), "private void ForgetKeptTab(").Contains("ForgetLayoutNotTaken(id);", StringComparison.Ordinal),
            "(final review) the tab brought forward (with a layout of its own) keeps it as it goes behind (the start-up layout never taken for it); a switch back lays it out, and a change by hand (DockManager.Changed) or its close lets it go");
        var window = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        var notTaken =(Func<ProjectTabHeader, bool>)Delegate.CreateDelegate(typeof(Func<ProjectTabHeader, bool>), window, typeof(MainWindow).GetMethod("LayoutNotTaken", Hidden)!);
        var forget = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), window, typeof(MainWindow).GetMethod("ForgetLayoutNotTaken", Hidden)!);
        typeof(MainWindow).GetField("_layoutNotTaken", Hidden)!.SetValue(window, "beta");
        var (betaTab, gammaTab) = (new ProjectTabHeader { Id = "beta" }, new ProjectTabHeader { Id = "gamma" });
        var heldForBeta = notTaken(betaTab) && !notTaken(gammaTab);
        forget("gamma");
        var keptOnOther = notTaken(betaTab);
        forget("beta");
        expect(heldForBeta && keptOnOther && !notTaken(betaTab), "(final review) only that tab keeps its layout, and only until it is let go (another tab's layout laid out or closed changes nothing)");

        // (final review) No tab is taken in while a close is asking (its idle callback runs in the dialog's loop too): they stay for the next start,
        // or are taken in once the close is called off; and should a close begin all the same, the files moved in are no tab's and stay.
        expect(Ordered(recover, "if (!_tabGesturesSet)", "if (_closeAsked || _recoveringTabs)", "_leftTabsPutOff |= _closeAsked;", "return;", "_recoveringTabs = true;",
                   "_tabs.TakeLeftBehind(known, out var front)", "if (!_closeAsked && _tabs.Adopt(tab))", "adopted.Add(tab.Id);")
               && Ordered(MemberOf(Source("MainWindow.xaml.cs"), "private void MainWindow_Closing("), "_closeAsked = true;", "finally", "_closeAsked = false;", "if (!close)",
                   "TakeInLeftTabsPutOff();", "return;")
               && Ordered(MemberOf(exit, "private void TakeInLeftTabsPutOff("), "if (!_leftTabsPutOff)", "_leftTabsPutOff = false;",
                   "Dispatcher.BeginInvoke(RecoverProjectTabs, System.Windows.Threading.DispatcherPriority.ApplicationIdle);"),
            "(final review) RecoverProjectTabs takes nothing in while a close is asking (put off, run again once the close is called off) and adopts nothing should one begin meanwhile");
        var asking = Fresh("project-tabs-restored-front-closing");
        Left(asking, "left", true, ("l1", 1), ("l2", 2));
        var closing = new ProjectTabs(asking, "start-closing", "closing");
        var movedIn = closing.TakeLeftBehind([closing.Active.Id], out _);
        closing.ForgetSession();
        var later = new ProjectTabs(asking, "start-later", "later");
        expect(movedIn.Count == 2 && movedIn.All(t => File.Exists(closing.StateFile(t.Id))) && later.TakeLeftBehind([later.Active.Id]).Count == 2,
            "(final review) tabs moved in but never adopted (a close begun meanwhile): the Forget plan's ForgetSession keeps their files, and the next start takes them in");
        later.ForgetSession();

        // (fix round 3) The same on the window itself: RecoverProjectTabs run while a close is asking, then the close called off.
        var putOff = Fresh("project-tabs-restored-front-put-off");
        var fell = Left(putOff, "fell", false, ("f1", 1), ("f2", 2));
        PutOffWhileClosing(putOff, fell.FolderPath, expect);
    }

    /// <summary>
    /// RecoverProjectTabs on the window itself while a close is asking (fix round 3), as far as it reaches (none made, nothing shown: its tabs, a view
    /// model, a strip never on screen): it puts the tabs a dead session left off and touches nothing; the close called off (TakeInLeftTabsPutOff, as
    /// MainWindow_Closing calls it once its _closeAsked is let go) runs it again at idle, and it takes them in behind the tab in front.
    /// </summary>
    private static void PutOffWhileClosing(string root, string deadFolder, Action<bool, string> expect)
    {
        var tabs = new ProjectTabs(root, "start-put-off", "put-off");
        var vm = Holding(0);
        var window = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Hidden)!.SetValue(window, value);
        bool Get(string name) => (bool)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!;

        // The window's thread's dispatcher (a window made the usual way has it from its constructor), its tabs, a view model, a strip, the
        // paused still's timer taken as made (showing the strip starts none), the strip's keys set, and a close asking.
        typeof(DispatcherObject).GetField("_dispatcher", Hidden)!.SetValue(window, Dispatcher.CurrentDispatcher);
        Set("_tabs", tabs);
        Set("_viewModel", vm);
        Set("_unresumed", new HashSet<string>(StringComparer.Ordinal));
        Set("ProjectTabStrip", new ProjectTabStrip { Visibility = Visibility.Visible });
        Set("_stillSettle", new DispatcherTimer());
        Set("_tabGesturesSet", true);
        Set("_closeAsked", true);
        string[] Files() => Directory.Exists(deadFolder) ? [.. Directory.EnumerateFiles(deadFolder).Order(StringComparer.Ordinal)] : [];
        var before = Files();
        typeof(MainWindow).GetMethod("RecoverProjectTabs", Hidden)!.Invoke(window, null);
        expect(Get("_leftTabsPutOff") && tabs.Count == 1 && before.Length > 0 && Files().SequenceEqual(before),
            "(fix round 3) on the window: RecoverProjectTabs while a close is asking puts the tabs off (_leftTabsPutOff), the dead session's files stay where they are, and no tab is added");

        // The close called off: its finally lets _closeAsked go, then MainWindow_Closing queues the tabs put off again.
        Set("_closeAsked", false);
        typeof(MainWindow).GetMethod("TakeInLeftTabsPutOff", Hidden)!.Invoke(window, null);
        var queued = !Get("_leftTabsPutOff");
        expect(queued && Idle(() => tabs.Count == 3) && tabs.Active.Id == "start-put-off" && tabs.Find("f1") is not null && tabs.Find("f2") is not null
               && File.Exists(tabs.StateFile("f1")) && File.Exists(tabs.StateFile("f2")) && !File.Exists(Path.Combine(deadFolder, Path.GetFileName(tabs.StateFile("f1"))))
               && vm.StatusText == MainWindow.RestoredLine(null, 2),
            "(fix round 3) the close called off (TakeInLeftTabsPutOff): run again at idle, it takes them in behind the tab in front, their files moved into this session's folder, and says so");
        tabs.WaitFor(null, 10_000);
        tabs.ForgetSession();
    }

    /// <summary>
    /// The tab in front brought back with unsaved changes, its cache file kept and the tick's last look set as ResumeTabAsync leaves them (final
    /// review, AD-1), on the window as far as KeepFrontRecovery reaches (none made, nothing shown): the file stays while the history has not
    /// moved, and goes once the tab is clean.
    /// </summary>
    private static void DirtyResumeKeepsItsFile(Action<bool, string> expect)
    {
        var tabs = new ProjectTabs(Fresh("project-tabs-restored-front-dirty"), "front", "dirty");
        var vm = Holding(2);
        var front = tabs.Active;
        (front.Title, front.Dirty, front.Empty) = ("Front", true, false);
        tabs.WriteTab(front.Id, vm.CaptureStateForTab("Front"), vm.SavedFingerprintForTab, ProjectTabs.HeaderJson(front));
        var window = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Hidden)!.SetValue(window, value);
        Set("_tabs", tabs);
        Set("_viewModel", vm);
        Set("_unresumed", new HashSet<string>(StringComparer.Ordinal));
        Set("_frontRecovery", Activator.CreateInstance(typeof(MainWindow).GetNestedType("FrontLook", BindingFlags.NonPublic)!, front.Id, vm.HistoryPosition, true));
        void Tick() => typeof(MainWindow).GetMethod("KeepFrontRecovery", Hidden)!.Invoke(window, null);
        Tick();
        expect(vm.HasUnsavedChanges && tabs.WaitFor(front.Id, 10_000) && tabs.HasState(front.Id) && File.Exists(tabs.SavedFile(front.Id)),
            "(final review) brought back with unsaved changes: its file and its saved state's fingerprint stay, and the tick leaves them while its history has not moved");
        expect(MainWindow.FrontRecoveryFor(false, written: true, dirty: true, moved: false) == MainWindow.FrontRecoveryStep.None
               && MainWindow.FrontRecoveryFor(false, written: true, dirty: true, moved: true) == MainWindow.FrontRecoveryStep.Write
               && MainWindow.FrontRecoveryFor(false, written: true, dirty: false, moved: true) == MainWindow.FrontRecoveryStep.Delete,
            "(final review) the tick's table: nothing while unmoved, written anew once the history moves, let go once clean");
        vm.Undo();
        vm.Undo();
        Tick();
        expect(!vm.HasUnsavedChanges && tabs.WaitFor(front.Id, 10_000) && !tabs.HasState(front.Id) && !File.Exists(tabs.HeaderFile(front.Id)),
            "(final review) undone back to the save: the tick lets its file go");
        tabs.ForgetSession();
    }

    // ----- tabs-kept-file-stamp -----

    private static void KeptFileStamp(Action<bool, string> expect)
    {
        // The rule (pure).
        expect(ProjectTabs.FileChangedSince(100, 10, 200, 10) && ProjectTabs.FileChangedSince(100, 10, 100, 11),
            "a stamp kept and the file there with another last write or length: changed, it opens from its file");
        expect(!ProjectTabs.FileChangedSince(100, 10, 100, 10), "the same last write and length: unchanged, it opens from its cache");
        expect(!ProjectTabs.FileChangedSince(0, 0, 200, 10), "no stamp kept: no change seen (a clean tab with none reads its file all the same: ReadForResume, below)");
        expect(!ProjectTabs.FileChangedSince(100, 10, 0, 0), "its project file not there any more: its cache");

        // (final review, C-4a and C-4c) A header is written with the stamp it carries, never one looked up on the disk as it is written.
        var root = Fresh("tabs-kept-file-stamp");
        Directory.CreateDirectory(root);
        static string StampOf(string json) => ProjectTabs.ReadHeader(ProjectTabs.Stamped(json)) is { } read ? $"{read.ProjectFileTicks}/{read.ProjectFileLength}" : "none";
        var carried = new ProjectTabHeader { Id = "c", Title = "Carried", ProjectPath = Path.Combine(root, "Not There.hproj"), Empty = false, ProjectFileTicks = 5, ProjectFileLength = 7 };
        var keptAsCarried = StampOf(ProjectTabs.HeaderJson(carried));
        carried.Dirty = true;
        var dirtyNone = StampOf(ProjectTabs.HeaderJson(carried));
        (carried.Dirty, carried.ProjectPath) = (false, "");
        expect(keptAsCarried == "5/7", "(final review) a clean tab's header keeps the stamp it carries, even for a file not there: nothing is looked up as it is written");
        expect(dirtyNone == "0/0" && StampOf(ProjectTabs.HeaderJson(carried)) == "0/0"
               && StampOf(ProjectTabs.HeaderJson(new ProjectTabHeader { Id = "e", ProjectPath = "D:/projects/Empty.hproj", ProjectFileTicks = 5, ProjectFileLength = 7 })) == "0/0",
            "(final review) none for a tab with unsaved changes, one with no project file, or an empty one");
        var service = Source("Services", "ProjectTabs.cs");
        var stamped = MemberOf(service, "public static string Stamped(");
        expect(stamped.Length > 0 && !new[] { "FileStamp(", "FileInfo", "File.", "Directory." }.Any(call => stamped.Contains(call, StringComparison.Ordinal))
               && Ordered(MemberOf(service, "public void WriteTab("), "ProjectStore.WriteWhole(HeaderFile(id), Stamped(headerJson));")
               && Ordered(MemberOf(service, "public void WriteHeader("), "ProjectStore.WriteWhole(HeaderFile(id), Stamped(headerJson));"),
            "(final review) every header written for a tab (put behind, kept for the next start, renamed) goes through Stamped, which makes no file call: the close's waited write never touches a share");

        // A tab kept with nothing unsaved, its stamp taken as its project was opened (09:00); its header written with it: it comes back from its cache.
        var tabs = new ProjectTabs(Path.Combine(root, "cache"), "start", "kept");
        var (kept, dirty, renamed, unstamped) = (tabs.Add("Kept", "kept"), tabs.Add("Dirty", "dirty"), tabs.Add("Renamed", "renamed"), tabs.Add("Unstamped", "unstamped"));
        var saved = Holding(2);
        ProjectTabHeader Kept(ProjectTabHeader tab, string name)
        {
            var path = Path.Combine(root, name + ".hproj");
            ProjectStore.SaveTo(saved.CaptureStateForSave(name), path);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc));
            (tab.ProjectPath, tab.Dirty, tab.Empty) = (path, false, false);
            (tab.ProjectFileTicks, tab.ProjectFileLength) = ProjectTabs.FileStamp(path);
            tabs.WriteTab(tab.Id, saved.CaptureStateForTab(name), null, ProjectTabs.HeaderJson(tab));
            return tab;
        }

        var projectPath = Kept(kept, "Kept").ProjectPath;
        var stamp = ProjectTabs.FileStamp(projectPath);
        expect(stamp.Ticks != 0 && ProjectTabs.ReadHeader(File.ReadAllText(tabs.HeaderFile(kept.Id))) is { } header
               && header.ProjectFileTicks == stamp.Ticks && header.ProjectFileLength == stamp.Length,
            "a clean tab with a project file is kept with the stamp it carries (its file's last write and length as it was opened) in its header");
        (dirty.ProjectPath, dirty.Dirty, dirty.Empty, dirty.ProjectFileTicks, dirty.ProjectFileLength) = (projectPath, true, false, stamp.Ticks, stamp.Length);
        tabs.WriteTab(dirty.Id, saved.CaptureStateForTab("Dirty"), saved.SavedFingerprintForTab, ProjectTabs.HeaderJson(dirty));
        expect(ProjectTabs.ReadHeader(File.ReadAllText(tabs.HeaderFile(dirty.Id))) is { ProjectFileTicks: 0, ProjectFileLength: 0 },
            "a tab with unsaved changes keeps no stamp in its file (whatever it carries)");
        var same = tabs.ReadForResume(kept.Id, projectPath);
        expect(same.From == TabResumeSource.Cache && Shape(same.State.Timeline) == Shape(saved.Document), "its file unchanged since: it comes back from its cache");

        // (final review, C-4a) The file saved over from another PC at 10:00, AFTER the open and BEFORE the close's clean write (SuspendAll): that
        // write keeps the 09:00 stamp, so the next start reads the file, never the stale cache that Ctrl+S would write over it.
        var newer = Holding(4);
        ProjectStore.SaveTo(newer.CaptureStateForSave("Kept"), projectPath);
        File.SetLastWriteTimeUtc(projectPath, new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc));
        tabs.WriteTab(kept.Id, saved.CaptureStateForTab("Kept"), null, ProjectTabs.HeaderJson(kept));
        var changed = tabs.ReadForResume(kept.Id, projectPath);
        expect(ProjectTabs.ReadHeader(File.ReadAllText(tabs.HeaderFile(kept.Id))) is { } rewritten && rewritten.ProjectFileTicks == stamp.Ticks
               && Shape(newer.Document) != Shape(saved.Document) && changed.From == TabResumeSource.ChangedFile && changed.Saved is null
               && Shape(changed.State.Timeline) == Shape(newer.Document) && (changed.FileTicks, changed.FileLength) == ProjectTabs.FileStamp(projectPath),
            "(final review) its file saved over after the open, before the clean close's write: that write keeps the open's stamp, and it opens from the file (taken as saved, its stamp the one read)");

        // (final review, C-4a) A clean tab behind renamed after its file changed: the rename's header keeps the open's stamp too.
        var renamedPath = Kept(renamed, "Renamed").ProjectPath;
        ProjectStore.SaveTo(newer.CaptureStateForSave("Renamed"), renamedPath);
        File.SetLastWriteTimeUtc(renamedPath, new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc));
        var target = Path.Combine(root, "Renamed Again.hproj");
        File.Move(renamedPath, target);
        (renamed.ProjectPath, renamed.Title) = (target, "Renamed Again");
        tabs.WriteHeader(renamed.Id, ProjectTabs.HeaderJson(renamed));
        var afterRename = tabs.ReadForResume(renamed.Id, target);
        expect(afterRename.From == TabResumeSource.ChangedFile && Shape(afterRename.State.Timeline) == Shape(newer.Document),
            "(final review) a clean tab behind renamed after its file changed: it opens from the file, not from the stale cache");

        // (final review) A clean tab carrying no stamp (none taken, a header from before) cannot tell: it reads its file as before 5.5A, saying nothing.
        Kept(unstamped, "Unstamped");
        (unstamped.ProjectFileTicks, unstamped.ProjectFileLength) = (0L, 0L);
        tabs.WriteTab(unstamped.Id, saved.CaptureStateForTab("Unstamped"), null, ProjectTabs.HeaderJson(unstamped));
        var noStamp = tabs.ReadForResume(unstamped.Id, unstamped.ProjectPath);
        expect(noStamp.From == TabResumeSource.File && noStamp.FileTicks == ProjectTabs.FileStamp(unstamped.ProjectPath).Ticks,
            "(final review) a clean tab with no stamp: read from its file (as before 5.5A; nothing said), with the stamp that read took");
        expect(tabs.ReadForResume(kept.Id, null).From == TabResumeSource.Cache && tabs.ReadForResume(dirty.Id, projectPath).From == TabResumeSource.Cache,
            "a tab with unsaved changes always comes back from its cache (unsaved work wins), even asked as a clean one");

        // (final review, C-4b) A changed file that cannot be read: a newer format (named as such), or a damaged one: the cache, tied to no file, with why.
        var future = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
        future["version"] = ProjectFile.CurrentVersion + 1;
        future["writtenBy"] = "HandPeg 9.9.9";
        File.WriteAllText(projectPath, future.ToJsonString());
        File.SetLastWriteTimeUtc(projectPath, new DateTime(2026, 11, 13, 11, 0, 0, DateTimeKind.Utc));
        var newerFormat = tabs.ReadForResume(kept.Id, projectPath);
        File.WriteAllText(projectPath, "{ \"format\": \"handpeg-project\", half written");
        File.SetLastWriteTimeUtc(projectPath, new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));
        var damaged = tabs.ReadForResume(kept.Id, projectPath);
        expect(newerFormat is { From: TabResumeSource.CacheFileUnreadable, Saved: null, FileTicks: 0, Why: "saved in a newer project format by HandPeg 9.9.9" }
               && Shape(newerFormat.State.Timeline) == Shape(saved.Document),
            "(final review) its file saved since in a newer format: the cache (no saved state: it comes back unsaved), with why (\"saved in a newer project format by HandPeg 9.9.9\")");
        expect(damaged is { From: TabResumeSource.CacheFileUnreadable, Why.Length: > 0 } && Shape(damaged.State.Timeline) == Shape(saved.Document),
            "(final review) its file changed and cannot be read (damaged, locked): the cache, with what the read said");
        expect(MainWindow.UnreadableOnDiskLine("Kept", "saved in a newer project format by HandPeg 9.9.9")
               == "Kept: its project file changed on the disk but could not be read (saved in a newer project format by HandPeg 9.9.9); opened from the copy kept with its tab, unsaved and tied to no file: Save asks for a name, and that file is left as it is.",
            "(final review) the status line names the project and why, and says Save asks for a name");
        File.Delete(projectPath);
        var missing = tabs.ReadForResume(kept.Id, projectPath);
        expect(missing.From == TabResumeSource.Cache && Shape(missing.State.Timeline) == Shape(saved.Document), "its file gone: it comes back from its cache");
        tabs.ForgetSession();

        // The window: a cold tab coming back reads its file or its cache on its queue, then opens it as a project is, and says which.
        var tabsSource = Source("MainWindow.ProjectTabs.cs");
        var resume = MemberOf(tabsSource, "private async Task<bool> ResumeTabAsync(");
        expect(Ordered(resume, "if (!tab.Dirty && tab.ProjectPath is { Length: > 0 } clean)",
                   "read = await _tabs.Enqueue(id, () => _tabs.ReadForResume(id, clean));", "else",
                   "(state, saved) = await _tabs.Enqueue(id, () => (_tabs.ReadState(id), _tabs.ReadSaved(id)));", "var unreadable = read.From == TabResumeSource.CacheFileUnreadable;",
                   "var (projectPath, dirty) = unreadable ? (\"\", true) : (tab.ProjectPath, tab.Dirty);", "await _viewModel.ResumeTabAsync(state, projectPath, dirty, saved,",
                   "if (unreadable)", "(tab.ProjectPath, tab.Dirty) = (\"\", true);", "DeleteTabFiles(id);", "if (read.From != TabResumeSource.Cache)",
                   "(tab.ProjectFileTicks, tab.ProjectFileLength) = (read.FileTicks, read.FileLength);", "if (read.From == TabResumeSource.ChangedFile)", "ChangedOnDiskLine(tab.Title)",
                   "else if (unreadable)", "UnreadableOnDiskLine(tab.Title, read.Why ?? \"\")")
               && MainWindow.ChangedOnDiskLine("Kept") == "Kept: opened from its project file, which changed on the disk since its tab was kept.",
            "a tab brought back: only a clean one with a project file is looked at (on the tab's queue), opened as a project is, its stamp the one read (one read from its cache keeps its own); (final review) one whose changed file could not be read opens unsaved and tied to no file, and says why");
        expect(ProjectTabs.HeaderJson(new ProjectTabHeader { Id = "x" }) is var plain && !plain.Contains("StepsBack", StringComparison.Ordinal) && !plain.Contains("ProjectFile", StringComparison.Ordinal)
               && ProjectTabs.ReadHeader(ProjectTabs.HeaderJson(new ProjectTabHeader { Id = "x", StepsBack = 2, ProjectFileTicks = 5, ProjectFileLength = 7 })) is { StepsBack: 2, ProjectFileTicks: 5, ProjectFileLength: 7 },
            "the new header fields are left out of the file while 0 (a header stays as small as before) and read back when set");

        // (final review, C-4a) The window takes the stamp as the project and its file are known to be the same: a save of the tab in front, an open into
        // it, a tab behind saved from its cache; a stamp for another file than the tab's by then dropped. (Fix round 3: an open's taken before its read,
        // a save's off the tab's queue, and only the last asked lands.)
        WindowTakesStamps(root, expect);
        var cache = Source("MainWindow.TabCache.cs");
        var stampAsk = MemberOf(cache, "private async Task StampProjectFileAsync(");
        expect(Ordered(MemberOf(cache, "private void ViewModel_ProjectSaved("), "DropFrontRecovery(_tabs.Active);", "StampProjectFileAsync(_tabs.Active, _viewModel.CurrentProjectPath)")
               && Ordered(MemberOf(tabsSource, "internal async Task OpenProjectInTabAsync("), "var stamp = await Task.Run(() => ProjectTabs.FileStamp(path));",
                   "if (_closeAsked || _windowClosing is { IsCancellationRequested: true })", "if (SameProjectFile(_viewModel.CurrentProjectPath, path))", "var (into, opening) = (_tabs.Active, _viewModel.CurrentProjectPath);", "var asked = ++into.StampsAsked;",
                   "await _viewModel.LoadProjectAsync(path);", "StampOpenedProject(into, opening, path, stamp, asked);", "var asked = ++made.StampsAsked;",
                   "await _viewModel.LoadProjectAsync(path);", "StampOpenedProject(made, \"\", path, stamp, asked);")
               && Ordered(MemberOf(tabsSource, "private string SaveTabInPlace("), "ProjectStore.SaveTo(state, path);", "(tab.ProjectPath, tab.Dirty, tab.NewProjectName, tab.NewProjectFolder) = (path, false, \"\", null);",
                   "_ = StampProjectFileAsync(tab, path);")
               && Ordered(stampAsk, "(tab.ProjectFileTicks, tab.ProjectFileLength) = (0L, 0L);", "var asked = ++tab.StampsAsked;",
                   "await Task.Run(() => ProjectTabs.FileStamp(path));", "asked != tab.StampsAsked", "!string.Equals(now, path, StringComparison.OrdinalIgnoreCase)", "WriteTabHeader(tab);")
               && !stampAsk.Contains("_tabs.Enqueue", StringComparison.Ordinal)
               && Ordered(MemberOf(tabsSource, "private async Task<bool> ResumeTabAsync("), "tab.StampsAsked++;", "(tab.ProjectFileTicks, tab.ProjectFileLength) = (read.FileTicks, read.FileLength);")
               && !ProjectTabs.HeaderJson(new ProjectTabHeader { Id = "x", StampsAsked = 3 }).Contains("StampsAsked", StringComparison.Ordinal)
               && MemberOf(tabsSource, "private void RefreshActiveTab(").Contains("TakeProjectPath(tab, _viewModel.CurrentProjectPath);", StringComparison.Ordinal)
               && MemberOf(tabsSource, "private void TakeHeaderForTab(").Contains("TakeProjectPath(tab, _viewModel.CurrentProjectPath);", StringComparison.Ordinal),
            "(final review) wired: every save of the tab in front, the two opens into a tab, and a save of a tab behind take the stamp; the tab in front's header follows its file (another file's stamp let go); (fix round 3) an open's stamp taken before its read, a save's off the tab's queue, only the last asked landing (a tab read back drops one on its way), the count never written");
    }

    /// <summary>
    /// The window's side of the stamp (final review, C-4a), on the window as far as these paths reach (none made, nothing shown): its tabs, its view
    /// model, the tabs not brought back. A save stamps the tab in front off its queue (fix round 3); a change after it is not taken; an open stamps it
    /// with the stamp taken before its read (<see cref="WindowOpensStamped"/>); a stamp for another file than the tab's by then, or no longer the last
    /// asked, is dropped; the tab's file changing lets the stamp go; a tab behind saved has its header written with it.
    /// </summary>
    private static void WindowTakesStamps(string root, Action<bool, string> expect)
    {
        var tabs = new ProjectTabs(Path.Combine(root, "window-cache"), "front", "stamping");
        var vm = Holding(1);
        var window = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Hidden)!.SetValue(window, value);
        Set("_tabs", tabs);
        Set("_viewModel", vm);
        Set("_unresumed", new HashSet<string>(StringComparer.Ordinal));
        var front = tabs.Active;
        (front.Title, front.Empty) = ("Front", false);
        var onSaved = typeof(MainWindow).GetMethod("ViewModel_ProjectSaved", Hidden)!;
        void Saved() => onSaved.Invoke(window, null);
        Task Stamp(ProjectTabHeader tab, string path) => (Task)typeof(MainWindow).GetMethod("StampProjectFileAsync", Hidden)!.Invoke(window, [tab, path])!;
        bool Until(Func<bool> done)
        {
            var until = Environment.TickCount64 + 10_000;
            while (!done() && Environment.TickCount64 < until)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
                Thread.Sleep(5);
            }

            return done();
        }

        // Ctrl+S while a write of the tab is under way on its queue (a recovery copy): the stamp of the file just written, taken off that queue
        // (fix round 3), so it lands before that write is done, never waiting behind it (a wait in which the file could change).
        var path = Path.Combine(root, "Front.hproj");
        using var gate = new ManualResetEventSlim();
        var held = tabs.Enqueue(front.Id, () => gate.Wait(10_000));
        vm.ProjectSaved += Saved;
        var line = vm.SaveProjectTo(path);
        vm.ProjectSaved -= Saved;
        var written = ProjectTabs.FileStamp(path);
        var landedFirst = Until(() => front.ProjectFileTicks != 0) && !held.IsCompleted;
        gate.Set();
        expect(line.StartsWith("Project saved", StringComparison.Ordinal) && landedFirst && tabs.WaitFor(front.Id, 10_000)
               && (front.ProjectFileTicks, front.ProjectFileLength) == written && front.ProjectPath == path,
            "(fix round 3) a save of the tab in front: it carries the stamp of the file just written, taken off its queue (it lands while a write of the tab is still under way there), its header's file the saved one");

        // Saved over from another PC afterwards: nothing takes that stamp (the header written as it closes keeps the save's).
        File.SetLastWriteTimeUtc(path, new DateTime(written.Ticks, DateTimeKind.Utc).AddMinutes(5));
        expect(tabs.WaitFor(front.Id, 10_000) && (front.ProjectFileTicks, front.ProjectFileLength) == written,
            "(final review) the file changed afterwards: the stamp is still the save's (it is never taken again as the header is written)");

        // A project opened into the tab (fix round 3): through OpenProjectInTabAsync itself, on a window of its own.
        WindowOpensStamped(root, expect);

        // A stamp asked for a file that is not the tab's by then: dropped (none carried).
        var other = Stamp(front, Path.Combine(root, "Other.hproj"));
        expect(Until(() => other.IsCompleted) && front is { ProjectFileTicks: 0, ProjectFileLength: 0 } && front.ProjectPath == path,
            "(final review) a stamp for another file than the tab's by then is dropped: the tab carries none (it reads its file as it comes back)");

        // Two asked for the tab, the first landing last (taken off its queue, they can): the one no longer the last asked never lands.
        var stale = Stamp(front, path);
        front.StampsAsked++;
        expect(Until(() => stale.IsCompleted) && front is { ProjectFileTicks: 0, ProjectFileLength: 0 } && front.ProjectPath == path,
            "(fix round 3) a stamp no longer the last asked for the tab (another asked since: a save, an open, the tab read back) never lands");

        // The tab's file another one (a New Project, a Save As, an open not stamped): the stamp it carried goes.
        var take = typeof(MainWindow).GetMethod("TakeProjectPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        (front.ProjectFileTicks, front.ProjectFileLength) = (5, 7);
        take.Invoke(null, [front, path]);
        var keptSame = (front.ProjectFileTicks, front.ProjectFileLength) == (5L, 7L);
        take.Invoke(null, [front, ""]);
        expect(keptSame && front is { ProjectPath: "", ProjectFileTicks: 0, ProjectFileLength: 0 },
            "(final review) the tab in front's file followed: kept for the same file, let go for another (a stamp is only ever of the header's own file)");

        // A tab behind saved from its cache (SaveTabInPlace): stamped, its header file written with the stamp.
        var behind = tabs.Add("Behind", "behind");
        var behindPath = Path.Combine(root, "Behind.hproj");
        ProjectStore.SaveTo(vm.CaptureStateForSave("Behind"), behindPath);
        (behind.ProjectPath, behind.Dirty, behind.Empty) = (behindPath, false, false);
        tabs.WriteTab(behind.Id, vm.CaptureStateForTab("Behind"), null, ProjectTabs.HeaderJson(behind));
        var behindStamp = Stamp(behind, behindPath);
        expect(Until(() => behindStamp.IsCompleted) && tabs.WaitFor(behind.Id, 10_000) && (behind.ProjectFileTicks, behind.ProjectFileLength) == ProjectTabs.FileStamp(behindPath)
               && ProjectTabs.ReadHeader(File.ReadAllText(tabs.HeaderFile(behind.Id))) is { } behindHeader && behindHeader.ProjectFileTicks == behind.ProjectFileTicks,
            "(final review) a tab behind saved from its cache: stamped, and its header file written again with the stamp");
        tabs.ForgetSession();
    }

    /// <summary>
    /// A project opened into the tab in front on the window itself (fix round 3, S-R1), through OpenProjectInTabAsync as File > Open runs it, as far
    /// as it reaches (none made, nothing shown: its tabs, a view model holding nothing, a strip never on screen). The stamp is the file's before the
    /// open read it: a save from another PC while the open is under way (after the read, as its media are looked at) leaves the tab an older stamp,
    /// so it reads its file as it comes back; an open that fails takes none.
    /// </summary>
    private static void WindowOpensStamped(string root, Action<bool, string> expect)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        (AppSettings.Current.AutoUpdateDependencies, AppSettings.Current.SendCrashReports) = (false, false);
        var tabs = new ProjectTabs(Path.Combine(root, "open-cache"), "opening", "open-stamp");
        var vm = new MainViewModel();
        var window = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Hidden)!.SetValue(window, value);
        Set("_tabs", tabs);
        Set("_viewModel", vm);
        Set("_unresumed", new HashSet<string>(StringComparer.Ordinal));
        Set("ProjectTabStrip", new ProjectTabStrip { Visibility = Visibility.Visible });
        Set("_stillSettle", new DispatcherTimer());
        var front = tabs.Active;
        Task Open(string file) => Dispatcher.CurrentDispatcher.InvokeAsync(() => window.OpenProjectInTabAsync(file)).Task.Unwrap();

        // A file that cannot be read: the open fails, and nothing is taken.
        var damaged = Path.Combine(root, "Damaged.hproj");
        File.WriteAllText(damaged, "{ \"format\": \"handpeg-project\", half written");
        var failed = Open(damaged);
        expect(Idle(() => failed.IsCompleted) && vm.CurrentProjectPath.Length == 0 && front is { ProjectPath: "", ProjectFileTicks: 0, ProjectFileLength: 0 },
            "(fix round 3) an open into the tab that fails (a damaged file) takes no stamp");

        // Pass 5.5A review: a close asking by the time the stamp is back (its prompt's loop runs the open's continuation): never opened into.
        var path = Path.Combine(root, "Opened.hproj");
        ProjectStore.SaveTo(Holding(1).CaptureStateForSave("Opened"), path);
        Set("_closeAsked", true);
        var whileClosing = Open(path);
        expect(Idle(() => whileClosing.IsCompleted) && vm.CurrentProjectPath.Length == 0 && front is { ProjectPath: "", ProjectFileTicks: 0 }
               && vm.StatusText.Contains("was not opened while HandPeg was closing", StringComparison.Ordinal),
            "(review) an open whose file stamp comes back while a close is asking opens nothing, and says so");
        Set("_closeAsked", false);

        // Saved over from another PC while the open is under way: after the open read it (the moment the view model names the file), before it is done.
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc));
        var beforeRead = ProjectTabs.FileStamp(path);
        var savedElsewhere = false;
        void SavedElsewhere(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.CurrentProjectPath) || vm.CurrentProjectPath != path)
                return;
            vm.PropertyChanged -= SavedElsewhere;
            File.AppendAllText(path, "\n");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 13, 9, 5, 0, DateTimeKind.Utc));
            savedElsewhere = true;
        }

        vm.PropertyChanged += SavedElsewhere;
        var opened = Open(path);
        var done = Idle(() => opened.IsCompleted);
        var now = ProjectTabs.FileStamp(path);
        expect(done && savedElsewhere && vm.CurrentProjectPath == path && front.ProjectPath == path && (front.ProjectFileTicks, front.ProjectFileLength) == beforeRead
               && ProjectTabs.FileChangedSince(front.ProjectFileTicks, front.ProjectFileLength, now.Ticks, now.Length),
            "(fix round 3) a project opened into the tab: its stamp is the file's before the open read it, so a save from another PC during the open (after the read) leaves it older: changed, the tab reads its file as it comes back, never the stale cache");
        tabs.ForgetSession();
    }

    /// <summary>The window's thread turned, its idle work too (ApplicationIdle runs before this SystemIdle stop), until <paramref name="done"/> (or 10 s have gone).</summary>
    private static bool Idle(Func<bool> done)
    {
        var until = Environment.TickCount64 + 10_000;
        while (!done() && Environment.TickCount64 < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }

        return done();
    }

    // ----- tabs-exit-dialog -----

    private static void ExitDialog(Action<bool, string> expect)
    {
        TabExitRow[] Rows() =>
        [
            new("a", "Alpha", "D:/projects/Alpha.hproj", false, "Alpha"),
            new("b", "Beta", "D:/projects/Beta.hproj", false, "Beta"),
            new("c", "Untitled Project", "Never saved", true, ""),
        ];

        var rows = Rows();
        var dialog = new SaveTabsOnExitDialog(rows, SaveTabsOnExitDialog.Purpose.Exit);
        expect(dialog is { Width: 640.0, SizeToContent: SizeToContent.Height, ResizeMode: ResizeMode.NoResize, ShowInTaskbar: false, WindowStartupLocation: WindowStartupLocation.CenterOwner }
               && dialog.Title == "Unsaved Project Tabs",
            "a 640 DIP dialog over HandPeg, as tall as its rows (they scroll past 320 DIPs), never in the taskbar");
        expect(rows.All(r => r.IsChecked) && ReferenceEquals(dialog.RowsList.ItemsSource, rows) && dialog.Rows.Count == 3, "a row for each unsaved tab, every one checked");
        expect(!rows[0].ShowsName && !rows[1].ShowsName && rows[2].ShowsName, "only the row never saved asks for a name");
        expect(!dialog.CanSave && !dialog.SaveButton.IsEnabled, "Save is off while the checked row never saved has no name");
        rows[2].Name = "Gamma";
        expect(dialog.CanSave && dialog.SaveButton.IsEnabled, "named: Save is on");
        rows[2].Name = "   ";
        expect(!dialog.SaveButton.IsEnabled, "a name of spaces is none");
        rows[2].IsChecked = false;
        expect(dialog.SaveButton.IsEnabled && !rows[2].ShowsName, "unchecked, it asks for no name and Save is on for the other two");
        rows[0].IsChecked = rows[1].IsChecked = false;
        expect(!dialog.SaveButton.IsEnabled, "with nothing checked there is nothing to save");
        rows[0].IsChecked = rows[1].IsChecked = rows[2].IsChecked = true;
        rows[2].Name = "Gamma";

        static string? Name(DependencyObject element) => AutomationProperties.GetName(element);
        expect((string)dialog.SaveButton.Content == "Save Checked & Close" && Name(dialog.SaveButton) == "Save Checked & Close" && dialog.SaveButton.IsDefault
               && (string)dialog.KeepButton.Content == "Keep Suspended for Next Launch" && Name(dialog.KeepButton) == "Keep Suspended for Next Launch"
               && (string)dialog.DiscardButton.Content == "Discard & Close" && Name(dialog.DiscardButton) == "Discard & Close"
               && (string)dialog.CancelButton.Content == "Cancel" && Name(dialog.CancelButton) == "Cancel" && dialog.CancelButton.IsCancel,
            "four buttons: Save Checked & Close (the default), Keep Suspended for Next Launch, Discard & Close, and Cancel (Esc)");

        // The four buttons fit the dialog's width.
        var buttons = (FrameworkElement)VisualTreeHelper.GetParent(dialog.SaveButton);
        buttons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        expect(buttons.DesiredSize.Width <= dialog.Width - 32, "they fit side by side inside its margins");

        expect(dialog.Choice == TabsExitChoice.Cancel, "unanswered (Esc, the title bar's close) is Cancel: HandPeg stays open");
        rows[2].Name = "";
        dialog.Choose(TabsExitChoice.SaveChecked);
        expect(dialog.Choice == TabsExitChoice.Cancel, "Save is never taken while it is off");
        rows[2].Name = "Gamma";
        dialog.Choose(TabsExitChoice.SaveChecked);
        var saved = dialog.Choice;
        dialog.Choose(TabsExitChoice.KeepSuspended);
        var kept = dialog.Choice;
        dialog.Choose(TabsExitChoice.Discard);
        var discarded = dialog.Choice;
        dialog.Choose(TabsExitChoice.Cancel);
        expect(saved == TabsExitChoice.SaveChecked && kept == TabsExitChoice.KeepSuspended && discarded == TabsExitChoice.Discard && dialog.Choice == TabsExitChoice.Cancel,
            "each button gives its answer");

        var others = new SaveTabsOnExitDialog(Rows(), SaveTabsOnExitDialog.Purpose.CloseOthers);
        expect((string)others.SaveButton.Content == "Save Checked & Close Tabs" && Name(others.SaveButton) == "Save Checked & Close Tabs"
               && (string)others.KeepButton.Content == "Keep Unsaved Tabs Open" && Name(others.KeepButton) == "Keep Unsaved Tabs Open"
               && (string)others.DiscardButton.Content == "Discard & Close Tabs" && Name(others.DiscardButton) == "Discard & Close Tabs"
               && others.Title == "Close Other Tabs" && others.FooterText.Visibility == Visibility.Collapsed,
            "Close Other Tabs: Save Checked & Close Tabs, Keep Unsaved Tabs Open, Discard & Close Tabs");
        expect(SaveTabsOnExitDialog.Labels(SaveTabsOnExitDialog.Purpose.Exit) == ("Save Checked & Close", "Keep Suspended for Next Launch", "Discard & Close"),
            "the words come from one place for each purpose");
        expect(!dialog.IsVisible && !others.IsVisible, "(neither dialog was shown)");

        // The 5.5A addenda: as HandPeg closes, every tab holding work is listed, a saved one shown as saved with nothing to tick.
        TabExitRow[] mixed = [new("a", "Alpha", "D:/projects/Alpha.hproj", false, "Alpha"), new("s", "Saved One", "D:/projects/Saved One.hproj", false, "", saved: true)];
        var withSaved = new SaveTabsOnExitDialog(mixed, SaveTabsOnExitDialog.Purpose.Exit);
        expect(mixed[1] is { Saved: true, NeedsSave: false, IsChecked: false, ShowsName: false, Ready: true, ToSave: false } && mixed[0] is { Saved: false, NeedsSave: true, IsChecked: true, ToSave: true },
            "(addenda) a saved tab's row: nothing to save, never checked, no name asked; the unsaved one checked to be saved");
        var content = (FrameworkElement)withSaved.Content;
        content.Measure(new Size(608, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        static FrameworkElement? Part(ItemsControl list, object item, string name) =>
            list.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter presenter ? presenter.ContentTemplate.FindName(name, presenter) as FrameworkElement : null;
        expect(Part(withSaved.RowsList, mixed[1], "SaveBox") is { Visibility: Visibility.Collapsed } && Part(withSaved.RowsList, mixed[1], "SavedRow") is { Visibility: Visibility.Visible } savedRow
               && Part(withSaved.RowsList, mixed[1], "SavedText") is TextBlock { Text: "Saved" } && AutomationProperties.GetName(savedRow) == "Saved One is saved"
               && Part(withSaved.RowsList, mixed[0], "SaveBox") is { Visibility: Visibility.Visible } && Part(withSaved.RowsList, mixed[0], "SavedRow") is { Visibility: Visibility.Collapsed },
            "(addenda) drawn: the saved row has no box to tick, a tick and Saved instead (named \"Saved One is saved\"); the unsaved row has its box");
        expect(withSaved.CanSave && withSaved.SaveButton.IsEnabled && withSaved.Title == "Unsaved Project Tabs"
               && withSaved.HeadingText.Text == "1 of 2 open project tabs has unsaved changes. Save it before closing?",
            "(addenda) one unsaved and one saved: Save is on for the unsaved one, and the heading counts them");
        mixed[0].IsChecked = false;
        expect(!withSaved.CanSave && !withSaved.SaveButton.IsEnabled, "(addenda) its unsaved row unchecked: nothing to save, Save is off (Discard & Close closes)");
        var allSaved = new SaveTabsOnExitDialog(
            [new("s", "Saved One", "D:/projects/Saved One.hproj", false, "", saved: true), new("t", "Saved Two", "D:/projects/Saved Two.hproj", false, "", saved: true)],
            SaveTabsOnExitDialog.Purpose.Exit);
        expect(allSaved.CanSave && allSaved.SaveButton.IsEnabled && allSaved.Title == "Open Project Tabs"
               && allSaved.HeadingText.Text == "2 project tabs are open, all saved. Keep them for the next time HandPeg starts?"
               && (string)allSaved.KeepButton.Content == "Keep Suspended for Next Launch" && (string)allSaved.DiscardButton.Content == "Discard & Close",
            "(addenda) two saved tabs are still asked about: keep them for the next start, or close them (Save Checked & Close simply closes: nothing is unsaved)");
        expect(SaveTabsOnExitDialog.ExitWords(3, 3).Heading == "3 project tabs have unsaved changes. Save them before closing?"
               && SaveTabsOnExitDialog.ExitWords(2, 5).Heading == "2 of 5 open project tabs have unsaved changes. Save them before closing?"
               && SaveTabsOnExitDialog.ExitWords(0, 3).Title == "Open Project Tabs",
            "(addenda) the heading for all, some and none of the tabs unsaved");
        expect(!withSaved.IsVisible && !allSaved.IsVisible, "(neither was shown)");

        // The classic question for one project alone (Pass 5.5A review): a project with a file is saved to it; the name box is for work never saved.
        var withFile = new SaveOnExitDialog("C0001", "D:/projects/Wedding.hproj");
        expect(withFile.SavesToFile && withFile.NameRow.Visibility == Visibility.Collapsed && withFile.SavesToText.Visibility == Visibility.Visible
               && withFile.SavesToText.Text == "Saves to its project file: Wedding.hproj" && withFile.SaveButton.IsEnabled,
            "one project opened from Wedding.hproj: no name box, Save & Close saves to Wedding.hproj (not to the video's name)");
        var never = new SaveOnExitDialog("C0001");
        expect(!never.SavesToFile && never.NameRow.Visibility == Visibility.Visible && never.SavesToText.Visibility == Visibility.Collapsed
               && never.ProjectName == "C0001" && never.SaveButton.IsEnabled,
            "work never saved: the name box, filled with the video's name");
        expect(!withFile.IsVisible && !never.IsVisible, "(neither was shown)");
    }

    // ----- project-tabs-slide-out -----

    private static void Idle()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(50);
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

    private static void SlideOut(Action<bool, string> expect)
    {
        // The gate (pure).
        expect(ProjectTabStrip.SlideWanted(2, true, true, true) && !ProjectTabStrip.SlideWanted(0, true, true, true) && !ProjectTabStrip.SlideWanted(2, false, true, true)
               && !ProjectTabStrip.SlideWanted(2, true, false, true) && !ProjectTabStrip.SlideWanted(2, true, true, false),
            "a slide only with tabs restored, the strip shown, the extra animations on (Settings) and Windows' client-area animations on");
        var slide = ProjectTabStrip.SlideAnimation(-120);
        expect(slide is { IsFrozen: true, From: -120, To: 0, FillBehavior: FillBehavior.Stop } && slide.Duration == new Duration(TimeSpan.FromMilliseconds(ProjectTabStrip.SlideMs))
               && ProjectTabStrip.SlideMs == 180 && slide.EasingFunction is CubicEase { EasingMode: EasingMode.EaseOut, IsFrozen: true },
            "the slide: from where it starts to its own place, 180 ms, a cubic ease out, frozen, gone at its end (FillBehavior.Stop)");
        expect(ProjectTabStrip.SlideFade is { IsFrozen: true, From: 0, To: 1, FillBehavior: FillBehavior.Stop } && ReferenceEquals(ProjectTabStrip.SlideFade.EasingFunction, ProjectTabStrip.SlideEase)
               && ReferenceEquals(ProjectTabStrip.SlideFade, ProjectTabStrip.SlideFade),
            "the fade in, from nothing to whole over the same ease, made once and frozen");
        expect(ProjectTabStrip.SlideOffset(10, 150) == -140, "a tab starts at the tab in front's x: its offset is the tab in front's x less its own place's");

        var background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        background.Freeze();
        var window = new Window
        {
            Left = -20000, Top = -20000, Width = 900, Height = 120, Background = background, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Title = "HandPeg golden: tab slide-out",
        };
        var strip = new ProjectTabStrip { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var (a, b, c) = (new ProjectTabItem { Id = "a", Title = "New Project", IsActive = true }, new ProjectTabItem { Id = "b", Title = "Second Project" },
            new ProjectTabItem { Id = "c", Title = "Third Project", IsDirty = true });
        strip.Tabs.Add(a);
        strip.Tabs.Add(b);
        strip.Tabs.Add(c);
        window.Content = new Grid { Children = { strip } };
        var settings = AppSettings.Current;
        var extra = settings.EnableExtraAnimations;
        try
        {
            window.Show();
            Idle();
            FrameworkElement Box(ProjectTabItem item) => (FrameworkElement)strip.Items.ItemContainerGenerator.ContainerFromItem(item);
            double X(FrameworkElement element) => element.TranslatePoint(new Point(0, 0), strip.Items).X;
            var (ax, bx, cx) = (X(Box(a)), X(Box(b)), X(Box(c)));
            var slots = new[] { a, b, c }.Select(t => VisualTreeHelper.GetOffset(Box(t)).X).ToArray();
            var plusSlot = VisualTreeHelper.GetOffset(strip.NewTabButton).X;

            // The extra animations off (Settings): nothing moves.
            settings.EnableExtraAnimations = false;
            strip.SlideOutFromActive(["b", "c"]);
            expect(!strip.IsSliding && Box(b).RenderTransform == Transform.Identity, "with the extra animations off the restored tabs are simply there");
            settings.EnableExtraAnimations = extra;

            var offsets = strip.BeginSlide(["b", "c"]);
            expect(offsets.Count == 2 && !offsets.ContainsKey("a") && Math.Abs(offsets["b"] - (ax - bx)) < 0.01 && Math.Abs(offsets["c"] - (ax - cx)) < 0.01 && offsets["c"] < offsets["b"] && offsets["b"] < 0,
                "each restored tab starts at the tab in front's x (its offset the tab in front's x less its own place's); the tab in front does not move");
            expect(Box(b).RenderTransform is TranslateTransform { IsFrozen: false } shift && shift.HasAnimatedProperties && Box(b).HasAnimatedProperties
                   && Box(c).RenderTransform is TranslateTransform { IsFrozen: false } && !ReferenceEquals(Box(b).RenderTransform, Box(c).RenderTransform),
                "each has a transform of its own, its x and its opacity animated");
            expect(strip.NewTabButton.RenderTransform is TranslateTransform { IsFrozen: false } plus && plus.HasAnimatedProperties, "+ slides out with them, from behind the tab in front too");
            expect(Panel.GetZIndex(Box(a)) == 1 && Panel.GetZIndex(strip.Items) == 1 && Panel.GetZIndex(Box(b)) == 0 && Panel.GetZIndex(Box(c)) == 0,
                "the tab in front is drawn over the tabs coming out from behind it, and the tabs over +");
            expect(new[] { a, b, c }.Select(t => VisualTreeHelper.GetOffset(Box(t)).X).SequenceEqual(slots) && VisualTreeHelper.GetOffset(strip.NewTabButton).X == plusSlot,
                "the layout never moves: only the RenderTransform and the opacity do");

            strip.FinishSlide();
            bool Clean() =>
                !strip.IsSliding && new FrameworkElement[] { Box(a), Box(b), Box(c), strip.NewTabButton, strip.OverflowButton }.All(e => e.RenderTransform == Transform.Identity && !e.HasAnimatedProperties)
                && new DependencyObject[] { Box(a), Box(b), Box(c), strip.Items }.All(e => e.ReadLocalValue(Panel.ZIndexProperty) == DependencyProperty.UnsetValue);
            expect(Clean(), "ended: every transform the identity again, no clock left, the z-order cleared");

            strip.BeginSlide(["b", "c"]);
            var until = Environment.TickCount64 + 3000;
            while (strip.IsSliding && Environment.TickCount64 < until)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(10);
            }

            expect(Clean(), "left alone, the slide ends by itself once its 180 ms have run, nothing left behind");

            strip.BeginSlide(["b"]);
            var sliding = strip.IsSliding;
            expect(sliding && strip.BeginReorder(b) && Clean(), "a drag along the strip ends a slide at once (the drag reads where the tabs are drawn)");
            strip.EndReorder();
            strip.BeginSlide(["c"]);
            strip.Visibility = Visibility.Collapsed;
            Idle();
            expect(Clean(), "and so does the strip being hidden");
            strip.Visibility = Visibility.Visible;
            Idle();

            expect(Descendants<StackPanel>(strip).All(p => p.Background is null) && strip.Items.Background is null && strip.Background is null
                   && Descendants<Button>(strip).Where(x => x.Name == "Tab").All(x => x.Margin == new Thickness(0, 0, ProjectTabStrip.TabGap, 0)),
                "afterwards nothing has a background (the gaps stay the caption) and every tab keeps its margin");
        }
        finally
        {
            settings.EnableExtraAnimations = extra;
            window.Close();
        }

        // Only after the first frame, once tabs are restored.
        var tabsSource = Source("MainWindow.ProjectTabs.cs");
        var stripSource = Source("Controls", "ProjectTabStrip.xaml.cs");
        expect(Ordered(MemberOf(tabsSource, "private async void RecoverProjectTabs("), "_tabs.TakeLeftBehind(known, out var front)", "adopted.Add(tab.Id);", "ShowTabs();", "ProjectTabStrip.SlideOutFromActive(adopted);")
               && !MemberOf(tabsSource, "private void InitializeProjectTabs(").Contains("SlideOutFromActive", StringComparison.Ordinal)
               && tabsSource.Split("SlideOutFromActive(").Length == 2,
            "called only as the tabs left by the last session are taken in (ApplicationIdle after ContentRendered: never on the first frame)");
        expect(stripSource.Contains("private static IEasingFunction? _slideEase;", StringComparison.Ordinal) && stripSource.Contains("private static DoubleAnimation? _slideFade;", StringComparison.Ordinal)
               && !stripSource.Contains("static readonly CubicEase", StringComparison.Ordinal) && !stripSource.Contains("static readonly DoubleAnimation", StringComparison.Ordinal)
               && MemberOf(stripSource, "internal bool BeginReorder(").Contains("FinishSlide();", StringComparison.Ordinal),
            "the ease and the fade are made on first use (nothing at type load), and a drag ends a slide first");
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
}
