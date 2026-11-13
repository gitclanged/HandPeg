using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.5A lane C: the lagged recovery copy (N-2 written, N-1 and N on its Redo) for the autosave and the front tab's crash-recovery cache file.
/// autosave-lag-n2: three edits after a save; the copy is the Sequence after the first, its Undo the first's step, its Redo the last two, the
/// view model's own history untouched; written and opened, it is exactly the project two Undos leave, and Ctrl+Y gives N-1 then N.
/// autosave-lag-threshold: one fresh step, or one since a save, is written as it is; an Undo still lags (onto the saved state); undoing past
/// the save then two new steps lags (the branch rule); an edit under way does not; Save writes N. autosave-lag-project-frame: a Project
/// Settings step among the last two: the copy has the frame before it, Redo puts the turned frame back. autosave-lag-front-tab: the tab in
/// front's crash-recovery file, written two actions back, an exact write after it on its queue lands last, the tick's table (written,
/// deleted when clean, never for a tab not brought back), and a crash leaves it for the next start, which opens it at N-2 with Ctrl+Y to N;
/// a lagged copy has no saved fingerprint (two edits since a save still come back unsaved) and says how far back it is (StepsBack).
/// autosave-lag-front-tab-save (the review's fixes): a save of the tab in front lets its recovery file go at once, even while playing; the 20 s
/// tick drops one left after a save it was not told of before anything holds it back, and waits out a press, a modal dialog or a close.
/// </summary>
public static class LaggedRecoveryTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string First = "D:/media/first.mkv", Second = "D:/media/second.mkv";

    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("autosave-lag-n2", "# The recovery copy is written two actions back: N-2 opened, N-1 and N on its Redo, exactly as two Undos leave the project.", LagN2);
        yield return Case("autosave-lag-threshold", "# Fewer than two steps since the last save or open, or an edit under way: the copy is the project as it is; Save never lags.", Threshold);
        yield return Case("autosave-lag-project-frame", "# A Project Settings step among the last two: the copy has the frame before it, and Redo puts each frame back with its step.", ProjectFrame);
        yield return Case("autosave-lag-front-tab", "# The tab in front's crash-recovery file: written two actions back on its queue, deleted when clean, and opened by the next start at N-2.", FrontTab);
        yield return Case("autosave-lag-front-tab-save", "# A save of the tab in front lets its crash-recovery file go at once, even while playing; the tick drops a stale one first and waits out a press.", FrontTabSaved);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
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

    private static string Fresh(string name)
    {
        var folder = Path.Combine(AppPaths.Temp, name);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A view model holding a Sequence of dropped files (as ProjectTabsTests.Open: nothing is probed as it is opened again), their files in Project Media as a drop puts them, taken as saved.</summary>
    private static MainViewModel Open()
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
        return vm;
    }

    /// <summary>Every clip of the Sequence, track by track: what must come back exactly.</summary>
    private static string Shape(TimelineDocument document) =>
        string.Join(" | ", document.Tracks.Select(t => string.Join(", ", t.Clips.Select(c => FormattableString.Invariant($"{c.Id} {Path.GetFileName(c.SourcePath)} {c.Start:0.###}-{c.End:0.###}")))));

    /// <summary>The whole project as its fingerprint has it (CaptureState, its time left out): every setting, the Sequence, the bin.</summary>
    private static string StateJson(MainViewModel vm)
    {
        var state = vm.CaptureState();
        state.SavedAt = default;
        return JsonSerializer.Serialize(state);
    }

    /// <summary>The three edits every case starts from: the first clip trimmed to 3 s, the second deleted, the clip on track 2 trimmed to 2 s.</summary>
    private static (string S1, string S2, string S3) ThreeEdits(MainViewModel vm, Action<bool, string> expect)
    {
        var done = vm.TrimClip(vm.ViewOf(vm.Document.Tracks[0].Clips[0]), start: false, seconds: 3);
        var s1 = Shape(vm.Document);
        done &= vm.DeleteClip(vm.ViewOf(vm.Document.Tracks[0].Clips[1]));
        var s2 = Shape(vm.Document);
        done &= vm.TrimClip(vm.ViewOf(vm.Document.Tracks[1].Clips[0]), start: false, seconds: 2);
        var s3 = Shape(vm.Document);
        expect(done && s1 != s2 && s2 != s3, "three edits, each one step of Undo: a trim, a delete, a trim");
        return (s1, s2, s3);
    }

    private static string[] Names(List<UndoStepState>? steps) => steps?.Select(s => s.What).ToArray() ?? [];

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

        if (!task.IsCompleted)
            throw new TimeoutException("the project did not open within 30 s");
        task.GetAwaiter().GetResult();
    }

    /// <summary>A project file opened in a fresh view model as File &gt; Open puts a project in place (RestoreStateAsync, from the window's thread's queue).</summary>
    private static MainViewModel Opened(string path)
    {
        var vm = new MainViewModel();
        var state = ProjectStore.Load(path);
        PumpUntil(Dispatcher.CurrentDispatcher.InvokeAsync(() => vm.RestoreStateAsync(state, "Opened")).Task.Unwrap());
        return vm;
    }

    // ----- autosave-lag-n2 -----

    private static void LagN2(Action<bool, string> expect)
    {
        var vm = Open();
        var s0 = Shape(vm.Document);
        var (s1, s2, s3) = ThreeEdits(vm, expect);
        var exact = vm.CaptureStateForSave();
        var names = Names(exact.UndoHistory);
        expect(names.Length == 3 && exact.RedoHistory is null && vm.FreshSteps == 3 && vm.CanLagRecovery,
            "the exact state carries the three steps and nothing to redo; three steps since the save: the copy lags");

        var lagged = vm.CaptureStateForRecovery("Lagged");
        expect(Shape(lagged.Timeline) == s1, "the copy's Sequence is the one after the first edit (N-2)");
        expect(lagged.UndoHistory is [{ } only] && only.What == names[0] && ReferenceEquals(only.State, exact.UndoHistory![0].State),
            "its Undo holds the first edit's step alone (the very packed bytes Undo keeps)");
        expect(Names(lagged.RedoHistory).SequenceEqual([names[2], names[1]]), "its Redo holds the last two actions, the second edit last (Ctrl+Y gives it first)");
        expect(Shape(vm.Document) == s3 && Names(vm.CaptureStateForSave().UndoHistory).SequenceEqual(names) && vm.CaptureStateForSave().RedoHistory is null,
            "the view model is left as it was: the Sequence at N, its three steps, nothing to redo");

        var path = Path.Combine(Fresh("autosave-lag-n2"), "Lagged.autosave.hproj");
        ProjectStore.SaveTo(lagged, path);
        var opened = Opened(path);
        expect(Shape(opened.Document) == s1 && !opened.HasUnsavedChanges, "written and opened, the project is at N-2, taken as it was opened");

        vm.Undo();
        vm.Undo();
        var twoBack = StateJson(vm);
        expect(Shape(vm.Document) == s1 && StateJson(opened) == twoBack, "opened, it is exactly the project two Undos leave (every setting, the Sequence, the bin)" + Differs(StateJson(opened), twoBack));
        vm.Redo();
        vm.Redo();
        expect(Shape(vm.Document) == s3, "(the view model's two Undos redone)");

        opened.Redo();
        expect(Shape(opened.Document) == s2, "Ctrl+Y gives N-1: the second edit back");
        opened.Redo();
        expect(Shape(opened.Document) == s3 && StateJson(opened) == StateJson(vm), "Ctrl+Y again gives N: the project exactly as it was live");
        opened.Undo();
        opened.Undo();
        opened.Undo();
        expect(Shape(opened.Document) == s0, "and Undo goes back through all three steps to the saved Sequence");
    }

    // ----- autosave-lag-threshold -----

    private static void Threshold(Action<bool, string> expect)
    {
        // One step since the save: as it is.
        var one = Open();
        expect(one.TrimClip(one.ViewOf(one.Document.Tracks[0].Clips[0]), start: false, seconds: 3), "one edit since the save");
        var single = one.CaptureStateForRecovery();
        expect(!one.CanLagRecovery && Shape(single.Timeline) == Shape(one.Document) && single.UndoHistory?.Count == 1 && single.RedoHistory is null,
            "one fresh step: the copy is the project as it is, its one step to undo and nothing to redo");

        // Three steps, a save, one more: as it is (one step since the save).
        var saved = Open();
        ThreeEdits(saved, expect);
        saved.MarkSaved();
        expect(saved.FreshSteps == 0 && !saved.CanLagRecovery, "saved after three edits: no fresh step");
        expect(saved.TrimClip(saved.ViewOf(saved.Document.Tracks[0].Clips[^1]), start: false, seconds: 10), "one edit after the save");
        var afterSave = saved.CaptureStateForRecovery();
        expect(!saved.CanLagRecovery && Shape(afterSave.Timeline) == Shape(saved.Document) && afterSave.UndoHistory?.Count == 4 && afterSave.RedoHistory is null,
            "one step since the save: the copy is the project as it is, never two steps back past the save");

        // Three edits, then Undo: still two fresh steps, and the copy lands on the saved Sequence.
        var undone = Open();
        var s0 = Shape(undone.Document);
        var (_, s2, _) = ThreeEdits(undone, expect);
        var names = Names(undone.CaptureStateForSave().UndoHistory);
        undone.Undo();
        var back = undone.CaptureStateForRecovery();
        expect(Shape(undone.Document) == s2 && undone.FreshSteps == 2 && Shape(back.Timeline) == s0 && back.UndoHistory is null,
            "three edits and an Undo: two fresh steps, the copy is the saved Sequence with nothing to undo");
        expect(Names(back.RedoHistory).SequenceEqual([names[2], names[1], names[0]]), "its Redo gives the first edit, then the second, then the third that was undone (Ctrl+Y in that order)");

        // Undoing past the save, then two new steps: a new branch, and the copy lags (never before the state the undos reached).
        var branch = Open();
        ThreeEdits(branch, expect);
        branch.MarkSaved();
        branch.Undo();
        branch.Undo();
        var reached = Shape(branch.Document);
        var pastSave = branch.FreshSteps;
        expect(pastSave < 0 && !branch.CanLagRecovery && Shape(branch.CaptureStateForRecovery().Timeline) == reached, "two Undos past the save: no fresh step, the copy as it is");
        expect(branch.DeleteClip(branch.ViewOf(branch.Document.Tracks[1].Clips[0])), "a new edit there");
        expect(branch.FreshSteps == 1 && !branch.CanLagRecovery, "one step on the new branch: the copy as it is");
        expect(branch.TrimClip(branch.ViewOf(branch.Document.Tracks[0].Clips[0]), start: false, seconds: 1), "a second new edit");
        var branched = branch.CaptureStateForRecovery();
        expect(branch.FreshSteps == 2 && Shape(branched.Timeline) == reached && branched.RedoHistory?.Count == 2,
            "two steps on the new branch (the saved state left behind on it): the copy lags, to the state the Undos reached");

        // An edit under way: as it is.
        var held = Open();
        ThreeEdits(held, expect);
        using (held.Document.BeginTransaction("held open"))
        {
            var during = held.CaptureStateForRecovery();
            expect(!held.CanLagRecovery && Shape(during.Timeline) == Shape(held.Document) && during.RedoHistory is null,
                "an edit open on the Sequence: the copy is the project as it is (no lag while a change is under way)");
        }

        expect(held.CanLagRecovery, "once it is closed the copy lags again");

        // Save writes N, and leaves no fresh step behind it.
        var path = Path.Combine(Fresh("autosave-lag-threshold"), "Saved.hproj");
        var line = held.SaveProjectTo(path);
        var file = ProjectStore.Load(path);
        expect(line.StartsWith("Project saved", StringComparison.Ordinal) && Shape(file.Timeline) == Shape(held.Document) && file.UndoHistory?.Count == 3 && file.RedoHistory is null,
            "Save with three fresh steps writes the project as it is (N), its three steps to undo, nothing to redo");
        expect(held.FreshSteps == 0 && !held.CanLagRecovery, "and after the save no step is fresh: the next copy is exact until two more steps");
    }

    // ----- autosave-lag-project-frame -----

    /// <summary>The Project Settings as the view model holds them, in a line: natural size, turned, the Sequence's own turn.</summary>
    private static string Frame(MainViewModel vm) =>
        $"{vm.CurrentProjectSettings.NaturalWidth}x{vm.CurrentProjectSettings.NaturalHeight} vertical {vm.UseVerticalResolution} output '{vm.OutputWidth}' x '{vm.OutputHeight}'";

    /// <summary>Where two projects' JSON differ (the first few paths), for a failure's line; "" when they do not.</summary>
    private static string Differs(string a, string b)
    {
        if (a == b)
            return "";
        var parts = new List<string>();
        void Walk(JsonElement x, JsonElement y, string path)
        {
            if (parts.Count >= 6)
                return;
            if (x.ValueKind != y.ValueKind)
            {
                parts.Add(path);
                return;
            }

            switch (x.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var name in x.EnumerateObject().Select(p => p.Name).Union(y.EnumerateObject().Select(p => p.Name)))
                    {
                        if (x.TryGetProperty(name, out var l) && y.TryGetProperty(name, out var r))
                            Walk(l, r, $"{path}.{name}");
                        else
                            parts.Add($"{path}.{name}");
                    }

                    break;
                case JsonValueKind.Array when x.GetArrayLength() != y.GetArrayLength():
                    parts.Add($"{path} (count)");
                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var (l, r) in x.EnumerateArray().Zip(y.EnumerateArray()))
                        Walk(l, r, $"{path}[{i++}]");
                    break;
                default:
                    if (x.GetRawText() != y.GetRawText())
                        parts.Add($"{path}: {x.GetRawText()} / {y.GetRawText()}");
                    break;
            }
        }

        using var left = JsonDocument.Parse(a);
        using var right = JsonDocument.Parse(b);
        Walk(left.RootElement, right.RootElement, "$");
        return " (differs at " + string.Join(", ", parts) + ")";
    }

    private static bool Turn(MainViewModel vm) =>
        vm.ApplyProjectSettings(1080, 1920, null, vm.Document.ColorSpace, vm.Document.PixelAspect, vm.Document.SampleRate);

    private static void ProjectFrame(Action<bool, string> expect)
    {
        var folder = Fresh("autosave-lag-project-frame");

        // The Project Settings step in the middle: N-2 is before it.
        var vm = Open();
        var flat = Frame(vm);
        expect(vm.TrimClip(vm.ViewOf(vm.Document.Tracks[0].Clips[0]), start: false, seconds: 3), "edit 1: a trim");
        var s1 = Shape(vm.Document);
        expect(Turn(vm) && vm.UseVerticalResolution, "edit 2: Project Settings to 1080 x 1920 (the frame turned tall), one step");
        var tall = Frame(vm);
        expect(vm.DeleteClip(vm.ViewOf(vm.Document.Tracks[0].Clips[1])), "edit 3: a delete");
        var s3 = Shape(vm.Document);
        expect(flat != tall && Frame(vm) == tall, "the frame is tall from edit 2 on");
        var path = Path.Combine(folder, "Middle.autosave.hproj");
        ProjectStore.SaveTo(vm.CaptureStateForRecovery(), path);
        var opened = Opened(path);
        expect(Shape(opened.Document) == s1 && Frame(opened) == flat, "opened at N-2: the Sequence after edit 1, the frame as it was before the Project Settings step");
        vm.Undo();
        vm.Undo();
        expect(StateJson(opened) == StateJson(vm), "exactly the project two Undos leave" + Differs(StateJson(opened), StateJson(vm)));
        vm.Redo();
        vm.Redo();
        opened.Redo();
        expect(Frame(opened) == tall, "Ctrl+Y gives N-1: the frame turned tall again with its step" + (Frame(opened) == tall ? "" : $" ({Frame(opened)} / {tall})"));
        opened.Redo();
        expect(Shape(opened.Document) == s3 && Frame(opened) == tall && StateJson(opened) == StateJson(vm), "Ctrl+Y again gives N, tall, exactly as it was live" + Differs(StateJson(opened), StateJson(vm)));

        // The Project Settings step last: N-1 is still flat, N tall.
        var last = Open();
        expect(last.TrimClip(last.ViewOf(last.Document.Tracks[0].Clips[0]), start: false, seconds: 3) && last.DeleteClip(last.ViewOf(last.Document.Tracks[0].Clips[1])),
            "edits 1 and 2: a trim and a delete");
        var t2 = Shape(last.Document);
        expect(Turn(last) && Frame(last) == tall, "edit 3: Project Settings to 1080 x 1920");
        var lastPath = Path.Combine(folder, "Last.autosave.hproj");
        ProjectStore.SaveTo(last.CaptureStateForRecovery(), lastPath);
        var openedLast = Opened(lastPath);
        expect(Frame(openedLast) == flat, "opened at N-2: flat");
        openedLast.Redo();
        expect(Shape(openedLast.Document) == t2 && Frame(openedLast) == flat, "Ctrl+Y gives N-1: the delete back, still flat (the turn is the last action)");
        openedLast.Redo();
        expect(Frame(openedLast) == tall && StateJson(openedLast) == StateJson(last), "Ctrl+Y again gives N: tall, exactly as it was live" + Differs(StateJson(openedLast), StateJson(last)));
    }

    // ----- autosave-lag-front-tab -----

    private static void FrontTab(Action<bool, string> expect)
    {
        // What the 20 s tick does (pure).
        static MainWindow.FrontRecoveryStep Step(bool unresumed, bool written, bool dirty, bool moved) => MainWindow.FrontRecoveryFor(unresumed, written, dirty, moved);
        expect(Step(false, false, true, true) == MainWindow.FrontRecoveryStep.Write && Step(false, true, true, true) == MainWindow.FrontRecoveryStep.Write,
            "the tick: unsaved changes and none written yet, or the undo position moved since: written");
        expect(Step(false, true, true, false) == MainWindow.FrontRecoveryStep.None && Step(false, false, false, true) == MainWindow.FrontRecoveryStep.None,
            "unsaved but not moved since the last write, or clean with none written: nothing");
        expect(Step(false, true, false, true) == MainWindow.FrontRecoveryStep.Delete && Step(false, true, false, false) == MainWindow.FrontRecoveryStep.Delete,
            "clean (saved, or undone back to the save) with one written: deleted");
        expect(new[] { (true, true), (true, false), (false, true), (false, false) }.All(p => Step(true, p.Item1, p.Item2, true) == MainWindow.FrontRecoveryStep.None),
            "a tab in front that could not be brought back: never written nor deleted (its file is its work)");

        var root = Fresh("autosave-lag-front-tab");
        var tabs = new ProjectTabs(root, "front", "crashed");
        var vm = Open();
        var (s1, s2, s3) = ThreeEdits(vm, expect);
        var front = tabs.Active;
        (front.Title, front.Dirty, front.Empty, front.PlayheadMs) = ("Front Tab", true, false, 1500);
        var header = ProjectTabs.HeaderJson(front);
        var saved = vm.SavedFingerprintForTab;

        // Written two actions back, the state given left as it is.
        var state = vm.CaptureStateForTab(front.Title);
        var lag = vm.TakeRecoveryLag();
        expect(lag is not null && MainWindow.WriteRecoveryFile(tabs, front.Id, state, lag, saved, header) == true,
            "the tab in front with three fresh steps: its recovery file written two actions back");
        var read = tabs.ReadState(front.Id);
        expect(Shape(read.Timeline) == s1 && read.UndoHistory?.Count == 1 && read.RedoHistory?.Count == 2,
            "<id>.hptab holds N-2, one step to undo and the last two actions to redo");
        expect(!File.Exists(tabs.SavedFile(front.Id)) && ProjectTabs.ReadHeader(File.ReadAllText(tabs.HeaderFile(front.Id))) is { Dirty: true, Empty: false, Title: "Front Tab", StepsBack: 2 },
            "beside it its header (unsaved changes, two actions back) and no saved state's fingerprint: a lagged copy comes back unsaved until it is saved");
        expect(Shape(state.Timeline) == s3 && state.RedoHistory is null && state.UndoHistory?.Count == 3, "the state captured is left as it was (N, exact): the lag is made of a copy");
        expect(MainWindow.WriteRecoveryFile(tabs, front.Id, vm.CaptureStateForTab(front.Title), null, saved, ProjectTabs.WithStepsBack(header, 2)) == false
               && File.ReadAllText(tabs.SavedFile(front.Id)) == saved && ProjectTabs.ReadHeader(File.ReadAllText(tabs.HeaderFile(front.Id))) is { Dirty: true, StepsBack: 0 },
            "written exactly as it is (no lag to take): its saved state's fingerprint beside it, and its header says it is exact again");

        // An exact write asked later on the tab's queue lands after a lagged one, however long that takes.
        var (late, lateLag, exact) = (vm.CaptureStateForTab(front.Title), vm.TakeRecoveryLag(), vm.CaptureStateForTab(front.Title));
        bool? slow = null;
        _ = tabs.Enqueue(front.Id, () =>
        {
            Thread.Sleep(150);
            slow = MainWindow.WriteRecoveryFile(tabs, front.Id, late, lateLag, saved, header);
        });
        _ = tabs.Enqueue(front.Id, () => tabs.WriteTab(front.Id, exact, saved, header));
        expect(tabs.WaitFor(front.Id, 10_000) && slow == true && Shape(tabs.ReadState(front.Id).Timeline) == s3 && tabs.ReadState(front.Id).RedoHistory is null
               && ProjectTabs.ReadHeader(File.ReadAllText(tabs.HeaderFile(front.Id))) is { StepsBack: 0 },
            "a lagged write still running and an exact write asked after it (put behind, kept for the next start): the exact one lands last, its header exact");

        // HandPeg falls over with the lagged file there: the next start takes it in and opens it at N-2.
        expect(MainWindow.WriteRecoveryFile(tabs, front.Id, vm.CaptureStateForTab(front.Title), vm.TakeRecoveryLag(), saved, header) == true, "the recovery file written two actions back again");
        tabs.ReleaseSession();
        var next = new ProjectTabs(root, "fresh", "next");
        var left = next.TakeLeftBehind([next.Active.Id]);
        expect(left is [{ Id: "front", Dirty: true, Empty: false, Title: "Front Tab", StepsBack: 2 }] && next.Adopt(left[0]),
            "a crash later, the next start takes the tab in (its header: unsaved changes, two actions back) behind its start-up tab");
        var back = new MainViewModel();
        var resuming = Dispatcher.CurrentDispatcher.InvokeAsync(() => back.ResumeTabAsync(next.ReadState("front"), left[0].ProjectPath, left[0].Dirty, next.ReadSaved("front"))).Task.Unwrap();
        PumpUntil(resuming);
        expect(Shape(back.Document) == s1 && back.HasUnsavedChanges, "clicked, it opens at N-2 with its changes still unsaved");
        back.Redo();
        expect(Shape(back.Document) == s2, "Ctrl+Y gives N-1");
        back.Redo();
        expect(Shape(back.Document) == s3 && StateJson(back) == StateJson(vm), "Ctrl+Y again gives N: the work as it was when HandPeg fell over");
        next.ForgetSession();

        // Pass 5.5A review: exactly two edits since the save. N-2 is then the saved state itself; with no saved fingerprint the copy still comes
        // back unsaved (never taken as saved: Ctrl+W asks, the exit dialog lists it, a second crash keeps it), the last two actions on its Redo.
        var two = Open();
        var t0 = Shape(two.Document);
        var edited = two.TrimClip(two.ViewOf(two.Document.Tracks[0].Clips[0]), start: false, seconds: 3);
        var t1 = Shape(two.Document);
        edited &= two.DeleteClip(two.ViewOf(two.Document.Tracks[0].Clips[1]));
        var t2 = Shape(two.Document);
        var twoTabs = new ProjectTabs(Fresh("autosave-lag-front-tab-two"), "two", "crashed");
        (twoTabs.Active.Title, twoTabs.Active.Dirty, twoTabs.Active.Empty) = ("Two Edits", true, false);
        expect(edited && t0 != t1 && t1 != t2
               && MainWindow.WriteRecoveryFile(twoTabs, "two", two.CaptureStateForTab("Two Edits"), two.TakeRecoveryLag(), two.SavedFingerprintForTab, ProjectTabs.HeaderJson(twoTabs.Active)) == true
               && Shape(twoTabs.ReadState("two").Timeline) == t0 && twoTabs.ReadSaved("two") is null,
            "two edits since the save: the copy is the saved Sequence itself (N-2), written with no saved state's fingerprint");
        var twoBack = new MainViewModel();
        PumpUntil(Dispatcher.CurrentDispatcher.InvokeAsync(() => twoBack.ResumeTabAsync(twoTabs.ReadState("two"), "", true, twoTabs.ReadSaved("two"))).Task.Unwrap());
        expect(Shape(twoBack.Document) == t0 && twoBack.HasUnsavedChanges, "brought back after a crash, it opens at the saved Sequence with its changes unsaved (the dot stays)");
        twoBack.Redo();
        var gaveN1 = Shape(twoBack.Document) == t1;
        twoBack.Redo();
        expect(gaveN1 && Shape(twoBack.Document) == t2 && StateJson(twoBack) == StateJson(two) && twoBack.HasUnsavedChanges, "Ctrl+Y gives N-1, then N, still unsaved");
        twoTabs.ForgetSession();

        // Pass 5.5A review: a tab brought back from a lagged copy says so once; the exit dialog's row says so while it stays behind.
        expect(MainWindow.StepsBackLine(2) == "Opened 2 actions back from where HandPeg stopped: Ctrl+Y twice brings the last edits back.",
            "the status line as it comes back: \"Opened 2 actions back from where HandPeg stopped: Ctrl+Y twice brings the last edits back.\"");

        // The window's wiring.
        var window = Source("MainWindow.xaml.cs");
        var cache = Source("MainWindow.TabCache.cs");
        var tabsSource = Source("MainWindow.ProjectTabs.cs");
        expect(Ordered(MemberOf(window, "private void Autosave_Tick("), "if (_viewModel.IsPlaying || _gpu is { IsPlaying: true } || IsScrubHeld || TimelineDragHeld || _switchingTab)",
                   "return;", "KeepFrontRecovery();", "settings.AutosaveEnabled", "_lastAutosave = DateTime.UtcNow;", "_viewModel.AutosaveAsync();"),
            "the 20 s tick: nothing while playing, scrubbing, dragging or switching (the autosave neither); the tab in front's file whatever the autosave says");
        expect(Ordered(MemberOf(cache, "private void KeepFrontRecovery("), "if (_frontRecovery is { } last && last.Id != tab.Id)", "if (_viewModel.BusyWithDocument || _viewModel.IsRecording)",
                   "FrontRecoveryFor(_unresumed.Contains(tab.Id),", "WriteFrontRecovery(tab, position);", "DeleteTabFiles(tab.Id);"),
            "the tick follows the table, never for a tab not brought back");

        // 5.5.0 (the user's Q9 b): written during an export, the queue or captions; held only while the Sequence is opened, loaded or rewritten.
        var busyVm = new MainViewModel();
        var loads = typeof(MainViewModel).GetField("_operationLoadsDocument", BindingFlags.NonPublic | BindingFlags.Instance)!;
        busyVm.IsBusy = true;
        var exporting = busyVm.BusyWithDocument;
        loads.SetValue(busyVm, true);
        var opening = busyVm.BusyWithDocument;
        var vmSource = Source(Path.Combine("ViewModels", "MainViewModel.EncodeAndLoad.cs")) + Source(Path.Combine("ViewModels", "MainViewModel.Project.cs"))
            + Source(Path.Combine("ViewModels", "MainViewModel.OutputAndDeadAir.cs"));
        expect(!exporting && opening
               && new[] { "\"Opening a project\"", "\"Loading the source\"", "\"Loading with a Game Preset\"", "\"Downloading a link\"", "\"Removing dead air\"" }
                   .All(t => vmSource.Contains($"RunOperationAsync({t}, WorkLane.Interactive, loadsDocument: true,", StringComparison.Ordinal))
               && new[] { "\"Exporting\"", "\"Encoding the queue\"" }.All(t => vmSource.Contains($"RunOperationAsync({t}, WorkLane.Foreground, async", StringComparison.Ordinal) || Source(Path.Combine("ViewModels", "MainViewModel.Queue.cs")).Contains($"RunOperationAsync({t}, WorkLane.Foreground, async", StringComparison.Ordinal)),
            "(5.5.0, Q9 b) the tab in front's recovery copy is written while an export, the queue or captions run, and held while a project or file is opened, loaded or rewritten (Remove Dead Air)");
        expect(Ordered(MemberOf(cache, "private void WriteFrontRecovery("), "TakeHeaderForTab(header);", "_viewModel.CaptureStateForTab(", "_viewModel.TakeRecoveryLag()",
                   "_tabs.Enqueue(id, () =>", "WriteRecoveryFile(_tabs, id, state, lag, saved, json)"),
            "captured on the window's thread, lagged and written on the tab's own queue (the queue every write and delete of its files is on)");
        expect(Ordered(MemberOf(cache, "private void DeleteTabFiles("), "_frontRecovery = null;", "_tabs.Enqueue(id, () => _tabs.DeleteFiles(id));")
               && MemberOf(tabsSource, "private void RefreshActiveTab(").Contains("DropFrontRecovery(tab);", StringComparison.Ordinal)
               && MemberOf(tabsSource, "private ProjectTabHeader PutActiveBehind(").Contains("DropFrontRecovery(tab);", StringComparison.Ordinal),
            "deleted with the tab's files (brought back, closed), at once when it is saved or undone back to the save, and when an emptied tab goes behind");
        expect(MemberOf(cache, "internal static bool? WriteRecoveryFile(").Contains("tabs.WriteTab(id, lagged ?? state, back ? null : saved, ProjectTabs.WithStepsBack(header, back ? MainViewModel.RecoveryLagSteps : 0));", StringComparison.Ordinal)
               && MemberOf(tabsSource, "private void TakeHeaderForTab(").Contains("tab.StepsBack = 0;", StringComparison.Ordinal)
               && Ordered(MemberOf(tabsSource, "private async Task<bool> ResumeTabAsync("), "DeleteTabFiles(id);", "else if (tab.StepsBack > 0)", "_viewModel.StatusText = StepsBackLine(tab.StepsBack);", "tab.StepsBack = 0;")
               && MemberOf(Source("MainWindow.TabExit.cs"), "private List<TabExitRow> RowsOf(").Contains("RowDetail(path, tab.StepsBack)", StringComparison.Ordinal),
            "a lagged copy is marked in its header (every exact write of the tab clears it), said once as the tab comes back, and named in the exit dialog's row");

        // Only the autosave and this file lag: every other write is exact.
        var src = Path.Combine(RepositoryRoot(), "src");
        var lagging = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("TakeRecoveryLag()", StringComparison.Ordinal)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var project = Source("ViewModels", "MainViewModel.Project.cs");
        expect(lagging.SequenceEqual(["MainViewModel.Project.cs", "MainViewModel.Recovery.cs", "MainWindow.TabCache.cs"])
               && project.Split("TakeRecoveryLag()").Length == 2 && MemberOf(project, "public async Task AutosaveAsync(").Contains("TakeRecoveryLag()", StringComparison.Ordinal)
               && cache.Split("TakeRecoveryLag()").Length == 2 && MemberOf(cache, "private void WriteFrontRecovery(").Contains("TakeRecoveryLag()", StringComparison.Ordinal),
            "the lag is taken only by the autosave and the tab in front's recovery file (Save, Save As, portable, bundle, tabs put behind or kept, duplicates and the crash writer stay exact)");
    }

    // ----- autosave-lag-front-tab-save -----

    private static void SetField(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Hidden)!.SetValue(window, value);

    /// <summary>The tick's last look at the tab in front set as WriteFrontRecovery leaves it: a recovery file of <paramref name="id"/> written.</summary>
    private static void LookWritten(MainWindow window, string id) =>
        SetField(window, "_frontRecovery", Activator.CreateInstance(typeof(MainWindow).GetNestedType("FrontLook", BindingFlags.NonPublic)!, id, ((object?)null, 0, 0), true));

    private static void FrontTabSaved(Action<bool, string> expect)
    {
        var root = Fresh("autosave-lag-front-tab-save");
        var tabs = new ProjectTabs(root, "front", "saving");
        var vm = Open();
        ThreeEdits(vm, expect);
        var front = tabs.Active;
        (front.Title, front.Dirty, front.Empty) = ("Front Tab", true, false);
        expect(MainWindow.WriteRecoveryFile(tabs, front.Id, vm.CaptureStateForTab(front.Title), vm.TakeRecoveryLag(), vm.SavedFingerprintForTab, ProjectTabs.HeaderJson(front)) == true
               && tabs.HasState(front.Id), "the tab in front's crash-recovery file written two actions back");

        // The window as far as these paths reach (none made, nothing shown): its tabs, its view model, and its tick's last look (a file written).
        var window = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        SetField(window, "_tabs", tabs);
        SetField(window, "_viewModel", vm);
        SetField(window, "_unresumed", new HashSet<string>(StringComparer.Ordinal));
        LookWritten(window, front.Id);
        var onSaved = typeof(MainWindow).GetMethod("ViewModel_ProjectSaved", Hidden)!;
        void Saved() => onSaved.Invoke(window, null);
        void Tick() => typeof(MainWindow).GetMethod("Autosave_Tick", Hidden)!.Invoke(window, [null, EventArgs.Empty]);
        bool Gone() => tabs.WaitFor(front.Id, 10_000) && !tabs.HasState(front.Id) && !File.Exists(tabs.HeaderFile(front.Id));

        // Ctrl+S, then Space at once (the settling look and the tick both wait while playing): the save itself lets the file go.
        vm.ProjectSaved += Saved;
        vm.IsPlaying = true;
        var line = vm.SaveProjectTo(Path.Combine(root, "Saved.hproj"));
        expect(line.StartsWith("Project saved", StringComparison.Ordinal) && Gone(),
            "a save made while playing: the recovery file and its header are gone (deleted on its queue, after any write of it)");
        expect(typeof(MainWindow).GetField("_frontRecovery", Hidden)!.GetValue(window) is null, "and the tick's last look is let go: the next tick looks afresh");
        vm.ProjectSaved -= Saved;

        // A recovery file left after a save the window was not told of: the 20 s tick lets it go first, while playing, with no fingerprint.
        expect(MainWindow.WriteRecoveryFile(tabs, front.Id, vm.CaptureStateForTab(front.Title), vm.TakeRecoveryLag(), vm.SavedFingerprintForTab, ProjectTabs.HeaderJson(front)) == false
               && tabs.WaitFor(front.Id, 10_000) && tabs.HasState(front.Id) && !vm.MayHaveUnsavedChanges, "(a recovery file written again, the project saved)");
        LookWritten(window, front.Id);
        Tick();
        expect(Gone(), "the tick, while playing, with nothing unsaved since: the file goes before playback holds the tick back");

        // Unsaved again: the tick leaves the file alone while playing (written or dropped only once it may look).
        expect(vm.DeleteClip(vm.ViewOf(vm.Document.Tracks[0].Clips[^1])) && vm.MayHaveUnsavedChanges, "an edit after the save");
        expect(MainWindow.WriteRecoveryFile(tabs, front.Id, vm.CaptureStateForTab(front.Title), vm.TakeRecoveryLag(), vm.SavedFingerprintForTab, ProjectTabs.HeaderJson(front)) is not null
               && tabs.WaitFor(front.Id, 10_000), "(its recovery file written)");
        LookWritten(window, front.Id);
        Tick();
        expect(tabs.WaitFor(front.Id, 10_000) && tabs.HasState(front.Id), "with unsaved changes, the tick while playing keeps the file (nothing taken on the window's thread)");
        vm.IsPlaying = false;
        tabs.ForgetSession();

        // The wiring: every save of the project raises it, an open or an autosave never; the window hears it from its constructor.
        var project = Source("ViewModels", "MainViewModel.Project.cs");
        var mainSource = Source("MainWindow.xaml.cs");
        var cache = Source("MainWindow.TabCache.cs");
        expect(new[] { "public string SaveProjectTo(", "private string SavePortable(", "public string SaveProject(string name)", "private string WriteBackConverted(" }
                   .All(m => Ordered(MemberOf(project, m), "MarkSaved();", "ProjectSaved?.Invoke();"))
               && project.Split("ProjectSaved?.Invoke();").Length == 5 && !MemberOf(project, "public async Task AutosaveAsync(").Contains("ProjectSaved", StringComparison.Ordinal),
            "MainViewModel.ProjectSaved is raised by Save, Save As, a portable save and a converted project written back (four places), never by the autosave");
        expect(mainSource.Contains("_viewModel.ProjectSaved += ViewModel_ProjectSaved;", StringComparison.Ordinal)
               && MemberOf(cache, "private void ViewModel_ProjectSaved(").Contains("DropFrontRecovery(_tabs.Active)", StringComparison.Ordinal),
            "the window lets the tab in front's recovery file go on it");
        expect(Ordered(MemberOf(mainSource, "private void Autosave_Tick("), "if (_frontRecovery is { Written: true } && !_viewModel.MayHaveUnsavedChanges && !_switchingTab)",
                   "DropFrontRecovery(_tabs.Active);", "if (_viewModel.IsPlaying || _gpu is { IsPlaying: true } || IsScrubHeld || TimelineDragHeld || _switchingTab)", "return;",
                   "if (!FrontRecoveryHeld)", "KeepFrontRecovery();"),
            "the tick: a stale file dropped first (O(1)); the recovery copy never while playing, scrubbing, dragging or switching, nor while held");
        expect(Ordered(cache[cache.IndexOf("private bool FrontRecoveryHeld =>", StringComparison.Ordinal)..], "Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed",
                   "Mouse.Captured is not null", "ComponentDispatcher.IsThreadModal", "_closeAsked;"),
            "held: the left button down or the mouse captured anywhere (the player's handles, the 3D view, a slider), a modal dialog up, or a close under way");
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
