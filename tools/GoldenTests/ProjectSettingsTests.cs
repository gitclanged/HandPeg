using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4A: dock-startup-timeline-tab (pure data: the group holding the Timeline shows it, whatever tab the arrangement was saved on, and
/// nothing else changes; InitializeDocking asks for it and the preview's restore does not); project-settings-model (the colour space, pixel
/// aspect, sample rate and the two "chosen on purpose" flags on the document: left out of a project file at their defaults, read back,
/// copied by Clone and by an undo step, set through a transaction that rolls back); project-settings-first-video (a real view model: the
/// first video gives the project its rate, size and colour space, unless the New Project studio chose them; a change with clips is one
/// step of Undo, a rate change refused); export-match-project-settings ("Same as source" gone, legacy values read as Match Project
/// Settings, the export state following the project's pixel aspect, colour space and sample rate); summary-tab-retired (the XAML and
/// layouts without the Summary tab, each of its controls in one new home, a saved arrangement on Summary opening on the Timeline);
/// project-manager-studio (the studio's modes and names, NewProjectRequest.FrameFor, the Media Browser as a picker that keeps the
/// dock's title slot and writes no Browser settings). Every line says what it proved.
/// </summary>
public static class ProjectSettingsTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    // The settings tabs' pane ids after 5.4B (MainWindow.xaml's SettingsTabs: "Tab:" and the header; 5.4B retired the Video tab).
    private static readonly IReadOnlyList<string> TabIds = ["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("dock-startup-timeline-tab", "# HandPeg always opens on the Timeline: the group holding it shows it, whatever tab the arrangement was saved on; nothing else changes.", StartupTimelineTab);
        yield return Case("project-settings-model", "# Project Settings on the document: colour space, pixel aspect, sample rate, left out of the file at their defaults, kept by Clone, Undo and a transaction.", Model);
        yield return Case("project-settings-first-video", "# The first video sets the Project Settings, unless the New Project studio chose them; a later change is one step of Undo.", FirstVideo);
        yield return Case("export-match-project-settings", "# Export's \"Same as source\" is \"Match Project Settings\": legacy values read as it, and the export follows the project.", MatchProjectSettings);
        yield return Case("summary-tab-retired", "# The Summary tab is retired: each of its controls has one new home, and an arrangement saved on it opens on the Timeline.", SummaryRetired);
        yield return Case("project-manager-studio", "# The Project Manager's studio: New Project | Open / Manage Projects | Project Settings, frames from the aspect cards, the Media Browser as a picker.", Studio);
        yield return Case("project-settings-fixes", "# Project Settings after review: Undo and Redo bring the frame back, only what was changed is sent, the first clip keeps chosen settings, the picker sorts, names never collide.", Fixes);
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

    // ----- dock-startup-timeline-tab -----

    private static void StartupTimelineTab(Action<bool, string> expect)
    {
        foreach (var saved in new[] { "Tab:Export", "Tab:Audio", "Tab:Filters" })
        {
            var standard = MainWindow.StandardLayout(TabIds);
            var group = standard.Main.GroupOf("UnifiedTimeline")!;
            group.Active = saved;
            var (shape, version) = (DockManager.Shape(standard.Main), standard.Version);
            var changed = DockManager.ShowInItsGroup(standard, "UnifiedTimeline");
            expect(changed && group.Active == "UnifiedTimeline", $"Standard Editing saved on {saved}: its group shows the Timeline again");
            expect(DockManager.Shape(standard.Main) == shape && standard.Version == version, "and the arrangement keeps its shape and version");
            expect(!DockManager.ShowInItsGroup(standard, "UnifiedTimeline"), "a second time changes nothing (true only when something changed)");
        }

        // Assembly: the Timeline in a group of its own; the tabs group beside it keeps the tab it was on.
        var assembly = MainWindow.AssemblyLayout(TabIds);
        var tabs = assembly.Main.GroupOf("Tab:Export")!;
        var before = tabs.Active;
        expect(!DockManager.ShowInItsGroup(assembly, "UnifiedTimeline") && tabs.Active == before && before == "Tab:Export",
            "Assembly & Media: the Timeline's own group already shows it, and the tabs beside it stay on Export (only its own group is touched)");

        // The Timeline in the side window: shown there while it is open; left alone in the arrangement the side window closed on.
        var side = DockNode.Group("Tab:Audio", "UnifiedTimeline");
        side.Active = "Tab:Audio";
        var open = new DockLayoutState { Main = DockNode.Group("Player"), Side = side, SideOpen = true };
        expect(DockManager.ShowInItsGroup(open, "UnifiedTimeline") && side.Active == "UnifiedTimeline", "in the open side window, its group shows the Timeline");
        var shut = DockNode.Group("Tab:Audio", "UnifiedTimeline");
        shut.Active = "Tab:Audio";
        var closed = new DockLayoutState { Main = DockNode.Group("Player"), SideClosed = shut };
        expect(!DockManager.ShowInItsGroup(closed, "UnifiedTimeline") && shut.Active == "Tab:Audio",
            "in the arrangement the side window closed on, it is left as the user made it");

        // The window asks for it at start-up, in the one Load before the first frame; the preview's restore does not.
        var docking = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MainWindow.Docking.cs"));
        var layouts = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MainWindow.Layouts.cs"));
        // 5.4D: the arrangement is the Default layout's (StartupLayoutJson), still in the one Load, still on the Timeline.
        expect(docking.Contains("saved: TakeFirstRunLayout() ?? StartupLayoutJson(), showFirst: TimelinePaneId);", StringComparison.Ordinal)
               && System.Text.RegularExpressions.Regex.Matches(docking, @"_dock\.Load\(").Count == 1,
            "InitializeDocking loads the arrangement (the Default layout's, StartupLayoutJson) with showFirst: TimelinePaneId, in its one Load");
        expect(layouts.Contains("_dock.Load(_layoutBeforePreview);", StringComparison.Ordinal), "RestoreLayoutBeforePreview loads it as it was (no showFirst)");
    }

    // ----- project-settings-model -----

    private static void Model(Action<bool, string> expect)
    {
        var options = (JsonSerializerOptions)typeof(ProjectStore).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var plain = new TimelineDocument { FrameRate = FrameRate.Ntsc2997 };
        var json = JsonSerializer.Serialize(plain, options);
        expect(!json.Contains("colorSpace", StringComparison.Ordinal) && !json.Contains("pixelAspect", StringComparison.Ordinal)
               && !json.Contains("sampleRate", StringComparison.Ordinal) && !json.Contains("explicitFrame", StringComparison.Ordinal)
               && !json.Contains("projectSettings", StringComparison.Ordinal),
            "a document at the defaults writes no colorSpace, pixelAspect, sampleRate or explicit flags (a project file stays as it was)");
        var old = JsonSerializer.Deserialize<TimelineDocument>(json, options)!;
        expect(old is { ColorSpace: ProjectColorSpace.Sdr709, PixelAspect: ProjectPixelAspect.Square, SampleRate: 48000, ExplicitFrame: false, ExplicitFrameRate: false },
            "a file without them reads BT.709 SDR, square pixels, 48 kHz, nothing chosen on purpose");

        var set = new TimelineDocument
        {
            ColorSpace = ProjectColorSpace.HdrPq, PixelAspect = ProjectPixelAspect.SixteenNine, SampleRate = 44100, ExplicitFrame = true, ExplicitFrameRate = true,
            NaturalWidth = 1920, NaturalHeight = 1080, UseVerticalResolution = true,
        };
        var node = JsonNode.Parse(JsonSerializer.Serialize(set, options))!;
        expect(node["colorSpace"]?.GetValue<string>() == "HdrPq" && node["pixelAspect"]?.GetValue<string>() == "SixteenNine" && node["sampleRate"]?.GetValue<int>() == 44100
               && node["explicitFrame"]?.GetValue<bool>() == true && node["explicitFrameRate"]?.GetValue<bool>() == true,
            "set, they are written by name: colorSpace HdrPq, pixelAspect SixteenNine, sampleRate 44100, explicitFrame, explicitFrameRate");
        var back = JsonSerializer.Deserialize<TimelineDocument>(node.ToJsonString(), options)!;
        expect(back is { ColorSpace: ProjectColorSpace.HdrPq, PixelAspect: ProjectPixelAspect.SixteenNine, SampleRate: 44100, ExplicitFrame: true, ExplicitFrameRate: true },
            "and read back");
        node["sampleRate"] = 12345;
        expect(JsonSerializer.Deserialize<TimelineDocument>(node.ToJsonString(), options)!.SampleRate == 48000, "a sample rate other than 44100, 48000 or 96000 reads as 48000");
        expect(set.ProjectSettings == new ProjectSettings(1920, 1080, true, FrameRate.Fps60, ProjectColorSpace.HdrPq, ProjectPixelAspect.SixteenNine, 44100),
            "the read-only ProjectSettings view holds the natural size, vertical, rate, colour space, pixel aspect and sample rate");

        foreach (var deep in new[] { false, true })
        {
            var copy = set.Clone(deep);
            expect(copy is { ColorSpace: ProjectColorSpace.HdrPq, PixelAspect: ProjectPixelAspect.SixteenNine, SampleRate: 44100, ExplicitFrame: true, ExplicitFrameRate: true },
                $"Clone(deep: {deep.ToString().ToLowerInvariant()}) copies them (project tabs, queue jobs, autosave)");
        }

        var unpacked = UndoSteps.Unpack(UndoSteps.Pack(new TimelineSnapshot(set.Clone(), [])))?.Document;
        expect(unpacked is { ColorSpace: ProjectColorSpace.HdrPq, PixelAspect: ProjectPixelAspect.SixteenNine, SampleRate: 44100, ExplicitFrame: true },
            "an undo step (UndoSteps.Pack and Unpack) keeps them");

        expect(ProjectSettings.ColorSpaceOf("smpte2084") == ProjectColorSpace.HdrPq && ProjectSettings.ColorSpaceOf("arib-std-b67") == ProjectColorSpace.Hlg
               && ProjectSettings.ColorSpaceOf("bt709") == ProjectColorSpace.Sdr709 && ProjectSettings.ColorSpaceOf(null) == ProjectColorSpace.Sdr709,
            "a transfer characteristic's colour space: smpte2084 HDR10 PQ, arib-std-b67 HLG, anything else BT.709 SDR");
        expect(ProjectSettings.Label(ProjectColorSpace.Sdr709) == "BT.709 SDR" && ProjectSettings.Label(ProjectColorSpace.HdrPq) == "HDR10 PQ" && ProjectSettings.Label(ProjectColorSpace.Hlg) == "HLG"
               && ProjectSettings.Label(ProjectPixelAspect.Square) == "Square Pixels (1:1)" && ProjectSettings.Label(ProjectPixelAspect.FourThree) == "4:3"
               && ProjectSettings.SampleAspectOf(ProjectPixelAspect.Square) is null && ProjectSettings.SampleAspectOf(ProjectPixelAspect.SixteenNine) == "16/9",
            "their labels, and the sample aspect each asks an export for (none for square pixels)");

        // Through a transaction: rolled back when not committed; committed, a step of its own with no clip touched; the same again, nothing.
        var document = new TimelineDocument();
        var commits = new List<TimelineCommit>();
        document.Committed += commits.Add;
        using (var rolled = document.BeginTransaction("try the settings"))
            rolled.SetProjectSettings(ProjectColorSpace.Hlg, ProjectPixelAspect.FourThree, 96000);
        expect(document is { ColorSpace: ProjectColorSpace.Sdr709, PixelAspect: ProjectPixelAspect.Square, SampleRate: 48000 } && commits.Count == 0,
            "a transaction disposed without a commit puts them back, and raises nothing");
        using (var made = document.BeginTransaction("change the Project Settings"))
        {
            made.SetProjectSettings(ProjectColorSpace.Hlg, ProjectPixelAspect.FourThree, 12345);
            made.Commit();
        }

        expect(document is { ColorSpace: ProjectColorSpace.Hlg, PixelAspect: ProjectPixelAspect.FourThree, SampleRate: 48000 }
               && commits is [{ TouchedClipIds.Count: 0, TouchedTrackIds.Count: 0 }],
            "committed, they change (12345 Hz taken as 48 kHz) and Committed is raised once with no clip or track touched (a step of Undo)");
        using (var same = document.BeginTransaction("the same again"))
        {
            same.SetProjectSettings(ProjectColorSpace.Hlg, ProjectPixelAspect.FourThree, 48000);
            same.Commit();
        }

        expect(commits.Count == 1, "the same values again change nothing and raise nothing");
    }

    // ----- project-settings-first-video -----

    private static MediaInfo Probe(int width, int height, double fps, string transfer, double seconds = 8) =>
        new("matroska", seconds, 0, new VideoStreamInfo(width, height, "hevc", fps, "16:9", "1:1", transfer, transfer == "bt709" ? "bt709" : "bt2020", 0), [], []);

    /// <summary>A view model with an empty Sequence, as a New Project leaves it, its probe of the file to open set (nothing is probed or decoded).</summary>
    private static MainViewModel Empty()
    {
        var vm = new MainViewModel();
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [new TimelineDocument()]);
        return vm;
    }

    /// <summary>Opens a file onto the view model as a load does once probed (PlaceOpenedFile), the file opened before given.</summary>
    private static void Place(MainViewModel vm, string path, MediaInfo probe, string previous = "")
    {
        vm.SourceWidth = probe.Video!.Width;
        vm.SourceHeight = probe.Video.Height;
        typeof(MainViewModel).GetField("_mediaInfo", Hidden)!.SetValue(vm, probe);
        vm.LocalMediaPath = path;
        typeof(MainViewModel).GetMethod("PlaceOpenedFile", Hidden)!.Invoke(vm, [previous]);
    }

    private static int UndoCount(MainViewModel vm) => ((System.Collections.ICollection)typeof(MainViewModel).GetField("_undo", Hidden)!.GetValue(vm)!).Count;

    private static void FirstVideo(Action<bool, string> expect)
    {
        // An HDR first video: the project takes its rate, its size and its colour space (the user's decision: an HDR clip makes an HDR project).
        var vm = Empty();
        Place(vm, "D:/media/hdr.mkv", Probe(3840, 2160, 59.94, "smpte2084"));
        var document = vm.Document;
        expect(document.FrameRate == FrameRate.Ntsc5994 && document.NaturalWidth == 3840 && document.NaturalHeight == 2160,
            "the first video gives the Sequence its rate (60000/1001) and natural size (3840 x 2160)");
        expect(document is { ColorSpace: ProjectColorSpace.HdrPq, PixelAspect: ProjectPixelAspect.Square, SampleRate: 48000 },
            "and its colour space from its transfer (smpte2084: HDR10 PQ), square pixels, 48 kHz");
        expect(vm.CurrentProjectSettings == new ProjectSettings(3840, 2160, vm.UseVerticalResolution, FrameRate.Ntsc5994, ProjectColorSpace.HdrPq, ProjectPixelAspect.Square, 48000),
            "CurrentProjectSettings says so, vertical as the view model holds it");
        expect(vm.ProjectColorNote.Contains("BT.709 SDR, tone-mapped", StringComparison.Ordinal), "and Export says an HDR project is written as BT.709 SDR, tone-mapped");

        // A second file over the clips of the first: the project keeps its settings.
        Place(vm, "D:/media/other.mkv", Probe(1280, 720, 30, "bt709"));
        expect(vm.Document is { NaturalWidth: 3840, NaturalHeight: 2160, ColorSpace: ProjectColorSpace.HdrPq } && vm.Document.FrameRate == FrameRate.Ntsc5994,
            "a second file opened while the Sequence has clips changes none of them");

        // The New Project studio's choice first (9:16 1080 x 1920 at 60, SDR): the first video keeps it.
        var chosen = Empty();
        var applied = chosen.ApplyProjectSettings(1080, 1920, FrameRate.Fps60, ProjectColorSpace.Sdr709, ProjectPixelAspect.Square, 48000, undoable: false);
        expect(applied && chosen.Document is { NaturalWidth: 1920, NaturalHeight: 1080, ExplicitFrame: true, ExplicitFrameRate: true } && chosen.UseVerticalResolution
               && chosen.FrameWidth == 1080 && chosen.FrameHeight == 1920 && chosen.OutputWidth.Length == 0 && chosen.OutputHeight.Length == 0,
            "ApplyProjectSettings(1080 x 1920): a 1920 x 1080 natural size turned on its side, Export's size back to Match Project Settings, chosen on purpose");
        expect(UndoCount(chosen) == 0, "on an empty Sequence, no step of Undo");
        Place(chosen, "D:/media/hdr.mkv", Probe(3840, 2160, 59.94, "smpte2084"));
        expect(chosen.Document is { NaturalWidth: 1920, NaturalHeight: 1080, ColorSpace: ProjectColorSpace.Sdr709 } && chosen.Document.FrameRate == FrameRate.Fps60
               && chosen.UseVerticalResolution && chosen.FrameWidth == 1080 && chosen.FrameHeight == 1920,
            "the first video (4K HDR at 59.94) keeps the frame, rate and colour space chosen: 1080 x 1920 at 60, BT.709 SDR");
        expect(chosen.Document.Tracks.Sum(t => t.Clips.Count(c => c.ParentClipId is null)) == 1, "and it is laid on the Sequence all the same");

        // Auto rate, the frame left to the first clip: the clip's own.
        var auto = Empty();
        auto.ApplyProjectSettings(0, 0, null, ProjectColorSpace.Sdr709, ProjectPixelAspect.Square, 48000, undoable: false);
        Place(auto, "D:/media/clip.mkv", Probe(2560, 1440, 120, "bt709"));
        expect(auto.Document is { NaturalWidth: 2560, NaturalHeight: 1440, ExplicitFrame: false, ExplicitFrameRate: false } && auto.Document.FrameRate == FrameRate.Fps120,
            "with nothing chosen (0 x 0, Auto), the first clip gives its own size and rate");

        // With clips: a frame and colour change is one step of Undo, undone whole; a rate change is refused.
        var steps = UndoCount(vm);
        expect(!vm.ApplyProjectSettings(1920, 1080, FrameRate.Fps24, ProjectColorSpace.Sdr709, ProjectPixelAspect.Square, 48000)
               && vm.StatusText.Contains("frame rate", StringComparison.Ordinal) && vm.Document.FrameRate == FrameRate.Ntsc5994 && UndoCount(vm) == steps,
            "a frame-rate change on a Sequence with clips is refused, with a status line, and changes nothing");
        expect(vm.ApplyProjectSettings(1920, 1080, null, ProjectColorSpace.Hlg, ProjectPixelAspect.FourThree, 44100)
               && vm.Document is { NaturalWidth: 1920, NaturalHeight: 1080, ColorSpace: ProjectColorSpace.Hlg, PixelAspect: ProjectPixelAspect.FourThree, SampleRate: 44100 }
               && UndoCount(vm) == steps + 1,
            "1920 x 1080, HLG, 4:3, 44.1 kHz on a Sequence with clips: applied as one step of Undo");
        vm.Undo();
        expect(vm.Document is { NaturalWidth: 3840, NaturalHeight: 2160, ColorSpace: ProjectColorSpace.HdrPq, PixelAspect: ProjectPixelAspect.Square, SampleRate: 48000, ExplicitFrame: false },
            "Undo puts the size, colour space, pixel aspect and sample rate back together");
    }

    // ----- project-settings-fixes -----

    private static List<(string What, byte[] State)> StepsOf(MainViewModel vm) =>
        (List<(string What, byte[] State)>)typeof(MainViewModel).GetField("_undo", Hidden)!.GetValue(vm)!;

    private static void Fixes(Action<bool, string> expect)
    {
        // B5: a frame change undone and redone whole, the turn to tall and Export's Resolution boxes with it.
        var vm = Empty();
        Place(vm, "D:/media/wide.mkv", Probe(1920, 1080, 60, "bt709"));
        vm.UseVerticalResolution = false;
        vm.OutputWidth = "1280";
        vm.OutputHeight = "720";
        expect(vm is { UseVerticalResolution: false, OutputWidth: "1280", OutputHeight: "720", FrameWidth: 1280, FrameHeight: 720 } && vm.Document is { NaturalWidth: 1920, NaturalHeight: 1080 },
            "a 1920 x 1080 project with clips, wide, 1280 x 720 typed in Export's Resolution");
        var steps = UndoCount(vm);
        expect(vm.ApplyProjectSettings(1080, 1920, null, ProjectColorSpace.Sdr709, ProjectPixelAspect.Square, 48000)
               && vm is { UseVerticalResolution: true, OutputWidth: "", OutputHeight: "", FrameWidth: 1080, FrameHeight: 1920 } && UndoCount(vm) == steps + 1,
            "Project Settings 1080 x 1920: turned tall, Export's Resolution back to Match Project Settings, one step of Undo");
        var step = UndoSteps.Unpack(StepsOf(vm)[^1].State);
        expect(step?.Frame == new ProjectSettingsFrame(false, "1280", "720"), "the step carries the frame as it was: wide, 1280 x 720 in the boxes");
        vm.Undo();
        expect(vm is { UseVerticalResolution: false, OutputWidth: "1280", OutputHeight: "720", FrameWidth: 1280, FrameHeight: 720 }
               && vm.Document is { NaturalWidth: 1920, NaturalHeight: 1080, ExplicitFrame: false },
            "Undo: wide again, 1280 x 720 back in the boxes, the natural size and nothing chosen on purpose as before");
        vm.Redo();
        expect(vm is { UseVerticalResolution: true, OutputWidth: "", OutputHeight: "", FrameWidth: 1080, FrameHeight: 1920 } && vm.Document is { ExplicitFrame: true },
            "Redo: the 9:16 frame again, the boxes blank, chosen on purpose");
        vm.Undo();
        expect(vm is { UseVerticalResolution: false, OutputWidth: "1280", OutputHeight: "720" }, "and Undo once more: wide, 1280 x 720 (the frame goes with the step both ways)");

        vm.Checkpoint("an ordinary step");
        expect(UndoSteps.Unpack(StepsOf(vm)[^1].State) is { Frame: null }
               && !JsonSerializer.Serialize(new TimelineSnapshot(new TimelineDocument(), [])).Contains("\"Frame\"", StringComparison.Ordinal),
            "every other step carries no frame, and writes none (its packed bytes as before)");

        // B6: a frame or rate not given leaves the marks and Export's boxes as they were.
        var boxes = Empty();
        Place(boxes, "D:/media/wide.mkv", Probe(1920, 1080, 60, "bt709"));
        boxes.UseVerticalResolution = false;
        boxes.OutputWidth = "1280";
        boxes.OutputHeight = "720";
        expect(boxes.ApplyProjectSettings(0, 0, null, ProjectColorSpace.Sdr709, ProjectPixelAspect.Square, 44100)
               && boxes is { OutputWidth: "1280", OutputHeight: "720", UseVerticalResolution: false }
               && boxes.Document is { SampleRate: 44100, ExplicitFrame: false, ExplicitFrameRate: false },
            "only the sample rate changed (0 x 0, no rate): 44.1 kHz, Export's 1280 x 720 kept, the frame and rate not marked as chosen");
        var chosen = Empty();
        chosen.ApplyProjectSettings(1080, 1920, FrameRate.Fps60, ProjectColorSpace.Sdr709, ProjectPixelAspect.Square, 48000, undoable: false);
        chosen.ApplyProjectSettings(0, 0, null, ProjectColorSpace.Sdr709, ProjectPixelAspect.Square, 96000, undoable: false);
        expect(chosen.Document is { ExplicitFrame: true, ExplicitFrameRate: true, NaturalWidth: 1920, NaturalHeight: 1080, SampleRate: 96000 } && chosen.UseVerticalResolution
               && chosen.Document.FrameRate == FrameRate.Fps60,
            "a frame and rate chosen, then only the sample rate: still chosen, still 1080 x 1920 at 60");
        chosen.LeaveToFirstClip(frame: true, rate: true);
        expect(chosen.Document is { ExplicitFrame: false, ExplicitFrameRate: false }, "the frame emptied and Auto chosen again on an empty Sequence: left to the first clip");
        boxes.Document.ExplicitFrameRate = true;
        boxes.LeaveToFirstClip(frame: true, rate: true);
        expect(boxes.Document.ExplicitFrameRate, "but not once the Sequence has clips");

        // B7: what Project Settings chose before the first clip stays.
        var early = Empty();
        early.ApplyProjectSettings(0, 0, null, ProjectColorSpace.Sdr709, ProjectPixelAspect.FourThree, 44100, undoable: false);
        Place(early, "D:/media/hdr.mkv", Probe(3840, 2160, 59.94, "smpte2084"));
        expect(early.Document is { PixelAspect: ProjectPixelAspect.FourThree, SampleRate: 44100, ColorSpace: ProjectColorSpace.HdrPq, NaturalWidth: 3840 },
            "4:3 and 44.1 kHz chosen with the frame blank: the first clip gives its size and colour space and keeps them");

        // B8: the picker sorts by its own Sort box and direction, and writes no setting.
        var settings = AppSettings.Current;
        var kept = (settings.BrowserSort, settings.BrowserSortDescending);
        var before = PaneTitleSlot.Of("Media");
        try
        {
            var picker = new MediaBrowserPane(pickerMode: true);
            var items = (System.Collections.ObjectModel.ObservableCollection<MediaBrowserItem>)typeof(MediaBrowserPane).GetField("_items", Hidden)!.GetValue(picker)!;
            picker.SortBox.SelectedIndex = 2;
            picker.SortDirectionButton.IsChecked = false;
            items.Clear();
            foreach (var (name, bytes) in new[] { ("b.mp4", 30L), ("a.mp4", 10L), ("c.mp4", 20L) })
                items.Add(new MediaBrowserItem("D:/media/" + name, MediaBrowserKind.Video, name) { Bytes = bytes });
            items.Add(new MediaBrowserItem("D:/media/zed", MediaBrowserKind.Folder, "zed"));
            picker.SortDirectionButton.IsChecked = true;
            expect(string.Join(" ", items.Select(i => i.Name)) == "zed b.mp4 c.mp4 a.mp4", "Size, descending: the folder first, then 30, 20, 10 bytes");
            picker.SortBox.SelectedIndex = 0;
            expect(string.Join(" ", items.Select(i => i.Name)) == "zed c.mp4 b.mp4 a.mp4", "Name, descending: c, b, a");
            expect((settings.BrowserSort, settings.BrowserSortDescending) == kept, "and the Browser settings are as they were");
            picker.Release();
        }
        finally
        {
            PaneTitleSlot.Restore("Media", before);
            (settings.BrowserSort, settings.BrowserSortDescending) = kept;
        }

        var listed = new[] { new MediaBrowserItem("D:/x/b.mkv", MediaBrowserKind.Video, "b.mkv") { Bytes = 1 }, new MediaBrowserItem("D:/x/a.mkv", MediaBrowserKind.Video, "a.mkv") { Bytes = 2 } };
        expect(string.Join(" ", MediaBrowserPane.SortedBy(listed, "Name", false).Select(i => i.Name)) == "a.mkv b.mkv"
               && string.Join(" ", MediaBrowserPane.SortedBy(listed, "Size", true).Select(i => i.Name)) == "a.mkv b.mkv"
               && string.Join(" ", MediaBrowserPane.SortedBy(listed, "Size", false).Select(i => i.Name)) == "b.mkv a.mkv",
            "SortedBy: by name ascending, by size either way");

        // B9: the name offered is free in the folder, and a save never writes over another project without asking.
        var folder = Path.Combine(Path.GetDirectoryName(AppPaths.Projects)!, "free-names");
        Directory.CreateDirectory(folder);
        expect(MainViewModel.FreeProjectName(folder) == "New Project", "an empty folder offers \"New Project\"");
        File.WriteAllText(Path.Combine(folder, "New Project" + ProjectStore.Extension), "{}");
        expect(MainViewModel.FreeProjectName(folder) == "New Project 2", "with New Project.hproj there: \"New Project 2\"");
        Directory.CreateDirectory(Path.Combine(folder, "New Project 2"));
        expect(MainViewModel.FreeProjectName(folder) == "New Project 3", "with a portable project's folder New Project 2 there too: \"New Project 3\"");

        var portable = settings.FullyPortableProjectSave;
        try
        {
            settings.FullyPortableProjectSave = false;
            Directory.CreateDirectory(AppPaths.Projects);
            var taken = Path.Combine(AppPaths.Projects, "Taken" + ProjectStore.Extension);
            File.WriteAllText(taken, "kept");
            var saver = Empty();
            var asked = 0;
            saver.Confirm = (_, _, _) =>
            {
                asked++;
                return false;
            };
            var said = saver.SaveProject("Taken");
            expect(said.StartsWith("Not saved", StringComparison.Ordinal) && asked == 1 && File.ReadAllText(taken) == "kept" && saver.CurrentProjectPath.Length == 0,
                "a save by a name already in Projects asks once; declined, nothing is written");
            saver.Confirm = null;
            expect(saver.SaveProject("Taken").StartsWith("Not saved", StringComparison.Ordinal) && File.ReadAllText(taken) == "kept",
                "with no one to ask, it is refused");

            saver.KeepNewProjectName("New Project", folder);
            saver.Confirm = (_, _, _) =>
            {
                asked++;
                return false;
            };
            expect(saver.SaveProject("New Project").StartsWith("Not saved", StringComparison.Ordinal) && asked == 2
                   && File.ReadAllText(Path.Combine(folder, "New Project" + ProjectStore.Extension)) == "{}",
                "the first save into the studio's folder over a project there asks too, and declined writes nothing");

            saver.KeepNewProjectName("", null);
            saver.Confirm = (_, _, _) =>
            {
                asked++;
                return true;
            };
            var replaced = saver.SaveProject("Taken");
            expect(replaced.StartsWith("Project saved", StringComparison.Ordinal) && asked == 3 && File.ReadAllText(taken) != "kept" && saver.CurrentProjectPath == taken,
                "said yes: it is replaced, and is the open project");
            saver.SaveProject("Taken");
            expect(asked == 3, "saved again under its own name: nothing asked");
        }
        finally
        {
            settings.FullyPortableProjectSave = portable;
        }
    }

    // ----- export-match-project-settings -----

    private static SequenceExportState ExportState(MainViewModel vm) =>
        (SequenceExportState)typeof(MainViewModel).GetMethod("CompileExportState", Hidden)!.Invoke(vm, null)!;

    private static void MatchProjectSettings(Action<bool, string> expect)
    {
        const string match = "Match Project Settings", legacy = "Same as source";
        var vm = Empty();
        expect(MainViewModel.MatchProject == match && vm.Framerate == match && vm.Colorspace == match && vm.PixelAspectRatio == match,
            "a new project's Framerate, Colorspace and Pixel Aspect are Match Project Settings");
        expect(!vm.Framerates.Contains(legacy) && !vm.Colorspaces.Contains(legacy) && !vm.PixelAspectRatios.Contains(legacy)
               && vm.Framerates[0] == match && vm.Colorspaces[0] == match && vm.PixelAspectRatios[0] == match,
            "no list offers Same as source; each starts with Match Project Settings");
        expect(vm.AudioSampleRates[0] is { Key: 0, Value: "Match Project Settings (48 kHz)" }, "Sample Rate's first choice is Match Project Settings (48 kHz), still saved as 0");
        expect(vm.ResolutionPresets[0] == match && vm.ResolutionPreset == match, "Resolution's first preset is Match Project Settings, and blank boxes show it");
        vm.ResolutionPreset = "1080p (1920x1080)";
        expect((vm.OutputWidth, vm.OutputHeight) is ("1920", "1080") or ("1080", "1920"), "a size preset fills the boxes (on their side while the frame is vertical)");
        vm.ResolutionPreset = match;
        expect(vm.OutputWidth.Length == 0 && vm.OutputHeight.Length == 0, "Match Project Settings empties them again (the Sequence's own size)");
        var fresh = new EncodingPreset();
        expect(fresh is { Framerate: match, Colorspace: match, PixelAspectRatio: match }, "a preset's defaults are Match Project Settings");

        // Legacy values: a preset or project saved with Same as source reads as Match Project Settings, and is saved as it from then on.
        var apply = typeof(MainViewModel).GetMethod("ApplyPreset", Hidden)!;
        apply.Invoke(vm, [new EncodingPreset { Framerate = "30", Colorspace = "HDR to SDR (Tonemap)", PixelAspectRatio = "16:9" }, false]);
        apply.Invoke(vm, [new EncodingPreset { Framerate = legacy, Colorspace = legacy, PixelAspectRatio = legacy }, false]);
        expect(vm.Framerate == match && vm.Colorspace == match && vm.PixelAspectRatio == match, "a preset holding Same as source applies as Match Project Settings");
        var presets = JsonSerializer.Deserialize("[{\"Name\":\"Old\",\"PixelAspectRatio\":\"Same as source\",\"Framerate\":\"Same as source\",\"Colorspace\":\"Same as source\"}]",
            AppSettingsJsonContext.Default.ListEncodingPreset)!;
        apply.Invoke(vm, [new EncodingPreset { Framerate = "60" }, false]);
        apply.Invoke(vm, [presets[0], false]);
        expect(vm.Framerate == match && vm.Colorspace == match && vm.PixelAspectRatio == match, "so does one read from an old presets.json");
        expect(vm.CurrentEncodingSettings() is { Framerate: match, Colorspace: match, PixelAspectRatio: match }, "and the settings are saved as Match Project Settings");

        // The export: as before while the project keeps its defaults, then following the project's settings.
        var state = ExportState(vm);
        expect(state is { TargetFramerate: null, SampleAspectRatio: null, TonemapToSdr: false }, "at the defaults: the Sequence's rate, square pixels, no tone-map (as Same as source was)");
        expect(ExportSoundPlan.For(state, pictureOnly: true).SampleRate == 48000, "and sound mixed at 48 kHz");
        vm.Document.PixelAspect = ProjectPixelAspect.SixteenNine;
        vm.Document.SampleRate = 44100;
        state = ExportState(vm);
        expect(state.SampleAspectRatio == "16/9", "a project of 16:9 pixels exports them (-aspect 16/9)");
        expect(ExportSoundPlan.For(state, pictureOnly: true).SampleRate == 44100, "a 44.1 kHz project's sound is mixed at 44.1 kHz");
        vm.Document.ColorSpace = ProjectColorSpace.HdrPq;
        expect(ExportState(vm).TonemapToSdr && vm.ProjectColorNote.Length > 0, "an HDR project under Match Project Settings is tone-mapped to BT.709 SDR, and says so");
        vm.PixelAspectRatio = "1:1";
        vm.AudioSampleRate = 48000;
        state = ExportState(vm);
        expect(state.SampleAspectRatio == "1/1" && ExportSoundPlan.For(state, pictureOnly: true).SampleRate == 48000,
            "a value chosen in Export still wins over the project's (1:1, 48000 Hz)");
    }

    // ----- summary-tab-retired -----

    private static void SummaryRetired(Action<bool, string> expect)
    {
        var root = RepositoryRoot();
        var mainText = File.ReadAllText(Path.Combine(root, "src", "MainWindow.xaml"));
        var main = XDocument.Parse(mainText);
        var timelinePane = File.ReadAllText(Path.Combine(root, "src", "Views", "UnifiedTimelinePane.xaml"));
        var headers = main.Descendants().Where(e => e.Name.LocalName == "TabItem").Select(e => (string?)e.Attribute("Header")).ToList();
        expect(!headers.Contains("Summary") && headers.SequenceEqual(TabIds.Select(id => id["Tab:".Length..])),
            "MainWindow.xaml's tabs are Filters, Audio, Export and Command Preview: no Summary (and, 5.4B, no Video)");
        expect(MainWindow.BuiltInLayoutStates(TabIds).All(l => !l.Layout.Main.AllPanes().Contains("Tab:Summary")), "no built-in layout holds Tab:Summary");
        expect(!File.ReadAllText(Path.Combine(root, "src", "MainWindow.Layouts.cs")).Contains("Tab:Summary", StringComparison.Ordinal),
            "and MainWindow.Layouts.cs names it nowhere");
        expect(MainWindow.InspectorLayout(TabIds).Main.GroupOf("Media") is { Panes: ["Media"] }, "Color & Effects' media group is the Media Browser alone");

        int Count(string text, string what) => (text.Length - text.Replace(what, "", StringComparison.Ordinal).Length) / what.Length;
        var export = main.Descendants().First(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ExportPanel");
        string Named(string automation) => $"AutomationProperties.Name=\"{automation}\"";
        bool InExport(string automation) => export.Descendants().Any(e => (string?)e.Attribute("AutomationProperties.Name") == automation);
        expect(InExport("Format") && InExport("Web Optimized") && InExport("Source Details") && InExport("Source Information") && InExport("Projected Output"),
            "the Export tab holds Format, Web Optimized, the Projected Output (5.4B: in its third card) and a Source Details expander (Source Information)");
        expect(export.Descendants().Any(e => e.Name.LocalName == "ContentControl" && ((string?)e.Attribute("ContentTemplate"))?.Contains("PresetBar", StringComparison.Ordinal) == true
                                             && ((string?)e.Attribute("Visibility"))?.Contains("ShowPresetBarInExport", StringComparison.Ordinal) == true),
            "and the Export Presets selector while it is not above the tabs (ShowPresetBarInExport)");
        expect(Count(mainText, Named("Export Presets")) == 1 && Count(mainText, Named("Manage Export Presets")) == 1
               && Count(mainText, "{Binding Containers}") == 1 && Count(mainText, "{Binding WebOptimized}") == 1
               && Count(mainText, "Text=\"{Binding CopyBypassWarning}\"") == 1 && Count(mainText, "{Binding SourceInfoRows}") == 1
               && Count(mainText, "{Binding OutputInfoRows}") == 1 && Count(mainText, "{Binding UseVerticalResolution}") == 1,
            "each former Summary control has exactly one home: the Export Presets selector (and its Import / Export...), Format, Web Optimized, the copy warning, the details, Vertical in the Export tab's second card");
        expect(!mainText.Contains("Swap Project", StringComparison.Ordinal) && !mainText.Contains("SummaryCards", StringComparison.Ordinal)
               && Count(mainText, Named("Manage Game Presets")) == 0 && Count(timelinePane, Named("Manage Game Presets")) == 0
               && Count(timelinePane, Named("Game Presets")) == 0,
            "Swap Project and the cards are gone; the Timeline's row of tools has no Game Presets box (5.4C: a clip's or a track's right-click menu applies, saves, imports and exports them)");
        expect(AppSettings.PresetBarLocations.SequenceEqual([AppSettings.PresetBarInExport, AppSettings.PresetBarAtTop]) && AppSettings.PresetBarInExport == "Show in Export Tab"
               && new AppSettings().PresetBarLocation == AppSettings.PresetBarInExport,
            "the preset bar's places are the Export tab (the default) and the top of the window");

        // An arrangement saved on the Summary tab (every 5.3 arrangement holds it): the pane is unknown now, and its group shows the Timeline.
        var saved = MainWindow.StandardLayout(["Tab:Summary", .. TabIds]);
        saved.Main.GroupOf("Tab:Summary")!.Active = "Tab:Summary";
        var json = JsonSerializer.Serialize(saved, AppSettingsJsonContext.Default.DockLayoutState);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new ContentControl { Focusable = false };
        var window = new Window { Left = 40, Top = 40, Width = 1000, Height = 700, Content = host, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        var dock = new DockManager(window, host, () => MainWindow.StandardLayout(TabIds));
        foreach (var id in new[] { "Player", "Playback", "Timeline", "UnifiedTimeline", "Layout", "Header", "Media", "Keyframes", "Effects", "Meter", "Presets" }.Concat(TabIds))
        {
            var content = new Border { Child = new TextBlock { Text = id } };
            dock.Register(new DockPane { Id = id, Title = id, Content = () => content, Handle = id.StartsWith("Tab:", StringComparison.Ordinal) ? PaneHandle.Tab : PaneHandle.TitleSlot, MinWidth = () => 160, MinHeight = 60 });
        }

        var mainWindow = app.MainWindow;
        try
        {
            window.Show();
            dock.Load(json);
            var current = dock.Current;
            expect(!current.Main.AllPanes().Contains("Tab:Summary") && current.Main.GroupOf("UnifiedTimeline") is { Active: "UnifiedTimeline" } tabs && TabIds.All(tabs.Panes.Contains),
                "a saved arrangement on Tab:Summary loads without it, its group on the Timeline (no dock version bump: Validate drops the unknown pane)");
        }
        finally
        {
            window.Close();
            app.MainWindow = mainWindow;
        }
    }

    // ----- project-manager-studio -----

    private static void Studio(Action<bool, string> expect)
    {
        var root = RepositoryRoot();
        var xaml = XDocument.Load(Path.Combine(root, "src", "Dialogs", "ProjectManagerWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var automation = xaml.Descendants().Select(e => (string?)e.Attribute("AutomationProperties.Name")).OfType<string>().ToList();
        var names = xaml.Descendants().Select(e => (string?)e.Attribute(x + "Name")).OfType<string>().ToList();
        var modes = xaml.Descendants().Where(e => e.Name.LocalName == "RadioButton" && (string?)e.Attribute("GroupName") is null && ((string?)e.Attribute("Style"))?.Contains("ModeButton", StringComparison.Ordinal) == true)
            .Select(e => (string?)e.Attribute("AutomationProperties.Name")).ToList();
        expect(modes.SequenceEqual(["New Project Mode", "Open / Manage Projects", "Project Settings"]), "the mode bar: New Project | Open / Manage Projects | Project Settings, in that order");
        expect(new[] { "New Project", "Save Project Bundle", "Open Project or Bundle File", "Show Video and Hover Previews in Project Manager" }.All(automation.Contains)
               && new[] { "NameBox", "ProjectList", "LoadButton", "DeleteButton", "MessageText", "PreviewsBox" }.All(names.Contains),
            "Open / Manage keeps today's parts and names");
        expect(new[] { "New Project Name", "New Project Location", "16:9 Horizontal", "9:16 Vertical", "1:1 Square", "New Project Resolution", "New Project Frame Rate",
                   "New Project Colour Space", "Game Preset Cards", "Create Project", "Apply Project Settings", "Project Frame Rate", "Project Colour Space", "Project Pixel Aspect",
                   "Project Sample Rate" }.All(automation.Contains),
            "New Project has the name, location, aspect cards, resolution, rate, colour space, Game Preset cards and Create Project; Project Settings its boxes and Apply");
        var options = xaml.Descendants().Where(e => e.Name.LocalName == "ComboBoxItem").Select(e => (string?)e.Attribute("Content")).ToList();
        expect(new[] { "720p", "1080p", "1440p", "4K", "Custom W x H", "60", "30", "120", "24", "Auto from First Clip", "BT.709 SDR", "HDR (tone-mapped to SDR on export)" }.All(options.Contains),
            "resolutions 720p to 4K and Custom, rates 60 / 30 / 120 / 24 / Auto from First Clip, BT.709 SDR or HDR tone-mapped");

        expect(NewProjectRequest.FrameFor("16:9", 1080) == (1920, 1080) && NewProjectRequest.FrameFor("9:16", 1080) == (1080, 1920) && NewProjectRequest.FrameFor("1:1", 1080) == (1080, 1080),
            "FrameFor at 1080: 1920 x 1080, 1080 x 1920, 1080 x 1080");
        expect(NewProjectRequest.FrameFor("16:9", 720) == (1280, 720) && NewProjectRequest.FrameFor("16:9", 1440) == (2560, 1440) && NewProjectRequest.FrameFor("9:16", 2160) == (2160, 3840),
            "720p 1280 x 720, 1440p 2560 x 1440, 4K vertical 2160 x 3840");
        expect(NewProjectRequest.FrameFor("16:9", 1080, 1001, 777) == (1000, 776) && NewProjectRequest.FrameFor("odd", 1080) == (1920, 1080) && NewProjectRequest.FrameFor("9:16", 1080, 8, 8) == (1080, 1920),
            "a custom size is taken as it is, made even; an unknown aspect is 16:9; a custom size under 16 pixels is no custom size");
        var request = new NewProjectRequest("Clip Night", null, 1080, 1920, null, ProjectColorSpace.Sdr709, null, ["D:/a.mp4", "D:/b.mp4"]);
        expect(request is { FrameRate: null, StylePath: null, Files.Count: 2 }, "a request carries the files in order, Auto rate as null, no Game Preset as null");

        // The Media Browser as a picker: the dock's Media title slot stays the dock's, and nothing it does writes the Media pane's settings.
        var settings = AppSettings.Current;
        var kept = (settings.BrowserFolder, settings.BrowserSort, settings.BrowserSortDescending, settings.BrowserTileSize, settings.BrowserShowsProject, settings.BrowserShowsLibrary,
            settings.BrowserView, settings.BrowserFilmstripListWidth);
        var before = PaneTitleSlot.Of("Media");
        var docked = new PaneTitleSlot { PaneId = "Media" };
        try
        {
            var picker = new MediaBrowserPane(pickerMode: true);
            expect(ReferenceEquals(PaneTitleSlot.Of("Media"), docked), "a picker made after the dock's Media pane leaves PaneTitleSlot.Of(\"Media\") the dock's");
            expect(picker.PickerMode && picker.ProjectSide.Visibility == Visibility.Collapsed && picker.Items.SelectionMode == SelectionMode.Multiple && picker.SelectedFiles.Count == 0,
                "the picker hides Project Media and chooses files by a click each, none chosen yet");
            picker.SortBox.SelectedIndex = (picker.SortBox.SelectedIndex + 1) % 4;
            picker.SortDirectionButton.IsChecked = picker.SortDirectionButton.IsChecked != true;
            picker.TileSize = settings.BrowserTileSize >= 50 ? 10 : 90;
            (picker.LibrarySide.IsChecked == true ? picker.FolderSide : picker.LibrarySide).IsChecked = true;
            picker.ViewMode = MediaBrowserView.List;
            picker.ViewMode = MediaBrowserView.Filmstrip;
            var now = (settings.BrowserFolder, settings.BrowserSort, settings.BrowserSortDescending, settings.BrowserTileSize, settings.BrowserShowsProject, settings.BrowserShowsLibrary,
                settings.BrowserView, settings.BrowserFilmstripListWidth);
            expect(now == kept, "sorting, the tile size and Folder Browser / Clip Library in the picker write none of the Media pane's Browser settings");
            expect(picker.ViewMode == MediaBrowserView.Grid && picker.FilmstripChoice.Visibility == Visibility.Collapsed,
                "nor does its view (5.4C: Grid or List; Filmstrip is not offered, and asked for it is the Grid)");
            picker.Release();
            expect(picker.SelectedFiles.Count == 0, "Release lets go of what it holds as the dialog closes");
        }
        finally
        {
            PaneTitleSlot.Restore("Media", before);
            (settings.BrowserFolder, settings.BrowserSort, settings.BrowserSortDescending, settings.BrowserTileSize, settings.BrowserShowsProject, settings.BrowserShowsLibrary,
                settings.BrowserView, settings.BrowserFilmstripListWidth) = kept;
        }
    }

    // ----- Files of the repository -----

    /// <summary>The repository's folder (where HandPegApp.csproj is), found upwards from the built program.</summary>
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
