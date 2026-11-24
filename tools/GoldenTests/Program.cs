using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace HandPegGoldenTests;

/// <summary>
/// HandPeg's golden-file tests: the export made of each fixture (its encode, its GPU scenes, its sound), the .hproj format's
/// reading and writing, and FfmpegRunner's handling of a command, compared with the files under golden\, plus the checks made
/// by code (the A/V invariant, the project round trip). Run by tools\GoldenTests.ps1; "--update" writes the golden files anew;
/// "--filter a|b" runs only the goldens whose names contain one of the terms (a case the filter leaves out never runs).
/// </summary>
/// <remarks>
/// Exit codes: 0 every golden passed; 1 at least one failed; 3 a filter term matched no golden (a typo or a renamed golden).
/// </remarks>
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
        var root = Value(args, "--root") ?? FindRoot();
        GoldenFilter.Configure(Value(args, "--filter"));
        var run = new GoldenRunner(Path.Combine(root, "golden"), update: args.Contains("--update"));

        // Registration order is execution order: several suites rely on state the earlier ones leave (see EnsureApplication).
        var graphs = run.Suite("GraphCases", GraphCases.All);
        run.Suite("ProjectTests", () => ProjectTests.All(Path.Combine(root, "fixtures"), Path.Combine(data, "scratch")));
        run.Suite("PersistenceTests", () => PersistenceTests.All(Path.Combine(data, "scratch")));
        run.Suite("TrustBoundaryTests", () => TrustBoundaryTests.All(Path.Combine(data, "scratch")));
        run.Suite("FaultBoundaryTests", FaultBoundaryTests.All);
        run.Suite("ThreadLifetimeTests", ThreadLifetimeTests.All);
        run.Suite("HotPathTests", HotPathTests.All);
        // runner-quoting round-trips every fixture's command, so a filtered run that wants it makes all of them first.
        if (GoldenFilter.Wanted("runner-quoting"))
            run.Suite("RunnerTests", () => RunnerTests.All(GoldenFilter.Active ? GoldenFilter.Unfiltered(() => GraphCases.All().ToList()) : graphs));
        run.Suite("TimelineDocumentTests", TimelineDocumentTests.All);
        run.Suite("ModelSeamTests", ModelSeamTests.All);
        run.Suite("ShellBudgetTests", ShellBudgetTests.All);
        run.Suite("ShellArchitectureTests", ShellArchitectureTests.All);
        run.Suite("TimelineGraphCases", TimelineGraphCases.All);
        run.One("gpu-av-invariant", GraphCases.GpuAvInvariant);
        run.Suite("CaptionTests", CaptionTests.All);
        run.Suite("WaveformTests", WaveformTests.All);
        run.Suite("CutoutTests", CutoutTests.All);
        run.Suite("DogfoodTests", DogfoodTests.All);
        run.Suite("GpuFxTests", GpuFxTests.All);
        run.Suite("ShaderNamesTests", ShaderNamesTests.All);
        run.Suite("GoldenKitTests", GoldenKitTests.All);
        run.Suite("SharedLayoutTests", SharedLayoutTests.All);
        run.Suite("ContainerParserTests", ContainerParserTests.All);
        run.One("creator-hotbar", HotbarTests.Run);
        run.One("portable-bootstrap", PortableBootstrapTests.Run);
        run.Suite("TimelineErgonomicsTests", TimelineErgonomicsTests.All);
        run.Suite("TimelineInputTests", TimelineInputTests.All);
        run.Suite("StudioCaptureTests", StudioCaptureTests.All);
        run.Suite("TimelineLaneTests", TimelineLaneTests.All);
        run.Suite("TimelineSettingsTests", TimelineSettingsTests.All);
        run.Suite("TimelineStackSoundTests", TimelineStackSoundTests.All);
        run.Suite("PeMachineTests", PeMachineTests.All);
        run.Suite("MediaFoundationTests", MediaFoundationTests.All);
        run.Suite("HardwareTierTests", HardwareTierTests.All);
        run.Suite("AudioEngineTests", AudioEngineTests.All);
        run.Suite("AudioSuiteTests", AudioSuiteTests.All);
        run.Suite("StreamerToolsTests", StreamerToolsTests.All);
        run.Suite("CleaveTests", CleaveTests.All);
        run.One("startup-json-source-generation", StartupJsonTests.Run);
        run.One("project-thumbnail", ProjectThumbnailTests.ProjectThumbnails);
        run.One("explorer-thumbnails", ProjectThumbnailTests.ExplorerPreviews);
        run.One("audio-chain-typed", MigrationTests.AudioChainTyped);
        run.One("hproj-format6-clips", MigrationTests.Format6Clips);
        run.One("hproj-51-import", MigrationTests.Import51);
        run.Suite("CompositorParityTests", CompositorParityTests.All);
        run.Suite("GpuSceneDifferentialTests", GpuSceneDifferentialTests.All);
        run.One("gpu-export-command", GpuExportTests.Commands);
        run.One("gpu-export", GpuExportTests.Exports);
        run.One("gpu-export-formats", GpuExportTests.Formats);
        run.One("copy-export-refused", GpuExportTests.CopyRefusals);
        run.One("gpu-export-every-frame", GpuExportTests.EveryFrame);
        run.One("gpu-program-strip", GpuExportTests.ProgramStrip);
        run.Suite("GpuFilmstripTests", GpuFilmstripTests.All);
        run.One("gpu-scene-clip-effect", GpuSceneClipEffectTests.Run);
        run.One("gpu-picture-subtitles", PictureSubtitleTests.Run);
        run.One("window-maximized-edge", WindowFrameTests.MaximizedEdge);
        run.One("window-frame-messages", WindowFrameTests.Messages);
        run.Suite("LayoutTests", LayoutTests.All);
        run.Suite("ReadoutAndTileTests", ReadoutAndTileTests.All);
        run.One("exploded-3d-transition-pacing", ExplodedViewTests.Pacing);
        run.One("exploded-3d-gestures", ExplodedViewTests.Gestures);
        run.Suite("NleFixesTests", NleFixesTests.All);

        // From here on every suite runs with the WPF Application and its dispatcher present, filtered or not.
        EnsureApplication();
        run.Suite("ProjectSettingsTests", ProjectSettingsTests.All);
        run.Suite("EngineCorrectnessTests", EngineCorrectnessTests.All);
        run.Suite("InvalidationTests", InvalidationTests.All);
        run.Suite("ProjectTabsTests", ProjectTabsTests.All);
        run.Suite("ProjectTabsPolishTests", ProjectTabsPolishTests.All);
        run.Suite("ExportPresetsTests", ExportPresetsTests.All);
        run.Suite("ExportLoopbackTests", ExportLoopbackTests.All);
        run.Suite("MediaBrowserTests", MediaBrowserTests.All);
        run.Suite("MomentumTests", MomentumTests.All);
        run.Suite("StudioPanesTests", StudioPanesTests.All);
        run.Suite("SideWindowTests", SideWindowTests.All);
        run.Suite("LayoutSelectorTests", LayoutSelectorTests.All);
        run.Suite("EncodedPreviewQaTests", EncodedPreviewQaTests.All);
        run.Suite("TabWindowStateTests", TabWindowStateTests.All);
        run.Suite("TabPerfTests", TabPerfTests.All);
        run.Suite("WorkerMemoryTests", WorkerMemoryTests.All);
        run.Suite("MemoryHygieneTests", MemoryHygieneTests.All);
        run.Suite("RenderPreviewRetiredTests", RenderPreviewRetiredTests.All);
        run.Suite("ViewportResizeTests", ViewportResizeTests.All);
        run.Suite("ScrubTests", ScrubTests.All);
        run.Suite("MultiTabIsolationTests", MultiTabIsolationTests.All);
        run.Suite("PreviewScalingTests", PreviewScalingTests.All);
        run.Suite("WaveContract5_6_4ATests", WaveContract5_6_4ATests.All);
        run.Suite("WaveContract5_6_4B_ReleaseTests", WaveContract5_6_4B_ReleaseTests.All);
        run.Suite("WaveTilesZoomTests", WaveTilesZoomTests.All);
        run.Suite("PlayerAndLibraryUiTests", PlayerAndLibraryUiTests.All);
        run.Suite("TabsDockTransportTests", TabsDockTransportTests.All);
        run.Suite("LiveEncodedFidelityTests", LiveEncodedFidelityTests.All);
        run.Suite("TimelineParentingTests", TimelineParentingTests.All);
        run.Suite("WipePillTests", WipePillTests.All);
        run.Suite("ColdPlayTests", ColdPlayTests.All);
        run.Suite("FirstRunStudioTests", FirstRunStudioTests.All);
        run.Suite("LaggedRecoveryTests", LaggedRecoveryTests.All);
        run.Suite("TabExitTests", TabExitTests.All);
        run.Suite("AutoTestFixesTests", AutoTestFixesTests.All);
        run.Suite("PlaybackSyncTests", PlaybackSyncTests.All);
        run.Suite("ColorStudioTests", ColorStudioTests.All);
        run.Suite("TierBadgeTests", TierBadgeTests.All);
        run.Suite("ColorMatchTests", ColorMatchTests.All);
        run.Suite("SpeedTimeTests", SpeedTimeTests.All);
        run.Suite("CutPacingTests", CutPacingTests.All);
        run.Suite("OverlayDpiTests", OverlayDpiTests.All);
        // The UI-scale tour and the cases after it drive real windows on the Application (their commands and undo steps
        // go through the dispatcher, which nothing here pumps between cases).
        run.Suite("UiScaleTests", UiScaleTests.All);
        run.One("header-flyouts-placement", HeaderFlyoutTests.Run);
        run.Suite("LayoutSelectorTests.Live", LayoutSelectorTests.Live);
        run.Suite("EncodedPreviewQaTests.Live", EncodedPreviewQaTests.Live);
        run.Suite("TabWindowStateTests.Live", TabWindowStateTests.Live);
        run.Suite("TabPerfTests.Live", TabPerfTests.Live);
        run.Suite("WorkerMemoryTests.Live", WorkerMemoryTests.Live);
        run.Suite("MemoryHygieneTests.Live", MemoryHygieneTests.Live);
        run.Suite("MultiTabIsolationTests.Live", MultiTabIsolationTests.Live);
        run.Suite("PreviewScalingTests.Live", PreviewScalingTests.Live);
        run.Suite("WaveTilesZoomTests.Live", WaveTilesZoomTests.Live);
        run.Suite("PlayerAndLibraryUiTests.Live", PlayerAndLibraryUiTests.Live);
        run.Suite("TabsDockTransportTests.Live", TabsDockTransportTests.Live);
        run.Suite("TimelineParentingTests.Live", TimelineParentingTests.Live);
        run.Suite("WipePillTests.Live", WipePillTests.Live);
        run.Suite("ColdPlayTests.Live", ColdPlayTests.Live);
        run.Suite("FirstRunStudioTests.Live", FirstRunStudioTests.Live);
        run.Suite("LaggedRecoveryTests.Live", LaggedRecoveryTests.Live);
        run.Suite("TabExitTests.Live", TabExitTests.Live);
        run.Suite("AutoTestFixesTests.Live", AutoTestFixesTests.Live);
        run.Suite("PlaybackSyncTests.Live", PlaybackSyncTests.Live);
        run.Suite("ColorStudioTests.Live", ColorStudioTests.Live);
        run.Suite("TierBadgeTests.Live", TierBadgeTests.Live);
        run.Suite("ColorMatchTests.Live", ColorMatchTests.Live);
        run.Suite("SpeedTimeTests.Live", SpeedTimeTests.Live);
        run.Suite("CutPacingTests.Live", CutPacingTests.Live);
        run.Suite("EngineCorrectnessTests.Live", EngineCorrectnessTests.Live);

        // Last: no thread rule was broken by any suite but the case that breaks one on purpose.
        run.Suite("ThreadLifetimeTests.Final", ThreadLifetimeTests.Final);

        return run.Finish(started.Elapsed);
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

/// <summary>
/// Runs the registered suites in order and compares each result with its golden file as soon as it is made, so the output
/// streams case by case (a hang names the last case that finished) and a slow case or suite is timed.
/// </summary>
internal sealed class GoldenRunner(string goldenFolder, bool update)
{
    private const double SlowCaseSeconds = 2.0;
    private readonly HashSet<string> _produced = new(StringComparer.OrdinalIgnoreCase);
    private int _passed, _failed, _written, _moments;

    /// <summary>Runs a suite and checks each of its results; returns the results that ran (the skipped ones dropped).</summary>
    public IReadOnlyList<GraphResult> Suite(string module, Func<IEnumerable<GraphResult>> suite)
    {
        var ran = new List<GraphResult>();
        var whole = Stopwatch.StartNew();
        var each = Stopwatch.StartNew();
        using (var results = suite().GetEnumerator())
        {
            while (true)
            {
                each.Restart();
                if (!results.MoveNext())
                    break;

                var result = results.Current;
                if (result.Skipped || !GoldenFilter.Wanted(result.Golden))
                    continue;

                ran.Add(result);
                Check(result);
                if (each.Elapsed.TotalSeconds >= SlowCaseSeconds)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  time  {result.Golden} {each.Elapsed.TotalSeconds:0.0} s"));
            }
        }

        if (ran.Count > 1 && whole.Elapsed.TotalSeconds >= SlowCaseSeconds)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  time  [{module}] {whole.Elapsed.TotalSeconds:0.0} s"));
        return ran;
    }

    /// <summary>Runs and checks a suite of one golden, only when the filter wants it.</summary>
    public void One(string golden, Func<GraphResult> make) => Suite(golden, () => [Lazy(golden, make)]);

    /// <summary>Prints the summary (and the golden files no case wrote) and returns the exit code.</summary>
    public int Finish(TimeSpan elapsed)
    {
        var exit = _failed == 0 ? 0 : 1;
        if (GoldenFilter.Active)
        {
            foreach (var term in GoldenFilter.Unmatched(_produced))
            {
                Console.WriteLine($"  FAIL  no golden matches '{term}'");
                exit = 3;
            }
        }
        else if (Directory.Exists(goldenFolder))
        {
            foreach (var stale in Directory.EnumerateFiles(goldenFolder, "*.txt").Where(f => !_produced.Contains(Path.GetFileNameWithoutExtension(f))))
                Console.WriteLine($"  note  golden\\{Path.GetFileName(stale)} belongs to no test any more");
        }

        var total = _passed + _failed;
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{_passed} of {total} passed{(update ? $", {_written} golden file(s) written" : "")}; {_moments} moments followed by the A/V check; {elapsed.TotalSeconds:0.00} s"));
        return exit;
    }

    private void Check(GraphResult result)
    {
        _moments += result.CheckedMoments;
        var problems = new List<string>(result.Problems);
        if (!_produced.Add(result.Golden))
            problems.Insert(0, $"a second case writes golden\\{result.Golden}.txt (golden names must be unique)");

        var path = Path.Combine(goldenFolder, result.Golden + ".txt");
        var actual = result.Text.ReplaceLineEndings("\n");
        var expected = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
        if (expected != actual)
        {
            // --update writes only a passing case: a golden must never record a failing state.
            if (update && problems.Count == 0)
            {
                Directory.CreateDirectory(goldenFolder);
                File.WriteAllText(path, actual, new UTF8Encoding(false));
                _written++;
            }
            else if (update)
            {
                problems.Insert(0, $"golden\\{result.Golden}.txt was not written: the case has problems");
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
            _passed++;
            Console.WriteLine($"  ok    {result.Golden}");
            return;
        }

        _failed++;
        Console.WriteLine($"  FAIL  {result.Golden}");
        foreach (var problem in problems)
            Console.WriteLine("        " + problem.Replace("\n", "\n        ", StringComparison.Ordinal).TrimEnd());
    }
}
