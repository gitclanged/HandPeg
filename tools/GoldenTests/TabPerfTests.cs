using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4D-TabPerf (implementer T): project tabs kept live in memory and swapped on a switch, the still kept fresh, the recovery file
/// written after the switch, the memory valve.
/// <para>tab-perf-hot-swap: on a real view model, a tab opened as a project is (the cold path) with two edits, a selection and its playhead
/// goes behind (DetachForTab: its live Sequence detached, no Committed handler left on it, a frozen copy with its history beside it), a
/// second tab is made and edited without touching it, and it comes back (AttachTab): the very same Sequence, the same undo mark and depth,
/// the same fingerprint and dot, its selection followed into its new views, its file, playhead and Undo going on; and it is what the cold
/// path opens from its frozen copy, fingerprint for fingerprint.</para>
/// <para>tab-perf-late-work: the work started for a tab (I-frames, waveforms, hover sheet: their cancellations; the streams' unpacking:
/// the project generation) is cancelled as it goes behind, so a continuation that comes back after a switch lands nowhere.</para>
/// <para>tab-perf-io-order: each tab's disk work runs one after another on the thread pool (ProjectTabs.Enqueue): a write, a delete and
/// a write keep the last; a read sees the write before it; a failure does not stop the chain; a close deletes after a write under way.</para>
/// <para>tab-perf-valve: which tab the memory valve lets go (pure): over 16 tabs, over the budget or with memory short, the least recently
/// used, never one of the two used last nor one whose file is not written.</para>
/// <para>tab-perf-switch-wiring: the window's code: a switch between live tabs awaits nothing between putting one behind and bringing the
/// other back, reads no cache file, opens nothing and makes no fingerprint; the tab put behind is written after the switch, on its tab's
/// chain; the engine still lets go of it; the still is kept fresh ahead and never awaited.</para>
/// <para>tab-perf-vm-field-audit: every instance field of MainViewModel is classified as the project's (kept by a tab: its context or
/// what CaptureState and the restore put back) or the application's (the same in every tab); a new field fails until it is classified.</para>
/// <para>tab-perf-kept-views: a tab kept live keeps its clips' views (MainViewModel.KeptViews): A put behind, B (a duplicate: the same clip
/// ids) edited, A back with the very same views in the same order, in one change of the views list, then A edited: B's views and Sequence
/// are untouched, nothing done to A's views while behind reached a clip, and B comes back with its own views showing its own clips; a view
/// still loading as its tab went behind is made anew.</para>
/// <para>tab-perf-instant-name: a file opened into a tab with nothing open (RouteOf: OpenOrReplace) names the tab as the open begins, with
/// the very name the open leaves; a failed open puts New Project back; a studio name or a project file still comes first; the name follows
/// the view model's file, project and studio name at once (they are told), playing or not, with no fingerprint.</para>
/// <para>tab-perf-memory-wiring: MemoryHygiene.IsQuiet is set once, lazily, from a tab put behind, brought back, closed or let go (never
/// before the first frame), and a compacting collection is asked for after every close and every tab let go to its file.</para>
/// <para>tab-perf-late-adds (5.4D-TabPerf review, P1 and P2): on a real view model and real files, a sound and a portrait video added to
/// tab A are counted as adds under way (HasPendingAdds, what the window refuses a switch on); A goes behind all the same while their probes
/// run: nothing of them lands in tab B (no clip, no step, no edit); A's context keeps the video still loading with its drop's arguments,
/// and as A comes back it is finished as the drop asked (its file's length, fitted to the frame), not left at its stand-in length and box.
/// The window's drop loops, the voiceover and the beats take the same guard (their source).</para>
/// <para>tab-perf-attach-safety (P3, P4/P7/P17, P12): a tab put behind lets go of its selected view (KeyLayer) and gets it back as it returns;
/// FrozenStateDiffers only reads the frozen copy (a reader on another thread never sees it blanked) and still matches a clean tab's
/// fingerprint; a switch from a tab with a file open to one of dropped files (no file) tells HasSource once, true, never false on the way,
/// and the window never lets go of the engine mid-switch.</para>
/// </summary>
public static class TabPerfTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string First = "D:/media/first.mkv", Second = "D:/media/second.mkv";

    /// <summary>The window-free cases (and those on a window of their own), before the UI-scale tests.</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("tab-perf-hot-swap", "# A project tab kept live in memory: put behind and brought back by a swap of references, exactly as it was, and as the cold path opens it.", HotSwap);
        yield return Case("tab-perf-late-work", "# Work started for a tab and still under way as it goes behind is cancelled: a late continuation never lands in the next tab.", LateWork);
        yield return Case("tab-perf-io-order", "# Each project tab's disk work runs in order on the thread pool: write, delete, write keeps the last; a read sees the write before it.", IoOrder);
        yield return Case("tab-perf-valve", "# The memory valve lets the least recently used live tab go to its cache file: over 16, over the budget or with memory short; never the two used last.", Valve);
        yield return Case("tab-perf-switch-wiring", "# A switch between live tabs awaits nothing, reads no cache file and makes no fingerprint; the tab put behind is written after it.", SwitchWiring);
        yield return Case("tab-perf-vm-field-audit", "# Every MainViewModel instance field is classified as the project's (a tab keeps it) or the application's: a new one fails until it is.", FieldAudit);
        yield return Case("tab-perf-kept-views", "# A tab kept live keeps its clips' views: none made anew as it comes back, never another tab's, the views list filled in one change.", KeptViews);
        yield return Case("tab-perf-instant-name", "# A file opened into a tab with nothing open names the tab at once, with the name the open leaves; a failed open puts the name back.", InstantName);
        yield return Case("tab-perf-memory-wiring", "# The memory hygiene's quiet test is set after the first frame; a compaction is asked for after a tab is closed or let go.", MemoryWiring);
        yield return Case("tab-perf-late-adds", "# Clips still being added as their tab goes behind land in no other tab; a video cut short is finished as its drop asked as its tab comes back.", LateAdds);
        yield return Case("tab-perf-attach-safety", "# A tab put behind lets go of its selected view; its frozen copy is only read; a tab coming back never tells HasSource false on the way.", AttachSafety);
    }

    /// <summary>The cases that need the WPF Application the UI-scale tests make: run last.</summary>
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
        return folder;
    }

    // ----- A view model holding a project, as ProjectTabsTests makes one -----

    /// <summary>A Sequence of dropped files (no file opened, so nothing is probed as it is put back): two clips of one file on track 1, one of another on track 2.</summary>
    private static TimelineDocument Sequence(string one, string two)
    {
        var document = new TimelineDocument { Width = 1920, Height = 1080, NaturalWidth = 1920, NaturalHeight = 1080, FrameRate = FrameRate.Fps30 };
        var a = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        a.Clips.AddRange([DogfoodTests.FileClip(one, 0, 4), DogfoodTests.FileClip(one, 4, 8, 4)]);
        var b = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 2 };
        b.Clips.Add(DogfoodTests.FileClip(two, 0, 6));
        document.Tracks.AddRange([a, b]);
        return document;
    }

    private static MainViewModel NewViewModel()
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports, settings.EnableExtraAnimations, settings.DroppedClipsJoinTrack) = (false, false, false, false);
        return new MainViewModel();
    }

    /// <summary>A view model holding <paramref name="document"/> put in place directly (no open), taken as saved.</summary>
    private static MainViewModel Holding(TimelineDocument document)
    {
        var vm = NewViewModel();
        Install(vm, document);
        vm.MarkSaved();
        return vm;
    }

    private static void Install(MainViewModel vm, TimelineDocument document) => typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [document]);

    private static string Fingerprint(MainViewModel vm) => (string)typeof(MainViewModel).GetMethod("Fingerprint", Hidden)!.Invoke(vm, null)!;

    private static object? Field(MainViewModel vm, string name) => typeof(MainViewModel).GetField(name, Hidden)!.GetValue(vm);

    private static void SetField(MainViewModel vm, string name, object? value) => typeof(MainViewModel).GetField(name, Hidden)!.SetValue(vm, value);

    /// <summary>How many handlers a Sequence's Committed event has (its backing field).</summary>
    private static int Subscribers(TimelineDocument document) =>
        (typeof(TimelineDocument).GetField("Committed", Hidden)!.GetValue(document) as Delegate)?.GetInvocationList().Length ?? 0;

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

    /// <summary>A tab opened from its state the cold way (MainViewModel.ResumeTabAsync, from the window's thread's queue as a click would), waited for.</summary>
    private static void Resume(MainViewModel vm, ProjectState state, string? path, bool dirty, string? saved)
    {
        var resuming = Dispatcher.CurrentDispatcher.InvokeAsync(() => vm.ResumeTabAsync(state, path, dirty, saved)).Task.Unwrap();
        PumpUntil(resuming);
        if (!resuming.IsCompleted)
            throw new TimeoutException("the tab did not come back within 30 s");
        resuming.GetAwaiter().GetResult();
    }

    // ----- tab-perf-hot-swap -----

    private static void HotSwap(Action<bool, string> expect)
    {
        // Tab 1 opened the way a project is (the cold path), then edited: two steps of Undo, a selection, a playhead.
        var opened = Holding(Sequence(First, Second)).CaptureStateForTab("Tab One");
        var vm = NewViewModel();
        Resume(vm, opened, null, false, null);
        expect(vm.HasSource && !vm.HasUnsavedChanges && vm.UndoMark is null, "tab 1: a project opened the cold way, clean, nothing to undo");

        var track = vm.Document.Tracks[0];
        expect(vm.TrimClip(vm.ViewOf(track.Clips[0])!, start: false, seconds: 3) && vm.DeleteClip(vm.ViewOf(track.Clips[1])!), "two edits: the first clip trimmed to 3 s, the second deleted");
        var key = vm.Document.Tracks[1].Clips[0];
        vm.KeyLayer = vm.ViewOf(key);
        vm.PositionMs = 2500;
        var (document, mark, fingerprint, dirty, shape, layers) = (vm.Document, vm.UndoMark, Fingerprint(vm), vm.HasUnsavedChanges, Shape(vm.Document), vm.Layers.Count);
        expect(dirty && mark is not null && vm.KeyLayer?.ClipId == key.Id, "tab 1 has unsaved changes, an undo mark and a clip selected");

        // ----- Tab 1 put behind, kept live -----
        var clock = Stopwatch.StartNew();
        var one = vm.DetachForTab("Tab One");
        var detachMs = clock.Elapsed.TotalMilliseconds;
        expect(ReferenceEquals(one.Document, document) && !ReferenceEquals(vm.Document, document) && vm.Document.Tracks.Count == 0,
            "DetachForTab: the context holds the live Sequence itself; the view model holds an empty one in its place");
        expect(Subscribers(document) == 0 && Subscribers(vm.Document) == 1, "the detached Sequence has no Committed handler (Document is the only timeline); the stand-in has the view model's");
        expect(one.UndoDepthNow == 2 && one.RedoDepthNow == 0 && vm.UndoMark is null && !vm.HasUnsavedChanges,
            "its two steps of Undo moved with it (by reference); the view model keeps none, and nothing unsaved");
        expect(one.MayBeDirty && one.SavedFingerprint.Length > 0, "it may have unsaved changes (the cheap look), its saved state's fingerprint with it");
        expect(!ReferenceEquals(one.Frozen.Timeline, document) && Shape(one.Frozen.Timeline) == shape && one.Frozen.UndoHistory?.Count == 2,
            "its frozen copy (for the cache file) is a copy of its own, the same clips, with its two steps of history");
        expect(MainViewModel.FrozenStateDiffers(one.Frozen, one.SavedFingerprint) && one.Frozen.Name == "Tab One" && one.Frozen.UndoHistory?.Count == 2,
            "the exact dot worked out from the frozen copy (the thread pool's): unsaved; the copy is left as it was (its name and history)");

        // ----- Tab 2: another project, edited: tab 1's Sequence is never touched -----
        Install(vm, Sequence("D:/media/third.mkv", "D:/media/fourth.mkv"));
        vm.MarkSaved();
        expect(vm.DeleteClip(vm.ViewOf(vm.Document.Tracks[1].Clips[0])!) && vm.HasUnsavedChanges, "tab 2: another Sequence, a clip deleted (one step of Undo)");
        expect(Shape(document) == shape && Subscribers(document) == 0, "tab 1's detached Sequence is as it was: nothing done in tab 2 reached it");
        var twoDocument = vm.Document;
        var two = vm.DetachForTab("Tab Two");

        // ----- Tab 1 brought back: a swap of references -----
        one.ProjectPath = "D:/projects/Tab One.hproj";
        clock.Restart();
        var attached = vm.AttachTab(one);
        var attachMs = clock.Elapsed.TotalMilliseconds;
        expect(attached && ReferenceEquals(vm.Document, document) && Subscribers(document) == 1 && Subscribers(twoDocument) == 0,
            "AttachTab: tab 1's very Sequence is the view model's again (its handler back); tab 2's is detached");
        expect(ReferenceEquals(vm.UndoMark, mark) && Shape(vm.Document) == shape && vm.Layers.Count == layers, "the same undo mark (the same packed step), the same clips, as many views");
        expect(Fingerprint(vm) == fingerprint && vm.HasUnsavedChanges == dirty, "the same fingerprint, and its unsaved changes still unsaved (no MarkSaved)");
        expect(vm.KeyLayer?.ClipId == key.Id && vm.Layers.Contains(vm.KeyLayer!), "its selection followed into its new views by the clip's id");
        expect(vm.CurrentProjectPath == "D:/projects/Tab One.hproj" && Math.Abs(vm.PositionMs - 2500) < 0.5, "its project file (a save while behind updates it) and its playhead");
        vm.Undo();
        expect(vm.Document.Tracks[0].Clips.Count == 2 && vm.HasUnsavedChanges, "Undo goes on where it left off: the deleted clip is back, still unsaved");
        vm.Redo();
        expect(Shape(vm.Document) == shape, "and Redo takes it again");

        // ----- The cold path opens its frozen copy the same -----
        var cold = NewViewModel();
        Resume(cold, one.Frozen, one.ProjectPath, dirty: true, one.SavedFingerprint);
        expect(Fingerprint(cold) == Fingerprint(vm) && Shape(cold.Document) == shape && cold.HasUnsavedChanges && cold.UndoMark is not null,
            "the cold path (its cache file's state opened) gives the same project: the same fingerprint, clips, dot and history");

        // ----- Tab 2 brought back in turn, then saved while behind -----
        // (Undo and Redo put a Sequence of their own in place: the one tab 1 holds now.)
        var undone = vm.Document;
        var oneAgain = vm.DetachForTab("Tab One");
        two.MarkSavedOnAttach = true;
        expect(vm.AttachTab(two) && ReferenceEquals(vm.Document, twoDocument) && !vm.HasUnsavedChanges && vm.UndoMark is not null,
            "a tab saved while it was behind (SaveTabInPlace) comes back saved, its history kept");
        expect(ReferenceEquals(oneAgain.Document, undone) && Subscribers(undone) == 0 && Shape(undone) == shape, "and tab 1 is behind again, detached, as Redo left it");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        (a 3-clip tab, headless: DetachForTab {detachMs:0.0} ms, AttachTab {attachMs:0.0} ms; the window adds its handlers and the engine)"));
    }

    // ----- tab-perf-late-work -----

    private static void LateWork(Action<bool, string> expect)
    {
        // A file that is there (so its streams are probed, on the thread pool) but no video: the probe comes back with nothing, later.
        var folder = Fresh("tab-perf-late");
        Directory.CreateDirectory(folder);
        var there = Path.Combine(folder, "not a video.mkv");
        File.WriteAllText(there, "not a video");
        var late = Sequence(there, Second);
        foreach (var clip in late.Tracks[0].Clips)
            clip.AudioStreamIndex = 0;
        var vm = Holding(late);
        var running = new[] { "_iFrameScanCancellation", "_waveformCancellation", "_timelineWaveformCancellation", "_spriteCancellation" }
            .ToDictionary(name => name, _ => new CancellationTokenSource());
        foreach (var (name, cts) in running)
            SetField(vm, name, cts);
        var generation = (int)Field(vm, "_projectGeneration")!;

        // The streams' unpacking under way (its probe of a file that is not there comes back after the switch). Started on the window's
        // dispatcher, as HandPeg starts it: its continuation comes back only as the dispatcher is turned (PumpUntil), never on the pool
        // halfway through the switch (which made this case depend on how fast the probe failed).
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Task unpacking;
        try
        {
            unpacking = (Task)typeof(MainViewModel).GetMethod("UnpackProjectStreamsAsync", Hidden)!.Invoke(vm, null)!;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }

        expect((int)Field(vm, "_unpacking")! == 1, "the streams' unpacking of tab 1 is under way");

        var one = vm.DetachForTab("Tab One");
        expect(running.Values.All(c => c.IsCancellationRequested), "DetachForTab cancels tab 1's I-frame scan, waveforms (each track's and the timeline's) and hover sheet: each drops its result after its wait");
        expect((int)Field(vm, "_projectGeneration")! == generation + 1 && (int)Field(vm, "_unpacking")! == 0, "and moves the project generation on");
        expect(Pending(one, "IFramesPending") && Pending(one, "WaveformsPending") && Pending(one, "SpritePending") && Pending(one, "UnpackPending"),
            "the context marks what was cut short, to start again as the tab comes back");

        Install(vm, Sequence("D:/media/third.mkv", "D:/media/fourth.mkv"));
        vm.MarkSaved();
        var shapeTwo = Shape(vm.Document);
        PumpUntil(unpacking);
        expect(unpacking.IsCompletedSuccessfully && Shape(vm.Document) == shapeTwo && !vm.HasUnsavedChanges && (int)Field(vm, "_unpacking")! == 0,
            "the unpacking's probe came back after the switch: it saw another project's generation and did nothing in tab 2");

        // A late waveform: GenerateWaveformsAsync checks its cancellation after its wait, so its picture never lands as tab 2's.
        var (waveform, timeline) = (running["_waveformCancellation"], running["_timelineWaveformCancellation"]);
        expect(waveform.IsCancellationRequested && timeline.IsCancellationRequested && vm.TimelineWaveform is null, "tab 2's timeline waveform is not tab 1's late one");
    }

    private static bool Pending(MainViewModel.TabProjectContext context, string name) =>
        typeof(MainViewModel.TabProjectContext).GetProperty(name, Hidden)!.GetValue(context) is true;

    // ----- tab-perf-io-order -----

    private static void IoOrder(Action<bool, string> expect)
    {
        var tabs = new ProjectTabs(Fresh("tab-perf-io"), "a", "session");
        var vm = Holding(Sequence(First, Second));
        ProjectState Named(string name)
        {
            var state = vm.CaptureStateForTab(name);
            state.Name = name;
            return state;
        }

        var header = ProjectTabs.HeaderJson(new ProjectTabHeader { Id = "a", Title = "A", Empty = false });
        using var gate = new ManualResetEventSlim();
        var held = tabs.Enqueue("a", () => gate.Wait(10_000));
        _ = tabs.Enqueue("a", () => tabs.WriteTab("a", Named("first"), null, header));
        tabs.DeleteFilesAfterIo("a");
        var last = tabs.Enqueue("a", () => tabs.WriteTab("a", Named("second"), null, header));
        expect(tabs.IsBusyWith("a") && !tabs.HasState("a"), "a tab's chain held: its write, a delete and a write wait their turn (nothing on the disk yet)");
        gate.Set();
        expect(tabs.WaitFor("a", 10_000) && held.IsCompletedSuccessfully && last.IsCompletedSuccessfully && !tabs.IsBusyWith("a"), "let go, the chain runs out in order");
        expect(tabs.HasState("a") && tabs.ReadState("a").Name == "second", "write, delete, write: the last write is what is there");

        _ = tabs.Enqueue("a", () => tabs.WriteTab("a", Named("third"), null, header));
        var read = tabs.Enqueue("a", () => tabs.ReadState("a").Name);
        expect(read.Wait(10_000) && read.Result == "third", "a read asked after a write sees it (ProjectTabs.Enqueue<T>)");

        _ = tabs.Enqueue("a", () => tabs.WriteTab("a", Named("fourth"), null, header));
        tabs.DeleteFilesAfterIo("a");
        expect(tabs.WaitFor("a", 10_000) && !tabs.HasState("a") && !File.Exists(tabs.HeaderFile("a")), "write then delete: nothing is left (a tab brought back keeps no cache file)");

        var failed = tabs.Enqueue("a", () => throw new IOException("the disk said no"));
        var after = tabs.Enqueue("a", () => tabs.WriteTab("a", Named("fifth"), null, header));
        expect(tabs.WaitFor("a", 10_000) && failed.IsFaulted && after.IsCompletedSuccessfully && tabs.ReadState("a").Name == "fifth",
            "a failure is its own task's: the chain goes on after it");

        var b = tabs.Add("B", "b");
        using var slow = new ManualResetEventSlim();
        _ = tabs.Enqueue("b", () => slow.Wait(10_000));
        _ = tabs.Enqueue("b", () => tabs.WriteTab("b", Named("b"), null, ProjectTabs.HeaderJson(b)));
        tabs.Close(b);
        slow.Set();
        expect(tabs.WaitFor(null, 10_000) && !tabs.HasState("b") && !File.Exists(tabs.HeaderFile("b")) && tabs.Find("b") is null,
            "a tab closed while its cache file is still to be written: its files are deleted after the write, never left behind");
        expect(tabs.WaitFor(null, 1000) && tabs.IoOf("a").IsCompleted, "nothing left waiting: WaitFor for every tab returns at once");
        tabs.ForgetSession();
    }

    // ----- tab-perf-valve -----

    private static void Valve(Action<bool, string> expect)
    {
        const long Mb = 1L << 20;
        var tabs = Enumerable.Range(1, 20).Select(i => new ProjectTabHeader { Id = $"t{i}", Title = $"Tab {i}", LastActive = i }).ToList();
        Dictionary<string, long> Kept(int count, long each) => tabs.Take(count).ToDictionary(t => t.Id, _ => each, StringComparer.Ordinal);
        var none = new HashSet<string>(StringComparer.Ordinal);

        expect(MainWindow.TabToLetGo(tabs, Kept(5, 20 * Mb), none, 1024 * Mb, false) is null, "five tabs of 20 MB under a 1 GB budget: none let go");
        expect(MainWindow.TabToLetGo(tabs, new Dictionary<string, long>(), none, 0, true) is null, "nothing kept: nothing to let go");
        expect(MainWindow.TabToLetGo(tabs, Kept(5, 300 * Mb), none, 1024 * Mb, false) == "t1", "five of 300 MB, over the budget: the least recently used (t1) goes");
        expect(MainWindow.TabToLetGo(tabs, Kept(5, 20 * Mb), none, 1024 * Mb, memoryShort: true) == "t1", "under the budget but Windows says memory is short: t1 goes");
        expect(MainWindow.TabToLetGo(tabs, Kept(17, 1 * Mb), none, 1024 * Mb, false) == "t1" && MainWindow.MostLiveTabs == 16, "seventeen kept (more than 16): t1 goes");
        expect(MainWindow.TabToLetGo(tabs, Kept(5, 300 * Mb), new HashSet<string>(["t1", "t2"], StringComparer.Ordinal), 1024 * Mb, false) == "t3",
            "a tab whose cache file could not be written must stay (its work is only in memory): the next least recently used goes");

        // The two used last (the tab in front, t20, and the one before it, t19) are never let go, kept or not.
        var recent = new Dictionary<string, long>(StringComparer.Ordinal) { ["t19"] = 900 * Mb, ["t20"] = 900 * Mb };
        expect(MainWindow.TabToLetGo(tabs, recent, none, 0, true) is null, "the two used last are never let go, whatever the budget or the memory");
        recent["t18"] = 1;
        expect(MainWindow.TabToLetGo(tabs, recent, none, 1024 * Mb, false) == "t18", "with a third over the budget, the third goes");
        expect(new AppSettings().HydratedTabBudgetMb == 1024, "the budget (AppSettings.HydratedTabBudgetMb) is 1 GB as HandPeg comes");
    }

    // ----- tab-perf-switch-wiring -----

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

    private static bool HasAny(string text, params string[] parts) => parts.Any(p => text.Contains(p, StringComparison.Ordinal));

    private static void SwitchWiring(Action<bool, string> expect)
    {
        var tabs = Read("MainWindow.ProjectTabs.cs");
        var switching = Method(tabs, "private async Task SwitchToTabAsync(");
        var put = switching.IndexOf("var behind = PutActiveBehind(out write);", StringComparison.Ordinal);
        var attach = switching.IndexOf("AttachHydrated(tab, live!)", StringComparison.Ordinal);
        expect(put > 0 && attach > put && !switching[put..attach].Contains("await ", StringComparison.Ordinal)
               && InOrder(switching, "var behind = PutActiveBehind(out write);", "_tabs.Activate(tab);", "ShowTabs();", "ApplyTabLayout(tab);", "_hydrated.TryGetValue(tab.Id, out var live)", "AttachHydrated(tab, live!)", "else", "await ResumeTabAsync(tab, behind);"),
            "SwitchToTabAsync: the tab in front put behind, the next in front, its layout, then a live tab attached at once: no await between putting one behind and bringing the other back");
        expect(InOrder(switching, "AttachHydrated(tab, live!)", "RefreshActiveTab(fingerprint: !hot);", "finally", "_switchingTab = false;", "QueueTabWrite(write);"),
            "the tab put behind's cache file is queued after the switch (in its finally, once the next tab is in front), and a live switch makes no fingerprint");

        var behind = Method(tabs, "private ProjectTabHeader PutActiveBehind(") + Method(tabs, "private TabViewContext DetachActive(") + Method(tabs, "private void TakeHeaderForTab(");
        var front = Method(tabs, "private bool AttachHydrated(");
        string[] cold = ["ReadState(", "ReadSaved(", "ResumeTabAsync(", "RestoreStateAsync(", "MarkSaved(", "HasUnsavedChanges", "CaptureThumbnailAsync(", "await ", "Task.Run(", ".Wait("];
        expect(behind.Length > 0 && front.Length > 0 && !HasAny(behind, cold) && !HasAny(front, cold),
            "putting a tab behind and bringing a live one back read no cache file, open nothing, make no fingerprint, capture no still and wait for nothing");
        expect(InOrder(Method(tabs, "private ProjectTabHeader PutActiveBehind("), "var live = DetachActive(tab, out write);", "_hydrated[tab.Id] = live;", "LeaveExplodedAtOnce();", "_gpu?.Suspend();", "ClearProgramStrip();"),
            "the tab put behind is kept live, its 3D view left, and the engine lets go of it (GpuPreviewEngine.Suspend, as before)");
        expect(InOrder(front, "ShowTabStill(live.Still);", "_viewModel.AttachTab(project)", "_hydrated.Remove(tab.Id);", "live.Write?.Cancel();", "DeleteTabFiles(tab.Id);", "AdoptProgramSheet(sheet, live.ProgramSheetMark);",
                   "RestoreExpansion(live.Expansion);", "RestorePlayerView(live.PlayerView);", "RestoreTabView(tab);"),
            "AttachHydrated: its still shown first, its project swapped in, its cache file let go after any write, its sheet, open stacks and player view back, its view restored inline (no Background wait)");
        expect(InOrder(Method(tabs, "private void QueueTabWrite("), "_unwritten.Add(write);", "_tabs.Enqueue(write.Id, () => WriteTabNow(write));")
               && InOrder(Method(tabs, "private void WriteTabNow("), "MainViewModel.FrozenStateDiffers(frozen, project.SavedFingerprint)", "_tabs.WriteTab(write.Id, frozen,", "Dispatcher.BeginInvoke(() => TabWritten(write)"),
            "the write runs on the tab's own chain on the thread pool, from its frozen copy, the exact dot worked out there and told back after");
        expect(InOrder(Method(tabs, "private async Task<bool> ResumeTabAsync("), "await _tabs.Enqueue(id, () => (_tabs.ReadState(id), _tabs.ReadSaved(id)));", "DeleteTabFiles(id);"),
            "a tab only in its file is read after any of its writes still under way, and its files deleted on the same chain");
        expect(InOrder(Method(tabs, "private bool MayCloseProjectTabs("), "FinishTabWrites();", "PromptToSaveOnExit") && InOrder(Method(tabs, "private void ForgetProjectTabs("), "FinishTabWrites();", "_tabs.ForgetSession();"),
            "closing HandPeg waits for every tab's disk work (5 s at most) before asking about or deleting any tab's files");
        expect(InOrder(Method(tabs, "private void HookTabCrashWrites("), "AppDomain.CurrentDomain.UnhandledException +=", "if (e.IsTerminating)", "WriteUnwrittenTabs();")
               && InOrder(Method(tabs, "private void WriteUnwrittenTabs("), "if (!write.IsWanted)", "_tabs.WriteTab(write.Id, project.Frozen,"),
            "falling over, the cache files still to be written are written from their frozen copies (a closed tab's never)");

        var still = Read("MainWindow.TabStill.cs");
        expect(InOrder(Method(still, "private async Task TakeLiveStillAsync("), "if (_viewModel.IsPlaying || _switchingTab || TimelineDragHeld", "IsThumbnailWaiting: false",
                   "if (_liveStill is not null && _liveStillMark == mark)", "await engine.CaptureThumbnailAsync(", "if (asked != _stillAsked || _switchingTab || !ReferenceEquals(front, _tabs.Active)")
               && InOrder(Method(still, "private void NoteStillStale()"), "if (_stillSettle is null || _viewModel.IsPlaying)", "_stillSettle.Start();")
               && still.Contains("private const int StillSettleMs = 350;", StringComparison.Ordinal),
            "the still is kept fresh 350 ms after the picture last changed, only while paused, not dragging and not switching, never in place of a save's thumbnail, never re-armed while playing");
        expect(InOrder(Method(tabs, "private void ShowTabs("), "ProjectTabStrip.Visibility = Visibility.Visible;", "KeepTabStillFresh();")
               && !Method(tabs, "private void InitializeProjectTabs(").Contains("KeepTabStillFresh", StringComparison.Ordinal),
            "it is hooked as the strip is first shown, never before the first frame (Law 1)");

        var vm = Read("ViewModels", "MainViewModel.Tabs.cs");
        var attachTab = Method(vm, "public bool AttachTab(");
        expect(attachTab.Length > 0 && !HasAny(attachTab, "LoadMediaAsync(", "RestoreStateAsync(", "TimelineToOpen(", "TakeUpHistory(", "MediaLoaded", "RunOperationAsync(", "ConvertLegacy(", "Fingerprint()", "await ")
               && InOrder(attachTab, "ApplyProjectSettings(state, legacyMixing: false);", "InstallProjectTimeline(state, tab.Document, tab.Views);", "if (tab.MarkSavedOnAttach)", "MarkSaved();"),
            "MainViewModel.AttachTab: no probe, open, deep clone, history read or MediaLoaded; the same steps an open takes on the tab's own Sequence; a fingerprint only for a tab saved while behind");
        var restore = Method(Read("ViewModels", "MainViewModel.Project.cs"), "public Task RestoreStateAsync(");
        expect(InOrder(restore, "ApplyProjectSettings(state, legacyMixing: oldShape is not null);", "TimelineToOpen(state);", "InstallProjectTimeline(state, timeline);", "TakeUpHistory(state, converted);", "MarkSaved();"),
            "the cold path takes the very same steps (ApplyProjectSettings, InstallProjectTimeline) around its file's own");
    }

    // ----- tab-perf-kept-views -----

    /// <summary>Each clip's view, by the clip's id.</summary>
    private static Dictionary<Guid, Layer> ViewsOf(MainViewModel vm) => vm.Document.Tracks.SelectMany(t => t.Clips).ToDictionary(c => c.Id, c => vm.ViewOf(c)!);

    /// <summary>What views show of their clips: their timing and a little of their look, to compare before and after.</summary>
    private static string Showing(IEnumerable<Layer> views) =>
        string.Join(" | ", views.Select(v => FormattableString.Invariant($"{v.ClipId} {v.StartTime:0.###}+{v.Duration:0.###} {v.Opacity} {v.PositionX:0.####}")));

    private static void KeptViews(Action<bool, string> expect)
    {
        // Tab A, and tab B a duplicate of it (TimelineDocument.Clone: the same clip ids, as a Duplicate's are).
        var vm = Holding(Sequence(First, Second));
        var aDocument = vm.Document;
        var aViews = ViewsOf(vm);
        var aOrder = vm.Layers.ToList();
        var aShowing = Showing(aOrder);
        var changes = 0;
        vm.Layers.CollectionChanged += (_, _) => changes++;

        var one = vm.DetachForTab("A");
        expect(vm.Layers.Count == 0 && aDocument.Tracks.SelectMany(t => t.Clips).All(c => vm.ViewOf(c) is null) && vm.SelectedClips.Count == 0,
            "A put behind: its views are in no list or map of the view model (the views list emptied with its Sequence)");

        var bDocument = aDocument.Clone(deep: true);
        changes = 0;
        Install(vm, bDocument);
        vm.MarkSaved();
        var bViews = ViewsOf(vm);
        expect(changes == 1 && bViews.Count == aViews.Count && bViews.All(p => aViews.ContainsKey(p.Key) && !ReferenceEquals(p.Value, aViews[p.Key])),
            "B (the same clip ids) has views of its own, made afresh and put in the views list in one change");

        // A look set on one of A's views while A is behind reaches no clip: not B's of the same id, not A's.
        var bFingerprint = Fingerprint(vm);
        var touched = aViews.Values.First();
        var opacity = touched.Opacity;
        touched.Opacity = 37;
        expect(Fingerprint(vm) == bFingerprint && bDocument.Tracks.SelectMany(t => t.Clips).All(c => c.State.Opacity != 37),
            "a view of A changed while A is behind is listened to by nothing: B's clip of the same id is untouched");
        touched.Opacity = opacity;

        var aShape = Shape(aDocument);
        expect(vm.TrimClip(vm.ViewOf(bDocument.Tracks[0].Clips[0])!, start: false, seconds: 3) && Shape(aDocument) == aShape && Showing(aOrder) == aShowing,
            "an edit in B (a trim) leaves A's Sequence and A's views as they were");
        var bOrder = vm.Layers.ToList();
        var bShowing = Showing(bOrder);
        var bShape = Shape(bDocument);
        var two = vm.DetachForTab("B");

        changes = 0;
        expect(vm.AttachTab(one), "A brought back");
        expect(ViewsOf(vm).All(p => ReferenceEquals(p.Value, aViews[p.Key])) && vm.Layers.SequenceEqual(aOrder) && Showing(vm.Layers) == aShowing,
            "A comes back with its very own views (none made anew), in the same order, showing what they showed");
        expect(changes == 1, "the views list filled in one change (one Reset), not one insert and one removal for each view");

        var first = aDocument.Tracks[0].Clips[0];
        expect(vm.TrimClip(vm.ViewOf(first)!, start: false, seconds: 2) && Math.Abs(first.Duration - 2) < 1e-6 && Math.Abs(aViews[first.Id].Duration - 2) < 1e-6,
            "A's views are listened to again: a trim through one is an edit of A's clip, and its view shows it");
        expect(Shape(bDocument) == bShape && Showing(bOrder) == bShowing && bOrder.All(v => !vm.Layers.Contains(v)),
            "B's Sequence and B's views are untouched by the edit in A (the same clip ids): no view points at another tab's clip");

        // B's first view still loading as B went behind (what looked at its file was the old tab's): made anew as B comes back.
        var loadingId = bDocument.Tracks[0].Clips[0].Id;
        var loading = two.Views!.Views[loadingId];
        loading.IsLoading = true;
        _ = vm.DetachForTab("A");
        expect(vm.AttachTab(two), "B brought back");
        var bAgain = ViewsOf(vm);
        expect(bAgain.Where(p => p.Key != loadingId).All(p => ReferenceEquals(p.Value, bViews[p.Key])) && !ReferenceEquals(bAgain[loadingId], loading),
            "B comes back with its own views, but the one still loading as it went behind is made anew");
        expect(bDocument.Tracks.SelectMany(t => t.Clips).All(c => vm.ViewOf(c) is { } v && Math.Abs(v.StartTime - c.Start) < 1e-9 && Math.Abs(v.Duration - c.Duration) < 1e-9)
               && Shape(vm.Document) == bShape && ReferenceEquals(vm.Document, bDocument),
            "and every view of B shows B's own clip (its trim to 3 s), not A's of the same id");
    }

    // ----- tab-perf-instant-name -----

    private static void InstantName(Action<bool, string> expect)
    {
        const string Clip = "D:/media/My Clip.mkv";
        expect(MainWindow.RouteOf(false, false, false, false, false, false, false, [Clip]) == MainWindow.DropRoute.OpenOrReplace,
            "a video dropped on a blank + tab is opened into it (RouteOf: OpenOrReplace, LoadDroppedFile)");
        var opening = MainWindow.OpeningTabName(Clip, hasSource: false, projectPath: "", unresumed: false);
        static string? NoFile() => null;
        expect(opening == "My Clip" && MainWindow.TabTitleOf("", "", hasSource: false, opening, NoFile) == "My Clip",
            "the tab is named for the file as its open begins, before the file is probed or open");
        expect(MainWindow.TabTitleOf("", "", hasSource: true, null, () => Clip) == "My Clip",
            "once it is open, the name is the very same (the file's): nothing is renamed as the open ends");
        expect(MainWindow.TabTitleOf("", "", hasSource: false, null, NoFile) == "New Project", "an open that failed puts New Project back (the name the + tab had)");
        expect(MainWindow.TabTitleOf("", "Studio Name", hasSource: false, opening, NoFile) == "Studio Name"
               && MainWindow.TabTitleOf("", "Studio Name", hasSource: true, null, () => Clip) == "Studio Name",
            "a name the New Project studio gave comes first, before and after the open, exactly as before");
        expect(MainWindow.TabTitleOf("D:/projects/Edit.hproj", "", hasSource: true, null, () => Clip) == "Edit", "a project file's name comes first of all");
        expect(MainWindow.OpeningTabName(Clip, hasSource: true, "", false) is null && MainWindow.OpeningTabName(Clip, false, "D:/projects/Edit.hproj", false) is null
               && MainWindow.OpeningTabName(Clip, false, "", unresumed: true) is null && MainWindow.OpeningTabName("https://example.com/watch?v=1", false, "", false) is null,
            "no name is taken with a video open or a project file (they name the tab), in a tab not brought back (new work gets a tab of its own), nor from a link");

        // The view model tells the four names the window refreshes the title on.
        var vm = NewViewModel();
        var told = new List<string>();
        vm.PropertyChanged += (_, e) => told.Add(e.PropertyName ?? "");
        vm.KeepNewProjectName("Studio Name", null);
        _ = vm.RenameProjectFile("D:/projects/Renamed.hproj");
        expect(told.Contains(nameof(MainViewModel.NewProjectName)) && told.Contains(nameof(MainViewModel.CurrentProjectPath)),
            "NewProjectName and CurrentProjectPath are told when they change (HasSource and LocalMediaPath were)");
        told.Clear();
        _ = vm.RenameProjectFile("D:/projects/Renamed.hproj");
        expect(!told.Contains(nameof(MainViewModel.CurrentProjectPath)), "and not when they are set to what they are");

        var tabs = Read("MainWindow.ProjectTabs.cs");
        var handler = Method(tabs, "private void InitializeProjectTabs(");
        expect(InOrder(handler, "nameof(MainViewModel.HasSource) or nameof(MainViewModel.LocalMediaPath) or nameof(MainViewModel.CurrentProjectPath)",
                   "or nameof(MainViewModel.NewProjectName))", "RefreshTabTitle();"),
            "the window refreshes the tab's name at once on a change of any of the four");
        var refresh = Method(tabs, "private void RefreshTabTitle(");
        expect(refresh.Length > 0 && !HasAny(refresh, "HasUnsavedChanges", "Fingerprint", "IsPlaying", "_tabDirty")
               && InOrder(refresh, "if (_switchingTab || _unresumed.Contains(tab.Id))", "tab.Title = title;", "ShowTabs();"),
            "the name only: no fingerprint or dot, playing or not, never mid-switch or for a tab not brought back");
        expect(InOrder(Method(tabs, "private bool NameTabForOpen("), "OpeningTabName(file, _viewModel.HasSource, _viewModel.CurrentProjectPath,", "_openingName = (_tabs.Active.Id, name);", "RefreshTabTitle();")
               && InOrder(Method(tabs, "private void EndTabOpening("), "_openingName = null;", "RefreshTabTitle();")
               && InOrder(Method(tabs, "private ProjectTabHeader PutActiveBehind("), "_openingName = null;", "tab.Title = ActiveTabTitle();", "var live = DetachActive(tab, out write);"),
            "the name is taken as the open begins and let go as it ends (the view model's name then); a tab put behind meanwhile keeps no name of an open not made in it");

        var drops = Read("MainWindow.Drops.cs");
        expect(InOrder(Method(drops, "private async Task OpenNamingTabAsync("), "var named = NameTabForOpen(file);", "await open();", "finally", "if (named)", "EndTabOpening();"),
            "OpenNamingTabAsync: named before the open, the name put right in its finally (a failed or faulted open too)");
        expect(InOrder(Method(drops, "private void LoadDroppedFile("), "if (_viewModel.HasSource)", "_viewModel.LoadFile(file);", "return;", "OpenNamingTabAsync(file, () => _viewModel.LoadFileAsync(file))")
               && drops.Contains("await OpenNamingTabAsync(file, () => _viewModel.LoadFileAsync(file));", StringComparison.Ordinal)
               && drops.Contains("await OpenNamingTabAsync(first, () => _viewModel.LoadFileWithStyleAsync(first, style));", StringComparison.Ordinal),
            "every open into a tab with nothing open goes through it: a drop (OpenOrReplace, several files), the Media pane and File > Open (LoadDroppedFile), a drop on a Game Preset");
    }

    // ----- tab-perf-memory-wiring -----

    private static void MemoryWiring(Action<bool, string> expect)
    {
        var sources = Directory.GetFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f).ReplaceLineEndings("\n"))).ToList();
        static int Count(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
        var tabs = sources.Single(s => s.Name == "MainWindow.ProjectTabs.cs").Text;

        expect(sources.Sum(s => Count(s.Text, "MemoryHygiene.IsQuiet =")) == 1 && Count(Method(tabs, "private void HookTabMemory("), "MemoryHygiene.IsQuiet =") == 1,
            "MemoryHygiene.IsQuiet is set in one place only (HookTabMemory), once (_memoryHooked)");
        expect(sources.Where(s => s.Name != "MainWindow.ProjectTabs.cs").All(s => !HasAny(s.Text, "HookTabMemory(", "KeptTabsChanged(", "CompactWhenQuiet("))
               && Count(tabs, "HookTabMemory();") == 2 && InOrder(Method(tabs, "private void KeptTabsChanged("), "HookTabMemory();")
               && InOrder(Method(tabs, "private void CompactWhenQuiet("), "HookTabMemory();", "MemoryHygiene.RequestCompactOnce(reason);"),
            "it is hooked only as tabs kept live change or a compaction is asked for, never from the window's start (Law 1: after the first frame)");
        expect(!HasAny(Method(tabs, "private void InitializeProjectTabs("), "HookTabMemory", "KeptTabsChanged", "CompactWhenQuiet", "MemoryHygiene"),
            "nothing of it in InitializeProjectTabs (before the first frame)");
        expect(InOrder(Method(tabs, "private bool QuietForMemory("), "!_viewModel.IsPlaying", "!_switchingTab", "!TimelineDragHeld", "!_viewModel.IsRecording"),
            "quiet: nothing playing, switching, dragged on the Timeline or recording");

        var close = Method(tabs, "private async Task CloseProjectTabAsync(");
        const string Closed = "CompactWhenQuiet(\"a project tab closed\");";
        expect(Count(close, Closed) == 3
               && InOrder(close, "_tabs.Close(tab);", Closed, "ForgetKeptTab(tab.Id);", "DeleteTabFiles(tab.Id);", Closed, "_tabs.Close(tab);", "finally", "_switchingTab = false;", Closed),
            "a compaction is asked for after each way a tab is closed (behind, the last one emptied, the one in front), once it is let go and the switch is over");
        expect(InOrder(Method(tabs, "private async void LetGoForMemory("), "_hydrated.Remove(id);", "KeptTabsChanged();", "CompactWhenQuiet(\"a project tab let go to its cache file\");"),
            "and after a tab kept live is let go to its cache file (the valve)");
    }

    // ----- tab-perf-late-adds (5.4D-TabPerf review, P1 and P2) -----

    // The window's thread turned while busy holds (or 30 s have gone).
    private static void PumpWhile(Func<bool> busy)
    {
        var until = Environment.TickCount64 + 30_000;
        while (busy() && Environment.TickCount64 < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            if (busy())
                Thread.Sleep(5);
        }
    }

    /// <summary>Runs <paramref name="work"/> on the window's dispatcher's context, as HandPeg runs it: its continuations come back only as the dispatcher is turned.</summary>
    private static T OnDispatcher<T>(Func<T> work)
    {
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            return work();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    }

    private static void LateAdds(Action<bool, string> expect)
    {
        if (GpuExportTests.FindFfmpeg() is not { } ffmpeg)
        {
            expect(false, "an FFmpeg to make the files with (HANDPEG_FFMPEG)");
            return;
        }

        // A portrait video (3 s: fitted, it is narrower than the frame) and a sound, made for the test and probed for real.
        var folder = Fresh("tab-perf-adds");
        Directory.CreateDirectory(folder);
        var (video, sound) = (Path.Combine(folder, "portrait.mp4"), Path.Combine(folder, "sound.wav"));
        GpuExportTests.FfmpegOnce(ffmpeg, $"-f lavfi -i \"testsrc2=s=180x320:r=30:d=3\" -c:v libx264 -pix_fmt yuv420p -g 30 -an -y \"{video}\"");
        GpuExportTests.FfmpegOnce(ffmpeg, $"-f lavfi -i \"sine=frequency=440:duration=2\" -y \"{sound}\"");
        var settings = AppSettings.Current;
        var (probe, sizing) = (settings.FfprobePath, settings.NewClipSizing);
        (settings.FfprobePath, settings.NewClipSizing) = (Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"), AppSettings.NewClipFit);
        try
        {
            // ----- Tab A: a sound and a video added, their probes running -----
            var vm = Holding(Sequence(First, Second));
            var aDocument = vm.Document;
            var aShape = Shape(aDocument);
            var (addingSound, addingVideo) = OnDispatcher(() => (vm.AddAudioLayerAsync(sound, 1), vm.AddVideoClipAsync(video, 2, null)));
            expect(vm.HasPendingAdds && vm.PendingAdds == 2 && !addingSound.IsCompleted && !addingVideo.IsCompleted,
                "two adds under way while their files are looked at (HasPendingAdds: the window refuses a switch meanwhile)");
            var placeholder = aDocument.Tracks.SelectMany(t => t.Clips).SingleOrDefault(c => c.SourcePath == video);
            expect(placeholder is not null && Math.Abs(placeholder.Start - 2) < 1e-6 && Math.Abs(placeholder.End - 7) < 1e-6 && vm.ViewOf(placeholder) is { IsLoading: true },
                "the video is on A's timeline at once, loading, at its stand-in length (2 s to 7 s)");
            var aMark = vm.UndoMark;

            // ----- A put behind all the same (what slips past the refusal), B in front -----
            var one = vm.DetachForTab("A");
            expect(placeholder is not null && one.LoadingAdds.TryGetValue(placeholder.Id, out var kept) && kept.PlayOnce && kept.FitInto is { Width: > 0, Height: > 0 }
                   && !kept.Fill && kept.SourceRange is null && one.LoadingAdds.Count == 1,
                "A's context keeps the video still loading, with its drop's arguments (plays once, fitted inside the frame)");
            Install(vm, Sequence("D:/media/third.mkv", "D:/media/fourth.mkv"));
            vm.MarkSaved();
            var (bDocument, bShape) = (vm.Document, Shape(vm.Document));
            PumpWhile(() => !addingSound.IsCompleted || !addingVideo.IsCompleted);
            expect(addingSound.IsCompletedSuccessfully && !addingSound.Result && addingVideo.IsCompletedSuccessfully && addingVideo.Result is null,
                "both probes came back after the switch: the adds placed nothing (false, null)");
            expect(ReferenceEquals(vm.Document, bDocument) && Shape(bDocument) == bShape && vm.UndoMark is null && !vm.HasUnsavedChanges,
                "tab B has no clip, no step and no edit of A's (the sound is not placed in B, the video not filled in there)");
            expect(!vm.HasPendingAdds, "and no add is under way any more");
            expect(placeholder is not null && Math.Abs(placeholder.End - 7) < 1e-6 && placeholder.SourceDuration <= 0 && one.UndoDepthNow == 1
                   && Shape(aDocument) != aShape && aDocument.Tracks.SelectMany(t => t.Clips).All(c => c.SourcePath != sound),
                "A's Sequence has its video as it was put behind (still the stand-in, one step: the add) and no sound");

            // ----- A back: the video finished as its drop asked -----
            _ = vm.DetachForTab("B");
            var attached = OnDispatcher(() => vm.AttachTab(one));
            var view = placeholder is null ? null : vm.ViewOf(placeholder);
            expect(attached && ReferenceEquals(vm.Document, aDocument) && view is { IsLoading: true } && vm.HasPendingAdds,
                "A comes back: its video's view made anew, loading, its add under way again");
            PumpWhile(() => vm.HasPendingAdds);
            view = placeholder is null ? null : vm.ViewOf(placeholder);
            var wide = vm.FrameWidth > 0 && vm.FrameHeight > 0 ? vm.FrameHeight * 180.0 / 320 / vm.FrameWidth : 0;
            expect(placeholder is not null && view is { IsLoading: false } && Math.Abs(placeholder.Start - 2) < 1e-6 && Math.Abs(placeholder.Duration - 3) < 0.1,
                "the video is filled in: from 2 s for its file's length (3 s), as a drop of it plays once, not its 5 s stand-in");
            expect(view is not null && wide > 0 && Math.Abs(view.SizeWidth - wide) < 0.01,
                "and fitted inside the frame at its own portrait shape, not the stand-in's full width");
            expect(ReferenceEquals(vm.UndoMark, aMark), "with no step of its own (the drop's step is the one)");
        }
        finally
        {
            (settings.FfprobePath, settings.NewClipSizing) = (probe, sizing);
        }

        // ----- The window's adds and the view model's other awaits take the same guard -----
        var drops = Read("MainWindow.Drops.cs");
        expect(InOrder(Method(drops, "private async Task AddDroppedAudioAsync("), "using var adding = _viewModel.BeginAdd();", "var generation = _viewModel.ProjectGeneration;",
                   "if (generation != _viewModel.ProjectGeneration)", "await _viewModel.AddAudioLayerAsync(")
               && InOrder(Method(drops, "private async Task AddDroppedLayersAsync("), "using var adding = _viewModel.BeginAdd();", "var generation = _viewModel.ProjectGeneration;",
                   "if (generation != _viewModel.ProjectGeneration)", "await _viewModel.AddVideoClipAsync(", "if (generation != _viewModel.ProjectGeneration)", "if (added > 1)"),
            "a drop's files: an add under way from the start, the loop stopped once the tab it was dropped on has gone behind");
        var audio = Read("ViewModels", "MainViewModel.Audio.cs");
        expect(InOrder(Method(audio, "private async Task StopVoiceoverAsync("), "using var placing = BeginAdd();", "(IsRecording, IsRecordingPaused) = (false, false);", "await PlaceRecordingAsync();")
               && InOrder(Method(audio, "private async Task PlaceRecordingAsync("), "var generation = _projectGeneration;", "await AddAudioLayerAsync(", "generation != _projectGeneration")
               && InOrder(Method(audio, "private async Task LoadVoiceoverAsync("), "using var adding = BeginAdd();", "await MediaProbe.ProbeAsync(", "if (generation != _projectGeneration)",
                   "await Waveforms.RenderAsync(", "if (generation != _projectGeneration)", "VoiceoverWaveform = waveform;"),
            "a voiceover take: an add under way from the moment its recording ends (IsRecording false) until it is placed, and placed in no other tab");
        expect(InOrder(Method(Read("ViewModels", "MainViewModel.AudioSuite.cs"), "public async Task<bool> DetectClipBeatsAsync("), "_projectGeneration);", "using var adding = BeginAdd();",
                   "await Task.Run(", "if (generation != _projectGeneration)", "Edit($\"detect the beats of {name}\""),
            "the beats of a clip: an add under way while they are found, and kept on no clip of another tab");
        var creator = Read("ViewModels", "MainViewModel.Creator.cs");
        expect(InOrder(Method(creator, "private async Task<bool> CompleteVideoLayerAsync("), "_projectGeneration);", "await Task.Run(", "if (generation != _projectGeneration)", "Document.FindClip(clipId)")
               && InOrder(Method(creator, "public async Task<bool> AddAudioLayerAsync("), "var generation = _projectGeneration;", "using (BeginAdd())", "await MediaProbe.ProbeAsync(", "if (generation != _projectGeneration)", "PlaceLayer("),
            "a video filled in and a sound placed only in the project they were added to");
    }

    // ----- tab-perf-attach-safety (5.4D-TabPerf review, P3, P4/P7/P17, P12) -----

    private static void AttachSafety(Action<bool, string> expect)
    {
        // P3: the selected view let go of as its tab goes behind, and taken up again as it comes back.
        var vm = Holding(Sequence(First, Second));
        expect(vm.TrimClip(vm.ViewOf(vm.Document.Tracks[0].Clips[0])!, start: false, seconds: 3), "tab A: one edit (a step of Undo for its history), then a clip selected");
        var key = vm.Document.Tracks[1].Clips[0];
        vm.KeyLayer = vm.ViewOf(key);
        var selected = vm.KeyLayer;
        var one = vm.DetachForTab("Tab One");
        expect(vm.KeyLayer is null && ReferenceEquals(one.KeyLayer, selected), "DetachForTab: the Inspector and Keyframes pane let go of A's selected view (KeyLayer null); A's context keeps it");

        // P4/P7/P17: the frozen copy is only read, whatever thread works out the dot.
        var frozen = one.Frozen;
        var (name, settingsName, savedAt, undo) = (frozen.Name, frozen.Settings.Name, frozen.SavedAt, frozen.UndoHistory);
        var seen = 0;
        var working = Task.Run(() =>
        {
            var differs = true;
            for (var i = 0; i < 200; i++)
                differs &= MainViewModel.FrozenStateDiffers(frozen, one.SavedFingerprint);
            return differs;
        });
        while (!working.IsCompleted)
        {
            if (frozen.Name != name || frozen.Settings.Name != settingsName || frozen.SavedAt != savedAt || !ReferenceEquals(frozen.UndoHistory, undo))
                seen++;
        }

        expect(working.Result && seen == 0 && name == "Tab One" && undo is { Count: 1 },
            "FrozenStateDiffers on the thread pool (200 times): unsaved, and a reader on this thread never saw the frozen copy's name, Settings name, time or history blanked");
        var tabs = Read("ViewModels", "MainViewModel.Tabs.cs");
        expect(!HasAny(tabs, "frozen.Name =", "(frozen.Name, frozen.Settings.Name", "frozen.UndoHistory =", "frozen.ThumbnailJpeg ="),
            "and it writes to no field of it (MainViewModel.Tabs.cs)");
        var clean = Holding(Sequence(First, Second));
        var cleanTab = clean.DetachForTab("Clean Tab");
        expect(!MainViewModel.FrozenStateDiffers(cleanTab.Frozen, cleanTab.SavedFingerprint) && cleanTab.Frozen.Name == "Clean Tab" && cleanTab.Frozen.Settings.Name == "Clean Tab",
            "a clean tab's frozen copy reads as its saved fingerprint (the name, Settings name and time read blank, as a fingerprint has them), and keeps its name");

        expect(vm.AttachTab(one) && ReferenceEquals(vm.KeyLayer, selected) && vm.KeyLayer?.ClipId == key.Id, "A back: its selected view is the Inspector's again");

        // P12: from a tab with a file open to one of dropped files (no file): HasSource told once, true, never false on the way.
        var b = Holding(Sequence("D:/media/third.mkv", "D:/media/fourth.mkv"));
        var dropped = b.DetachForTab("B");
        Install(b, Sequence(First, Second));
        b.LocalMediaPath = First;
        b.MarkSaved();
        _ = b.DetachForTab("A");
        var told = new List<bool>();
        b.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HasSource))
                told.Add(b.HasSource);
        };
        expect(b.AttachTab(dropped) && b.LocalMediaPath.Length == 0 && b.HasSource, "B (dropped files, no file open) comes back after A (a file open)");
        expect(told.Count == 1 && told[0], "HasSource told once, as B's Sequence is in place, and true (told: " + string.Join(", ", told) + ")");

        var window = Read("MainWindow.xaml.cs");
        expect(window.Contains("if (e.PropertyName == nameof(MainViewModel.HasSource) && !_viewModel.HasSource && !_switchingTab)", StringComparison.Ordinal),
            "and the window never lets go of the engine (UnloadGpu, which clears every live tab's frames) on a HasSource false mid-switch");
        expect(InOrder(Method(tabs, "public bool AttachTab("), "_holdingSource = true;", "LocalMediaPath = tab.LocalMediaPath;", "ApplyProjectSettings(state, legacyMixing: false);",
                   "InstallProjectTimeline(state, tab.Document, tab.Views);", "finally", "ReleaseSourceNote();"),
            "AttachTab holds HasSource from its file set until its Sequence is in place");
    }

    // ----- tab-perf-vm-field-audit -----

    private static void FieldAudit(Action<bool, string> expect)
    {
        var fields = typeof(MainViewModel).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var both = ProjectFields.Intersect(AppFields).ToList();
        var unknown = fields.Where(f => !ProjectFields.Contains(f) && !AppFields.Contains(f)).ToList();
        var gone = ProjectFields.Concat(AppFields).Where(f => !fields.Contains(f)).ToList();
        expect(unknown.Count == 0, "every MainViewModel instance field is classified (unclassified: " + (unknown.Count == 0 ? "none" : string.Join(", ", unknown)) + ")");
        expect(both.Count == 0, "none is both the project's and the application's (" + (both.Count == 0 ? "none" : string.Join(", ", both)) + ")");
        expect(gone.Count == 0, "and the lists name no field that is gone (" + (gone.Count == 0 ? "none" : string.Join(", ", gone)) + ")");
    }

    // The project's: kept by a tab put behind (MainViewModel.TabProjectContext), or put back as it comes back by what an open puts back
    // (ApplyProjectSettings, InstallProjectTimeline, SetMediaInfo, ResetForNewSource), or set again by the window (the playhead).
    private static readonly HashSet<string> ProjectFields = new(StringComparer.Ordinal)
    {
        "<AudioClipRows>k__BackingField", "<AudioRows>k__BackingField", "<AudioTracks>k__BackingField", "<CaptionLayer>k__BackingField",
        "<CaptionStyle>k__BackingField", "<ConvertedFrom>k__BackingField", "<CurrentProjectPath>k__BackingField", "<Document>k__BackingField",
        "<EdgeTuning>k__BackingField", "<EdgeTuningLayer>k__BackingField", "<IFrameMarks>k__BackingField", "<IFrames>k__BackingField",
        "<LastAudioOnlyContainer>k__BackingField", "<LayerRows>k__BackingField", "<Layers>k__BackingField", "<Markers>k__BackingField",
        "<NewProjectFolder>k__BackingField", "<NewProjectName>k__BackingField", "<OutputInfoRows>k__BackingField", "<RecycleBin>k__BackingField",
        "<SelectedLaneKeys>k__BackingField", "<SourceInfoRows>k__BackingField", "<SpriteIntervalSeconds>k__BackingField",
        "<SubtitleCues>k__BackingField", "<SubtitleTracks>k__BackingField", "_amfUsage", "_audioBitrate", "_audioChannels", "_audioEncoder",
        "_audioEncoderBeforeAudioOnly", "_audioLinked", "_audioSampleRate", "_audioTracksHint", "_autoCaptions", "_autosavedFingerprint",
        "_backgroundDim", "_backgroundRow", "_backgroundSelected", "_backgroundZoom", "_blurPasses", "_blurRadius", "_captionAnchorSource",
        "_captionAnchors", "_captionAudioPath", "_captionSourceChoice", "_captionUseExternalAudio", "_captionUseVoiceover", "_captionWords",
        "_chapterMarkers", "_chaptersAtCuts", "_chaptersAtMarkers", "_colorBlue", "_colorBrightness", "_colorContrast", "_colorGamma", "_colorGreen",
        "_colorHue", "_colorRed", "_colorSaturation", "_colorspace", "_commandPreview", "_committedSubtitles", "_container", "_crf", "_cropBottom",
        "_cropLeft", "_cropRight", "_cropTop", "_customFramerate", "_customPixelAspectRatio", "_deinterlace", "_denoise", "_destinationPath",
        "_documentShown", "_downloadResolution", "_downloadSubtitles", "_drawTargetLayer", "_duckAmountDb", "_duckAudio", "_durationMs",
        "_editingJob", "_emptyTrackViews", "_loadingAdds", "_addsToResume", "_encoderPreset", "_extraVideoArguments", "_fadeIn", "_fadeOut", "_filledEmptyTracks", "_framerate",
        "_generatedCaptions", "_iFrameLayout", "_iFrameScanCancellation", "_iFrameStatusText", "_isCommandManuallyEdited", "_isSourceHdr",
        "_keepAspectRatio", "_keyLayer", "_lastFieldEdit", "_lastLookEdit", "_lastTrackNumber", "_layoutSourceAspect", "_level", "_localMediaPath",
        "_lookahead", "_loudnessTarget", "_lutPath", "_mediaInfo", "_mergeAudioTracks", "_mixVoiceover", "_noVideo", "_normalizeAudio",
        "_nvencMultipass", "_nvencSpatialAq", "_nvencTemporalAq", "_nvencTune", "_openedClip", "_openedOver", "_outputHeight", "_outputWidth",
        "_pixelAspectRatio", "_placedFor", "_playheadVideoShown", "_positionMs", "_presetName", "_previewState", "_previewSubtitles", "_profile",
        "_projectGeneration", "_qsvIcq", "_rateControl", "_redo", "_resolutionPreset", "_savedFingerprint", "_selectedClipSet", "_selectedClips",
        "_selectedHandles", "_selectedKey", "_selectedKeyLane", "_selectedPreset", "_sequenceEffects", "_sequenceEffectsBypassed",
        "_sharpenStrength", "_sizeDrivesBitrate", "_snapToIFrames", "_sourceHeight", "_sourceIFrames", "_sourcePath", "_sourceWidth", "_speedFirst",
        "_spriteCancellation", "_spriteSheetPath", "_spriteSource", "_staleLooks", "_subtitleOffsetMs", "_subtitleOriginal", "_subtitleSourcePath",
        "_subtitleTracksHint", "_subtitlesAreImages", "_taken", "_targetBitrate", "_targetFileSize", "_targetSizeHint", "_templateToCome", "_tenBit",
        "_thumbnail", "_thumbnailAskedAt", "_thumbnailCapture", "_thumbnailGeneration", "_thumbnailTakenAt", "_timelineWaveform",
        "_timelineWaveformCancellation", "_touchedSinceSaved", "_trackDefaults", "_trackNumbers", "_tracksByNumber", "_tune", "_turnBeforePreset", "_undo", "_unpacking",
        "_useVerticalResolution", "_videoEncoder", "_views", "_visitStep", "_voiceoverDuration", "_voiceoverGainDb", "_voiceoverMixStart",
        "_voiceoverMixWaveform", "_voiceoverMixWidth", "_voiceoverPath", "_voiceoverStartSeconds", "_voiceoverStatus", "_voiceoverTrimEnd",
        "_voiceoverTrimStart", "_voiceoverWaveform", "_waveformCancellation", "_webOptimized", "_whisperLanguage", "_whisperPrompt",
        "_whisperTranslate", "_writtenCuts",
    };

    // The application's: the same whichever tab is in front (settings of the editor, caches by file, the clipboard, services, commands).
    private static readonly HashSet<string> AppFields = new(StringComparer.Ordinal)
    {
        "<AmfUsages>k__BackingField", "<AskOverwrite>k__BackingField", "<AudioEncoders>k__BackingField", "<CaptureThumbnail>k__BackingField",
        "<Choose>k__BackingField", "<Colorspaces>k__BackingField", "<Confirm>k__BackingField", "<ConfirmOrRemember>k__BackingField",
        "<ConfirmWarning>k__BackingField", "<Containers>k__BackingField", "<DefaultPresetChoices>k__BackingField",
        "<DependenciesNeeded>k__BackingField", "<DownloadResolutions>k__BackingField", "<EncoderPresets>k__BackingField",
        "<Framerates>k__BackingField", "<IsListingProjects>k__BackingField", "<IsTimelineDragHeld>k__BackingField", "<Levels>k__BackingField",
        "<LoudnessTargets>k__BackingField", "<Microphones>k__BackingField", "<NvencMultipasses>k__BackingField", "<NvencTunes>k__BackingField",
        "<PixelAspectRatios>k__BackingField", "<PlaybackControls>k__BackingField", "<PlaybackSpeeds>k__BackingField", "<PresetNames>k__BackingField",
        "<Presets>k__BackingField", "<Profiles>k__BackingField", "<Queue>k__BackingField", "<RateControlChoices>k__BackingField",
        "<RateControls>k__BackingField", "<RecentProjects>k__BackingField", "<ResolutionPresets>k__BackingField",
        "<SavesConvertedProjects>k__BackingField", "<SmartRules>k__BackingField", "<StylePresets>k__BackingField",
        "<TimelineSelection>k__BackingField", "<Tunes>k__BackingField", "<VideoEncoders>k__BackingField", "<WhisperModelChoices>k__BackingField",
        "ActionLogged", "CaptionsChanged", "ClipLibraryChanged", "ClipSelectionChanged", "EdgeTuningChanged", "LayerLoaded",
        "LayerVisibilityChanged", "MarkersChanged", "MediaLoaded", "PreviewRendered", "PreviewStateInvalidated", "SaveRequested", "SessionCleared",
        "TimelineChanged", "TimelineRestored", "_acceptProgressReports", "_activeStyle", "_anyKeys", "_applyingKeys", "_autosaving", "_baking",
        "_beforeEdit", "_checkpointedAt", "_commandDeferrals", "_commandPending", "_commandPreviewWatched", "_commandStale", "_commandTextQueued",
        "_committing", "_compactTrackControls", "_copied", "_copiedAttributes", "_cropDragStepTaken", "_cropDragging", "_cropForAll", "_cropHeld",
        "_cropShown", "_cropTypedAt", "_deadAirMode", "_deadAirSource", "_deadAirTarget", "_defaultPresetChoice", "_dependencyStatusText",
        "_editCommitted", "_exportLeftOut", "_exportQualityPreviewLoupe", "_exportQualityPreviewMode", "_exportQualityPreviewStatus",
        "_exportQualityPreviewWipe", "_filmstripsBeingMade", "_findingBeats", "_handlesWrittenAt", "_hardwareEncoderCount",
        "_hardwareEncoderStatusText", "_hardwareEncodersEarly", "_hardwareEncodersListed", "_hardwareProbeRun", "_hardwareProblems", "_hevcHinted",
        "_hideDroppedTracks", "_holdingSource", "_pendingAdds", "_sourceHeld", "_iFrameStatusVersion", "_inputGesture", "_isArrangeActive", "_isBackgroundWorker", "_isBusy",
        "_isDependencyUpdateAvailable", "_isExportQualityPreview", "_isInteractiveCropActive", "_isPlayerMuted", "_isPlaying",
        "_isProgressIndeterminate", "_isQueuePauseRequested", "_isRecording", "_isRecordingPaused", "_keyChangedOnly", "_keyframesEnabled",
        "_keyframesSnap", "_keysAppliedAt", "_layerControlsShown", "_layerFilmstrips", "_layerWaveforms", "_learningStarts", "_listingStyles",
        "_liveCaptionsWait", "_loadingAutomation", "_lookStepsHeld", "_loopPlayback", "_masterGainGesture", "_masterGainShown", "_openEdit",
        "_operationCancellation", "_otherText", "_pendingDefaultEncoder", "_pendingPreferredCodec", "_pictureDecode", "_pictureSet", "_pictureSetOf",
        "_pictureTrack", "_pictureWork", "_placingStep", "_placingViews", "_playbackSpeed", "_presetsView", "_progressValue", "_proxies",
        "_proxiesInUse", "_proxyFailed", "_proxyQueue", "_recentActions", "_recorder", "_recordingKey", "_recordingStartSeconds",
        "_refillingRateControl", "_reprojectPending", "_resolvingEncoder", "_restoring", "_resyncingShowIFrames", "_savedFromBuiltIn",
        "_selectedMicrophone", "_selectedProject", "_selectedStylePreset", "_selectingAppliedPreset", "_sequenceEffectWatcher", "_settingQuietly",
        "_settingsSave", "_settleEndsAt", "_settleNumber", "_showBeatGrid", "_showClipKeyframes", "_showEffectsPane", "_showIFrames",
        "_showKeyframeGraph", "_showKeyframesPane", "_showLinkedAudio", "_showMasterMeter", "_showMediaBrowser", "_showSafeMargins",
        "_showingTarget", "_shutdown", "_snapClips", "_snapToBeats", "_sourceThumbnails", "_stateText", "_statusText", "_stepGesture", "_styleName",
        "_subtitlesChangedPending", "_syncDepth", "_syncMasterTimeline", "_syncingDimensions", "_syncingResolutionPreset", "_syncingSizeAndBitrate",
        "_takingCrop", "_templateTargetOnly", "_toldPictures", "_undoSourcesHooked", "_userInputAt", "_volume", "_warmedFiles",
        "_watchingCropTarget", "_workFolder", "_writingGeneratedCommand", "addAdjustmentLayerCommand", "addEmptyTrackCommand", "addLayerCommand",
        "addTextLayerCommand", "addToQueueCommand", "cancelCommand", "clearCompletedCommand", "clearSubtitlesCommand",
        "copyYouTubeTimestampsCommand", "discardVoiceoverCommand", "downloadWhisperModelCommand", "drawTargetCommand", "editQueueJobCommand",
        "generateCaptionsCommand", "loadSourceCommand", "moveLayerDownCommand", "moveLayerUpCommand", "pauseQueueCommand", "pauseVoiceoverCommand",
        "recordVoiceoverCommand", "refreshMicrophonesCommand", "regenerateCommandCommand", "removeDeadAirCommand", "removeLayerCommand",
        "removeQueueJobCommand", "removeVoiceoverCommand", "renderPreviewCommand", "resetBlurSettingsCommand", "resetCaptionLayerCommand",
        "resetColorCommand", "resetCropCommand", "savePresetCommand", "saveProjectAsCommand", "saveProjectNowCommand", "startEncodeCommand",
        "startQueueCommand", "stopVoiceoverCommand", "toggleSoloCommand", "updateQueuedJobCommand",
    };
}
