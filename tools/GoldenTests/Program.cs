using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace HandPegGoldenTests;

/// <summary>
/// HandPeg's golden-file tests: the export made of each fixture (its encode, its GPU scenes, its sound), the .hproj format's reading and writing,
/// and FfmpegRunner's handling of a command, compared with the files under golden\, plus the checks made by code (the
/// A/V invariant, the project round trip). Run by tools\GoldenTests.ps1; "--update" writes the golden files anew.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // The compositor's worker (and the hardware benchmark) are started as this program again, as HandPeg starts
        // HandPeg.exe (WorkerProcess.ProgramPath is the running program): the same entry points, nothing else.
        if (args.Length > 0 && args[0] == HandPegApp.Compositor.CompositorWorker.Argument)
            return HandPegApp.Compositor.CompositorWorker.Run(args);
        if (args.Length > 0 && args[0] == HandPegApp.Compositor.HardwareBenchmark.Argument)
            return HandPegApp.Compositor.HardwareBenchmark.Run(args);

        // Everything HandPeg would keep (settings, caches, logs, the masks folder) goes into a folder of the test's own,
        // never the user's: set before any of HandPeg's code runs.
        var data = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("HANDPEG_DATA_DIR", data);

        var exit = 1;
        var thread = new Thread(() => exit = Run(args, data));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // HANDPEG_GOLDEN_KEEP_DATA=1 keeps it, the app's own log among it (logs\HandPeg.log), to read after a failure.
        try
        {
            if (Environment.GetEnvironmentVariable("HANDPEG_GOLDEN_KEEP_DATA") == "1")
                Console.WriteLine($"  note  the data folder is kept: {data}");
            else
                Directory.Delete(data, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return exit;
    }

    private static int Run(string[] args, string data)
    {
        var started = Stopwatch.StartNew();
        var update = args.Contains("--update");
        var filter = Value(args, "--filter");
        var root = Value(args, "--root") ?? FindRoot();
        var goldenFolder = Path.Combine(root, "golden");

        var results = new List<GraphResult>();
        var graphs = GraphCases.All().ToList();
        results.AddRange(graphs);
        results.AddRange(ProjectTests.All(Path.Combine(root, "fixtures"), Path.Combine(data, "scratch")));
        results.AddRange(RunnerTests.All(graphs));
        results.AddRange(TimelineDocumentTests.All());
        results.AddRange(TimelineGraphCases.All());
        results.Add(GraphCases.GpuAvInvariant());
        results.AddRange(CaptionTests.All());
        results.AddRange(WaveformTests.All());
        results.AddRange(CutoutTests.All());
        results.AddRange(DogfoodTests.All());
        results.AddRange(GpuFxTests.All());
        if (filter is null || "creator-hotbar".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(HotbarTests.Run());
        if (filter is null || "portable-bootstrap".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(PortableBootstrapTests.Run());
        results.AddRange(TimelineErgonomicsTests.All());
        results.AddRange(TimelineInputTests.All());
        results.AddRange(TimelineLaneTests.All());
        // timeline-effect-lanes-fold, timeline-effect-lane-cards, timeline-settings-menu (5.3): effect lanes only on an open track, a Cutout's
        // own cards off them, and the Timeline Settings menu.
        results.AddRange(TimelineSettingsTests.All());
        // timeline-stack-sound, live-preview-gone (5.3-Stage2): a video clip's audible Sub-Lanes drawn as one waveform across its block, and
        // the Live Preview switch gone (the player shows the clip's own file only for Interactive Crop and a Draw Target).
        results.AddRange(TimelineStackSoundTests.All());
        results.AddRange(PeMachineTests.All());
        results.AddRange(MediaFoundationTests.All());
        results.AddRange(HardwareTierTests.All());
        results.AddRange(AudioEngineTests.All());
        results.AddRange(AudioSuiteTests.All());
        results.AddRange(StreamerToolsTests.All());
        results.AddRange(CleaveTests.All());
        // startup-json-source-generation (5.2D): the start-up reads through generated metadata, byte for byte as reflection's.
        results.Add(StartupJsonTests.Run());
        // project-thumbnail, explorer-thumbnails (5.2D): baked project thumbnails, and Explorer's preview of them.
        results.Add(ProjectThumbnailTests.ProjectThumbnails());
        results.Add(ProjectThumbnailTests.ExplorerPreviews());
        // audio-chain-typed: a track's processing made typed (AudioChainBuilder.From) against the FFmpeg words it replaced, read back as before (5.2-AuditB).
        results.Add(MigrationTests.AudioChainTyped());
        // hproj-format6-clips (5.2-Arm64AndNpu, in place of hproj-main-video-migration): a project of clips written and opened again, and
        // a 5.1 project's main video read as it is and told apart (LegacyMainVideo converts it as the view model opens it).
        results.Add(MigrationTests.Format6Clips());
        // hproj-51-import (5.2E, the user's answer): a 5.1 project opened in the view model, converted, and written over its
        // own file under the same name by the window only (a harness's opening leaves it as it was).
        if (filter is null || "hproj-51-import".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(MigrationTests.Import51());
        if (filter is null || "compositor-parity".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.AddRange(CompositorParityTests.All());
        if (filter is null || "gpu-scene-differential".Contains(filter, StringComparison.OrdinalIgnoreCase) || "gpu-scene-plan".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.AddRange(GpuSceneDifferentialTests.All());
        if (filter is null || "gpu-export-command".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(GpuExportTests.Commands());
        if (filter is null || "gpu-export".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(GpuExportTests.Exports());
        if (filter is null || "gpu-export-formats".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(GpuExportTests.Formats());
        // copy-export-refused (5.2-Arm64AndNpu, the user's answer 5): a Copy export that cannot cut is refused before it starts.
        results.Add(GpuExportTests.CopyRefusals());
        if (filter is null || "gpu-export-every-frame".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(GpuExportTests.EveryFrame());
        if (filter is null || "gpu-program-strip".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(GpuExportTests.ProgramStrip());
        if (filter is null || "gpu-filmstrip".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.AddRange(GpuFilmstripTests.All());
        if (filter is null || "gpu-scene-clip-effect".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(GpuSceneClipEffectTests.Run());
        if (filter is null || "gpu-picture-subtitles".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(PictureSubtitleTests.Run());
        if (filter is null || "window-maximized-edge".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(WindowFrameTests.MaximizedEdge());
        // window-frame-messages (5.3): what the custom-frame hook answers, and what still reaches Windows below WindowChrome.
        if (filter is null || "window-frame-messages".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(WindowFrameTests.Messages());
        // dock-standard-layout, layout-edge-to-edge, lazy-settings-tabs (5.3-Stage2): Standard Editing's stacked middle column and its
        // upgrade, the window's edges with no dead space, and the Summary and Export tabs made only when shown.
        results.AddRange(LayoutTests.All());
        // time-display-tabular, media-browser-autofill-columns (5.3.1): the running times held still, the Media Browser's tiles filling the pane.
        results.AddRange(ReadoutAndTileTests.All());
        // exploded-3d-transition-pacing (a compositor worker: the 3D view's way in and out make no texture after their first frame) and
        // exploded-3d-gestures (5.3-Stage3): the 3D view's camera curve, its wheel, touchpad and touch.
        if (filter is null || "exploded-3d-transition-pacing".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(ExplodedViewTests.Pacing());
        results.Add(ExplodedViewTests.Gestures());
        // Pass 5.4A: the NLE fixes (a clip onto a new track, the blur under every picture, drops, the export's end, the pill), Project
        // Settings with the Summary tab retired and the New Project studio, and the title bar's project tabs.
        results.AddRange(NleFixesTests.All());
        results.AddRange(ProjectSettingsTests.All());
        results.AddRange(ProjectTabsTests.All());
        // Pass 5.4B: the title-bar tabs shown only for a project (and dragged, their menu and card), the unified Export Presets and
        // the 3-column Export tab with its defaults and HDR passthrough, and the Export Quality Preview (the encode loopback).
        results.AddRange(ProjectTabsPolishTests.All());
        results.AddRange(ExportPresetsTests.All());
        if (filter is null || "export-loopback".Contains(filter, StringComparison.OrdinalIgnoreCase) || "export-hdr-readback".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.AddRange(ExportLoopbackTests.All());
        // Pass 5.4C: the Project Media bin and the Media Browser's three views, momentum scrolling, the studios as dock panes with the
        // new Vertical & Shorts Studio layout, and any number of side windows.
        results.AddRange(MediaBrowserTests.All());
        results.AddRange(MomentumTests.All());
        results.AddRange(StudioPanesTests.All());
        results.AddRange(SideWindowTests.All());
        // Pass 5.4D: the dock splitter and the unified Layout selector with the default startup layout (A), the Live Encoded Preview on
        // the player bar and the 5.4C answers (B), each project tab's own 3D view and window layout (C).
        results.AddRange(LayoutSelectorTests.All());
        results.AddRange(EncodedPreviewQaTests.All());
        results.AddRange(TabWindowStateTests.All());
        // Pass 5.4D-TabPerf: tabs kept live and swapped (T), the worker's frame cache and memory (W), the managed heap (H).
        results.AddRange(TabPerfTests.All());
        results.AddRange(WorkerMemoryTests.All());
        results.AddRange(MemoryHygieneTests.All());
        // Pass 5.4.1A: Render Preview retired (A), the player resized with no stretch and the split wipe without the encoder's wait (B).
        results.AddRange(RenderPreviewRetiredTests.All());
        results.AddRange(ViewportResizeTests.All());
        // Last of all: its tour needs the WPF Application it makes, and a view model works otherwise once there is one (its command
        // and its undo steps go through the dispatcher, which nothing here pumps), so no test after it would see what it saw alone.
        results.AddRange(UiScaleTests.All());
        // header-flyouts-placement (5.3-Stage3): the header's flyouts against their buttons, a real dock laying out the four built-in
        // layouts under an open one; after the UI-scale tests, on the WPF Application they made.
        if (filter is null || "header-flyouts-placement".Contains(filter, StringComparison.OrdinalIgnoreCase))
            results.Add(HeaderFlyoutTests.Run());
        // Pass 5.4D's cases on the WPF Application made above.
        results.AddRange(LayoutSelectorTests.Live());
        results.AddRange(EncodedPreviewQaTests.Live());
        results.AddRange(TabWindowStateTests.Live());
        results.AddRange(TabPerfTests.Live());
        results.AddRange(WorkerMemoryTests.Live());
        results.AddRange(MemoryHygieneTests.Live());
        if (filter is not null)
            results = results.Where(r => r.Golden.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var (failed, written, moments) = (0, 0, 0);
        foreach (var result in results)
        {
            moments += result.CheckedMoments;
            var path = Path.Combine(goldenFolder, result.Golden + ".txt");
            var actual = result.Text.ReplaceLineEndings("\n");
            var expected = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
            var problems = new List<string>(result.Problems);

            if (expected != actual)
            {
                if (update)
                {
                    Directory.CreateDirectory(goldenFolder);
                    File.WriteAllText(path, actual, new UTF8Encoding(false));
                    written++;
                }
                else
                {
                    problems.Insert(0, expected is null
                        ? $"no golden file (golden\\{result.Golden}.txt): run with -Update to write it"
                        : $"differs from golden\\{result.Golden}.txt (- golden, + now):\n{LineDiff.Describe(expected, actual)}");
                }
            }

            if (problems.Count == 0)
            {
                Console.WriteLine($"  ok    {result.Golden}");
                continue;
            }

            failed++;
            Console.WriteLine($"  FAIL  {result.Golden}");
            foreach (var problem in problems)
                Console.WriteLine("        " + problem.Replace("\n", "\n        ", StringComparison.Ordinal).TrimEnd());
        }

        // Golden files no case writes any more.
        if (filter is null && Directory.Exists(goldenFolder))
        {
            var names = results.Select(r => r.Golden + ".txt").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in Directory.EnumerateFiles(goldenFolder, "*.txt").Where(f => !names.Contains(Path.GetFileName(f))))
                Console.WriteLine($"  note  golden\\{Path.GetFileName(stale)} belongs to no test any more");
        }

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{results.Count - failed} of {results.Count} passed{(update ? $", {written} golden file(s) written" : "")}; {moments} moments followed by the A/V check; {started.Elapsed.TotalSeconds:0.00} s"));
        return failed == 0 ? 0 : 1;
    }

    private static string? Value(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>The project's folder (where golden\ and fixtures\ are), found upwards from the built program.</summary>
    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "GoldenTests.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("GoldenTests.csproj was not found above the program: pass --root.");
    }
}
