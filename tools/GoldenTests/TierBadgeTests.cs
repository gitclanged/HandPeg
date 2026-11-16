using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.6A lane B: the S-F hardware tiers, the role cards, the first run's full test and Settings' stage cards.
/// hardware-tier-badges (pure): every machine of the calibration table in its grade (the XPS 13 B, the Plex Server C, the Desktop S, an
/// RTX 5090 100, an N100 D, an Atom and WARP F), with and without the processor's decode (a quick check's), the ladders' anchors and
/// the grade thresholds, the per-role pills (Compositor from its frame, Decode and Background from their card, Processor Only from the
/// processor, Export capped by its encoders) and the NPU's none. first-run-full-test (pure and source): the full test on every first-run
/// open from a pool thread, once more when FFmpeg is installed, without a test clip when there is no FFmpeg (the bus and the
/// compositor), the compositor stage's scene and cards, the removed status line, paragraph and software-fallback box, the picked roles
/// written. role-cards-panel (on the WPF Application, a window off-screen): the cards' automation ids, menus for a two-card + NPU
/// machine, a pick raising RoleChosen, the first run keeping it and writing it, Settings' stage cards from its copy of the settings.
/// </summary>
public static class TierBadgeTests
{
    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("hardware-tier-badges", "# The S-F tiers (5.6A): the calibration machines in their grades, the ladders' anchors, the thresholds, the per-role pills, the NPU's none.", TierBadges);
        yield return Case("first-run-full-test", "# The first run's full test (5.6A): every open, from a pool thread, again with FFmpeg, without a clip before it; the compositor stage; the de-clutter; picked roles.", FullTest);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("role-cards-panel", "# The role cards (5.6A): automation ids, each role's menu on a two-card + NPU machine, a pick raised and kept, Settings' stage cards.", RoleCardsPanelCase);
    }

    // ----- The calibration table (keep in step with PerformanceTiers' arrays: the orchestrator may retune both) -----

    private static readonly string[] AllCodecs = ["H.264", "HEVC", "AV1"];
    private static readonly string[] MediaFoundationAll = ["H.264", "HEVC", "VP9", "AV1"];

    /// <summary>
    /// A machine measured: its compositor card's hardware decode (fps), the processor's decode (fps, 0 not measured), the compositor's
    /// all-slots frame (ms), the codecs the card decodes in hardware, those Media Foundation decodes here, the grade it must get.
    /// </summary>
    private sealed record Machine(string Name, double Gpu, double Cpu, double CompositorMs, string[] Decodes, string[] MediaFoundation, TierGrade Grade, string Source);

    private static readonly Machine[] Machines =
    [
        new("XPS 13 (Adreno X1-85), 623 fps", 623, 782, 5.088, AllCodecs, MediaFoundationAll, TierGrade.B, "measured: full test 623 fps, CPU 782, compositor 5.088 ms"),
        new("XPS 13 (Adreno X1-85), 687 fps", 687.5, 782, 5.088, AllCodecs, MediaFoundationAll, TierGrade.B, "measured: quick check 687.5 fps (2026-11-13)"),
        new("Plex Server (GTX 1070, Ryzen 5 2600), 608 fps", 608, 387, 2.842, ["H.264", "HEVC", "HEVC 10-bit", "VP9"], ["H.264", "VP9"], TierGrade.C, "measured: LTSC, no Media Foundation HEVC"),
        new("Plex Server (GTX 1070, Ryzen 5 2600), 734 fps", 734, 481, 2.842, ["H.264", "HEVC", "HEVC 10-bit", "VP9"], ["H.264", "VP9"], TierGrade.C, "measured: the top of its range"),
        new("Desktop (RX 7900 XTX, 7950X3D)", 1170, 680, 0.551, AllCodecs, MediaFoundationAll, TierGrade.S, "measured 2026-11-14: decode 1,161-1,188 fps, CPU 657-710, compositor 0.551 ms"),
        new("RTX 5090", 3200, 2400, 0.20, AllCodecs, MediaFoundationAll, TierGrade.S, "estimate"),
        new("Intel N100, 120 fps", 120, 200, 12, AllCodecs, MediaFoundationAll, TierGrade.D, "estimate"),
        new("Intel N100, 250 fps", 250, 200, 12, AllCodecs, MediaFoundationAll, TierGrade.D, "estimate"),
        new("Atom", 100, 100, 40, ["H.264"], ["H.264"], TierGrade.F, "estimate"),
        new("WARP (no hardware decode)", 0, 80, 60, [], ["H.264"], TierGrade.F, "estimate"),
    ];

    private static HardwareBenchmarkResult Result(Machine machine, bool quick = false) => new()
    {
        Kind = quick ? HardwareBenchmarkResult.KindQuick : "",
        Clip = "H.264 1920x1080 60 fps, 90 frames",
        MediaFoundationDecodeCodecs = [.. machine.MediaFoundation],
        Adapters =
        [
            new HardwareBenchmarkAdapter
            {
                Index = 0, Name = machine.Name, DedicatedVideoMemoryMb = 8192, DecodeCodecs = [.. machine.Decodes], EncodeCodecs = ["H.264", "HEVC", "AV1"],
                HardwareDecodeFps = machine.Gpu, SoftwareDecodeFps = quick ? 0 : machine.Cpu, CompositorMs = machine.CompositorMs, ReadbackMBps = 5000,
            },
        ],
        Recommended = new HardwareBenchmarkRoles { Compositor = 0 },
    };

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void TierBadges(Action<bool, string> expect)
    {
        foreach (var machine in Machines)
        {
            var tier = PerformanceTiers.System(Result(machine));
            var quick = PerformanceTiers.System(Result(machine, quick: true));
            expect(tier?.Grade == machine.Grade && quick?.Grade == machine.Grade, string.Create(CultureInfo.InvariantCulture,
                $"{machine.Name} ({machine.Source}): System {tier}, without the processor's decode {quick}; must be {machine.Grade}"));
        }

        var desktop = PerformanceTiers.System(Result(Machines[4]));
        var rtx = PerformanceTiers.System(Result(Machines[5]));
        expect(desktop is { Score: >= 77 and <= 82 } && rtx is { Score: 100 }, $"the Desktop near 78 ({desktop}), the RTX 5090 at 100 ({rtx})");
        var xps = Result(Machines[0]);
        expect(PerformanceTiers.SystemTip(xps, PerformanceTiers.System(xps)!.Value) == $"System {PerformanceTiers.System(xps)}: 623 fps GPU, 782 fps CPU, 5.09 ms",
            $"the System tooltip: \"{PerformanceTiers.SystemTip(xps, PerformanceTiers.System(xps)!.Value)}\"");

        // The ladders: each anchor read back exactly, log-interpolated between, held beyond the ends; every weight and anchor in the arrays.
        foreach (var (name, ladder) in new[] { ("hardware decode", PerformanceTiers.HardwareDecodeLadder), ("processor decode", PerformanceTiers.SoftwareDecodeLadder), ("compositor", PerformanceTiers.CompositorLadder) })
        {
            var exact = ladder.All(a => Math.Abs(PerformanceTiers.Ladder(ladder, a.Value) - a.Score) < 1e-9);
            var (first, second) = (ladder[2], ladder[3]);
            var middle = PerformanceTiers.Ladder(ladder, Math.Sqrt(first.Value * second.Value));
            var held = PerformanceTiers.Ladder(ladder, ladder[0].Value / 10) == ladder[0].Score && PerformanceTiers.Ladder(ladder, ladder[^1].Value * 10) == ladder[^1].Score;
            expect(exact && Math.Abs(middle - (first.Score + second.Score) / 2) < 1e-9 && held && double.IsNaN(PerformanceTiers.Ladder(ladder, 0)),
                $"the {name} ladder ({string.Join(" ", ladder.Select(a => $"{F(a.Value)}:{F(a.Score)}"))}): anchors exact, the log middle halfway, held beyond its ends, nothing for 0");
        }

        expect(PerformanceTiers.SystemWeights.SequenceEqual([0.35, 0.20, 0.45]) && PerformanceTiers.BreadthFloor == 0.4
               && PerformanceTiers.BreadthCodecs.SequenceEqual([("HEVC", 0.5), ("AV1", 0.5)]) && PerformanceTiers.EncoderCaps.SequenceEqual([("AV1", 100.0), ("HEVC", 74.0), ("H.264", 54.0)]),
            "the weights: hardware decode 0.35 (kept 0.4 of it for H.264 alone, HEVC and AV1 half the breadth each), processor decode 0.20, compositor 0.45; the Export caps AV1 100, HEVC 74, H.264 54");

        // The thresholds.
        var grades = new[] { 0, 9, 10, 21, 22, 37, 38, 54, 55, 74, 75, 100 }.Select(s => $"{s} {PerformanceTiers.GradeOf(s)}").ToList();
        expect(string.Join(", ", grades) == "0 F, 9 F, 10 D, 21 D, 22 C, 37 C, 38 B, 54 B, 55 A, 74 A, 75 S, 100 S"
               && PerformanceTier.Of(74.5) == new PerformanceTier(75, TierGrade.S) && PerformanceTier.Of(-5).Score == 0 && PerformanceTier.Of(150).Score == 100 && PerformanceTier.Of(double.NaN).Grade == TierGrade.F,
            "the grades: " + string.Join(", ", grades) + "; 74.5 rounds to 75 (S), scores kept within 0..100");

        // Breadth: a codec counts only where the card decodes it AND Media Foundation has a decoder for it here.
        var plex = Result(Machines[2]);
        expect(PerformanceTiers.Breadth(plex, plex.Adapters[0]) == 0 && PerformanceTiers.Breadth(xps, xps.Adapters[0]) == 1,
            "breadth: the Plex Server's HEVC is not counted (LTSC has no Media Foundation HEVC decoder: the processor decodes it), the XPS 13's HEVC and AV1 are");

        // The per-role pills: the Desktop's RX 7900 XTX that composites, and an Arc A310 listed with H.264 encode only so the H.264 cap
        // (54) binds (5.6A review: the real Desktop's A310 encodes AV1 too: its pill is pinned below, from DesktopWithNpu).
        var full = new HardwareBenchmarkResult
        {
            Clip = "H.264 1920x1080 60 fps, 90 frames", MediaFoundationDecodeCodecs = [.. MediaFoundationAll],
            Adapters =
            [
                new() { Index = 0, Name = "AMD Radeon RX 7900 XTX", DedicatedVideoMemoryMb = 24560, DecodeCodecs = [.. AllCodecs], EncodeCodecs = [.. AllCodecs], HardwareDecodeFps = 1170, SoftwareDecodeFps = 680, CompositorMs = 0.551, ReadbackMBps = 9870 },
                new() { Index = 1, Name = "Intel(R) Arc(TM) A310 Graphics", DedicatedVideoMemoryMb = 3956, DecodeCodecs = [.. AllCodecs], EncodeCodecs = ["H.264"], HardwareDecodeFps = 1067, SoftwareDecodeFps = 680, ReadbackMBps = 7020 },
            ],
            Npus = [new HardwareBenchmarkNpu { Name = "An NPU" }],
            Recommended = new HardwareBenchmarkRoles { Compositor = 0 },
        };
        (string Role, int Card, string Tip)[] pills =
        [
            (HardwareRoles.Compositor, HardwareRoles.Auto, PerformanceTiers.ForRole(full, HardwareRoles.Compositor, HardwareRoles.Auto).Tip),
            (HardwareRoles.Compositor, 1, PerformanceTiers.ForRole(full, HardwareRoles.Compositor, 1).Tip),
            (HardwareRoles.Decode, HardwareRoles.Auto, PerformanceTiers.ForRole(full, HardwareRoles.Decode, HardwareRoles.Auto).Tip),
            (HardwareRoles.Decode, 1, PerformanceTiers.ForRole(full, HardwareRoles.Decode, 1).Tip),
            (HardwareRoles.Background, HardwareRoles.ProcessorOnly, PerformanceTiers.ForRole(full, HardwareRoles.Background, HardwareRoles.ProcessorOnly).Tip),
            (HardwareRoles.Export, 0, PerformanceTiers.ForRole(full, HardwareRoles.Export, 0).Tip),
            (HardwareRoles.Export, 1, PerformanceTiers.ForRole(full, HardwareRoles.Export, 1).Tip),
            (HardwareRoles.Npu, HardwareRoles.Auto, PerformanceTiers.ForRole(full, HardwareRoles.Npu, HardwareRoles.Auto).Tip),
        ];
        string[] wanted =
        [
            "Compositor 0.55 ms a frame · S (92)", "", "Decode 1,170 fps · S (80)", "Decode 1,067 fps · S (77)", "Processor 680 fps · B (46)",
            "Export AV1 in hardware, 1,170 fps · S (80)", "Export H.264 in hardware, 1,067 fps · B (54)", "",
        ];
        for (var i = 0; i < pills.Length; i++)
        {
            var (role, card, tip) = pills[i];
            expect(tip == wanted[i], $"{role} on {(card == HardwareRoles.Auto ? "Auto" : card == HardwareRoles.ProcessorOnly ? "Processor Only" : $"card {card}")}: \"{tip}\"{(tip.Length == 0 ? " (no pill)" : "")}");
        }

        var realA310 = PerformanceTiers.ForRole(DesktopWithNpu(), HardwareRoles.Export, 1).Tip;
        expect(realA310 == "Export AV1 in hardware, 1,067 fps · S (77)", $"the real Desktop's A310 (AV1, uncapped) on Export: \"{realA310}\"");

        var noEncoder = new HardwareBenchmarkResult { Adapters = [new() { Index = 0, Name = "A card", HardwareDecodeFps = 500, SoftwareDecodeFps = 450 }] };
        var estimate = new HardwareBenchmarkResult { Kind = HardwareBenchmarkResult.KindEstimate, Adapters = [new() { Index = 0, Name = "A card", DecodeCodecs = [], EncodeCodecs = [.. AllCodecs] }] };
        expect(PerformanceTiers.ForRole(noEncoder, HardwareRoles.Export, 0).Tip == "Export in software · C (32)" && PerformanceTiers.System(estimate) is null
               && PerformanceTiers.ForRole(estimate, HardwareRoles.Decode, 0).Tier is null && PerformanceTiers.ForRole(estimate, HardwareRoles.Compositor, 0).Tier is null,
            $"a card with no hardware encoder: Export from the processor (\"{PerformanceTiers.ForRole(noEncoder, HardwareRoles.Export, 0).Tip}\"); an estimate measures nothing: no System tier, no pills");
    }

    // ----- first-run-full-test -----

    private static void FullTest(Action<bool, string> expect)
    {
        var profile = SourceProbe.Src("Dialogs", "FirstRunWindow.Profile.cs");
        var loaded = SourceProbe.Member(profile, "private void Profile_Loaded(");
        var measure = SourceProbe.Member(profile, "private async Task MeasureFullyAsync()");
        expect(loaded.Contains("MeasureFully();", StringComparison.Ordinal) && !loaded.Contains("Owner", StringComparison.Ordinal),
            "the full test runs on every open: a first start (no owner) and opened from Settings alike");
        expect(measure.Contains("Task.Run(() => HardwareBenchmarkService.RunAsync(progress, token), CancellationToken.None)", StringComparison.Ordinal)
               && measure.Contains("var token = _profileCancellation.Token;", StringComparison.Ordinal) && measure.Contains("await _estimate;", StringComparison.Ordinal),
            "started from a pool thread (Task.Run) after the estimate, with the token Start Editing and closing cancel");
        expect(measure.Contains("SetMeasuring(true);", StringComparison.Ordinal) && SourceProbe.Member(profile, "private void SetMeasuring(").Contains("SyncTestCard.BlockedBy = measuring ? \"Waits for the hardware check\" : null;", StringComparison.Ordinal)
               && measure.Contains("if (SyncTestCard.IsRunning)", StringComparison.Ordinal) && measure.Contains("MeasureAfterSyncTest();", StringComparison.Ordinal),
            "while it measures the Playback & A/V Sync Test waits (\"Waits for the hardware check\"); never started while that test runs");
        expect(measure.Contains("HardwareBenchmarkService.LastFailure == HardwareBenchmarkService.BusyFailure", StringComparison.Ordinal) && measure.Contains("ShowWarningLine(BusyWarning);", StringComparison.Ordinal)
               && FirstRunWindow.BusyWarning.StartsWith("Warning: ", StringComparison.Ordinal),
            $"Settings' own test holding the gate is a warning, never an error: \"{FirstRunWindow.BusyWarning}\"");
        expect(measure.Contains("ShowFigures(partial);", StringComparison.Ordinal) && SourceProbe.Member(profile, "private void ShowFigures(").Contains("_figures = partial;", StringComparison.Ordinal)
               && !SourceProbe.Member(profile, "private void ShowFigures(").Contains("_benchmarkRoles", StringComparison.Ordinal),
            "its progress feeds the cards' figures and pills only: the roles stay the estimate's until its result");
        expect(SourceProbe.Member(profile, "private void MeasureAgainWithDecoding()").Contains("MeasureFully();", StringComparison.Ordinal)
               && SourceProbe.Member(profile, "private void MeasureAgainWithDecoding()").Contains("_fullRunning ? _runHasFfmpeg : _decoded", StringComparison.Ordinal),
            "FFmpeg installed: the test runs once more when the last could not decode (or after the one running without FFmpeg)");

        var window = SourceProbe.Src("Dialogs", "FirstRunWindow.xaml.cs") + SourceProbe.Src("Dialogs", "FirstRunWindow.Profile.cs") + SourceProbe.Src("Dialogs", "FirstRunWindow.Screens.cs");
        var xaml = SourceProbe.Src("Dialogs", "FirstRunWindow.xaml");
        expect(!xaml.Contains("x:Name=\"ProfileStatusText\"", StringComparison.Ordinal) && !xaml.Contains("x:Name=\"FallbackBox\"", StringComparison.Ordinal)
               && !xaml.Contains("The full test (seeking, accuracy", StringComparison.Ordinal) && !window.Contains("AutoFallbackToSoftware", StringComparison.Ordinal)
               && !window.Contains("RunQuickAsync", StringComparison.Ordinal) && !window.Contains("ProfileStatusText", StringComparison.Ordinal),
            "gone: the status line, the paragraph under the cards, the software-fallback box (Start Editing no longer writes it) and the quick check");
        expect(xaml.Contains("<local:RoleCardsPanel x:Name=\"RoleCards\" AutomationProperties.Name=\"GPU and NPU Roles\" />", StringComparison.Ordinal)
               && xaml.IndexOf("Text=\"GPU &amp; NPU Roles\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"SystemTierBadge\"", StringComparison.Ordinal)
               && xaml.IndexOf("x:Name=\"SystemTierBadge\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"RoleCards\"", StringComparison.Ordinal),
            "the role cards are RoleCardsPanel (still RoleCards), the System tier badge beside the GPU & NPU Roles title");
        var finish = SourceProbe.Member(SourceProbe.Src("Dialogs", "FirstRunWindow.xaml.cs"), "private void Finish()");
        expect(finish.Contains("if (firstStart && _benchmarkRoles is { } roles", StringComparison.Ordinal) && finish.Contains("if (_rolesChosen", StringComparison.Ordinal)
               && finish.Contains("WritePicks(_picks, settings,", StringComparison.Ordinal)
               && finish.IndexOf("WriteRoles(", StringComparison.Ordinal) < finish.IndexOf("WritePicks(", StringComparison.Ordinal),
            "Start Editing writes the recommended roles on a first start, then the roles picked by hand whatever the start");

        // The service and the worker: no clip before FFmpeg, the compositor stage, --adapters for the roles chosen.
        expect(HardwareBenchmarkService.FullArguments("", "", [-1, -1]) == "--hw-benchmark"
               && HardwareBenchmarkService.FullArguments(@"C:\c\bench.mp4", @"C:\f\ffmpeg.exe", [0, 2, 0]) == "--hw-benchmark \"C:\\c\\bench.mp4\" --ffmpeg \"C:\\f\\ffmpeg.exe\" --adapters 0,2",
            "the full test's command line: no clip and no FFmpeg (a fresh install: the bus and the compositor); with both and the roles chosen in Settings");
        var service = SourceProbe.Member(SourceProbe.Src("Services", "HardwareBenchmarkService.cs"), "private static async Task<HardwareBenchmarkResult?> MeasureAsync(");
        expect(!service.Contains("if (clip is null)", StringComparison.Ordinal) && service.Contains("await EnsureClipAsync(work.Token) ?? \"\"", StringComparison.Ordinal),
            "without FFmpeg (or a clip that could not be made) the test still runs, with no clip");
        var worker = SourceProbe.Member(SourceProbe.Src("Compositor", "HardwareBenchmark.cs"), "private static int Measure(string clip, IReadOnlySet<int> chosen)");
        expect(worker.Contains("var decodes = clip.Length > 0 && File.Exists(clip);", StringComparison.Ordinal) && !worker.Contains("FileNotFoundException", StringComparison.Ordinal)
               && worker.Contains("TimeCompositors(result, CompositorTargets(result, chosen), 0.7, 0.88);", StringComparison.Ordinal)
               && worker.Contains("if (decodes && hardware.Where(", StringComparison.Ordinal),
            "the worker without a clip: no throw, no decoding, no two-card test; the compositor stage on the role cards");

        var noClip = new HardwareBenchmarkResult { Clip = "", Adapters = [new() { Index = 0, Name = "AMD Radeon RX 7900 XTX", DedicatedVideoMemoryMb = 24560, ReadbackMBps = 9870 }] };
        expect(HardwareBenchmark.Recommend(noClip).Reasons.Contains("Decode: Auto, the compositor's card (its decoding is measured once FFmpeg is here)."),
            "a full result with no clip says the decoding is measured once FFmpeg is here");

        var desktop = new HardwareBenchmarkResult
        {
            Adapters =
            [
                new() { Index = 0, Name = "AMD Radeon RX 7900 XTX", VendorId = 0x1002, DeviceId = 0x744C, DedicatedVideoMemoryMb = 24560, EncodeCodecs = [.. AllCodecs] },
                new() { Index = 1, Name = "Intel(R) Arc(TM) A310 Graphics", VendorId = 0x8086, DeviceId = 0x56A6, DedicatedVideoMemoryMb = 3956, EncodeCodecs = [.. AllCodecs] },
                new() { Index = 2, Name = "AMD Radeon RX 7900 XTX", VendorId = 0x1002, DeviceId = 0x744C, DedicatedVideoMemoryMb = 24560, SameCardAs = 0, Note = "The same card as #0, listed twice by Windows." },
            ],
        };
        var timed = HardwareBenchmark.CompositorTargets(desktop, new HashSet<int>()).Select(a => a.Index).ToList();
        var chosen = HardwareBenchmark.CompositorTargets(desktop, new HashSet<int> { 1, 2 }).Select(a => a.Index).ToList();
        expect(timed.SequenceEqual([0]) && chosen.SequenceEqual([0, 1]),
            $"the compositor stage times the role cards only: the Desktop's RX 7900 XTX ({string.Join(",", timed)}); the A310 only when Settings gave it a role ({string.Join(",", chosen)}; a repeat never)");

        // The scene: the compositor-parity golden's Timing scene of 5.5 (the tier anchors were measured on it).
        var layers = new GpuLayerDescriptor[6];
        var header = HardwareBenchmark.TimingScene(layers);
        string Slots(in GpuLayerDescriptor layer)
        {
            var names = new List<string>();
            for (var i = 0; i < layer.EffectCount; i++)
                names.Add(layer.Effects[i].Slot.ToString());
            return string.Join(" ", names);
        }

        var scene = string.Join(" | ", layers.Select(l => Slots(l)));
        expect(scene == "Flip Lut3D Denoise ColorGrade ColorBalance ColorTemperature Vignette HueSaturation ToneCurve ChromaKey MatteChoke MatteSmooth LayerMask CornerFeather BoxBlur Sharpen"
                        + " | DropShadow BlendMode TransitionWipe TransitionBlurDissolve | TransitionWhipPan | TransitionZoomWarp | TransitionRgbGlitch | TransitionSpin"
               && header is { CanvasWidth: 1920, CanvasHeight: 1080, ActiveLayerCount: 6, Flags: FrameSceneHeader.FlagTimePasses | FrameSceneHeader.FlagWaitForTimings }
               && (layers[0].MaskResourceId, layers[0].LutResourceId, layers[0].CurveResourceId) == (1, 0, 2) && (layers[1].BlurFillRadius, layers[1].BlurFillPasses) == (20f, 2)
               && HardwareBenchmark.CompositorClockUpFrames == 90 && HardwareBenchmark.CompositorTimedFrames == 4,
            "the compositor stage's scene is the Timing scene: 1920 x 1080, six test-pattern layers, every 5.5 slot, a LUT, a mask and a curve; 90 clock-up frames, the best of 4");

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var written = JsonSerializer.Serialize(new HardwareBenchmarkAdapter { CompositorMs = 0.551 }, json);
        var old = JsonSerializer.Deserialize<HardwareBenchmarkAdapter>("""{"index":0,"name":"x","hardwareDecodeFps":870}""", json)!;
        expect(written.Contains("\"compositorMs\":0.551", StringComparison.Ordinal) && old.CompositorMs == 0
               && Regex.Matches(SourceProbe.Src("Services", "HardwareBenchmarkModels.cs"), "\"compositor\" \\(5\\.6\\.0\\)").Count == 1,
            "compositorMs is appended to the result's JSON (a result from before reads 0); the progress stage is \"compositor\"");

        // 5.6A review (F19): a run without its test clip never saves over a kept full result of these cards that measured decoding.
        HardwareBenchmarkResult Kept(string fingerprint, double decodeFps, string kind = "") =>
            new() { Kind = kind, Fingerprint = fingerprint, Adapters = [new() { Index = 0, Name = "AMD Radeon RX 7900 XTX", HardwareDecodeFps = decodeFps, ReadbackMBps = 9870 }] };
        var clipLess = new HardwareBenchmarkResult { Fingerprint = "cards-1", Clip = "", Adapters = [new() { Index = 0, Name = "AMD Radeon RX 7900 XTX", ReadbackMBps = 9870, CompositorMs = 0.551 }] };
        var cpuOnly = Kept("cards-1", 0);
        cpuOnly.Adapters[0].SoftwareDecodeFps = 680;
        expect(!HardwareBenchmarkService.ClipLessMayReplace(Kept("cards-1", 1170), clipLess) && !HardwareBenchmarkService.ClipLessMayReplace(cpuOnly, clipLess)
               && HardwareBenchmarkService.ClipLessMayReplace(null, clipLess) && HardwareBenchmarkService.ClipLessMayReplace(Kept("cards-2", 1170), clipLess)
               && HardwareBenchmarkService.ClipLessMayReplace(Kept("cards-1", 1170, HardwareBenchmarkResult.KindQuick), clipLess) && HardwareBenchmarkService.ClipLessMayReplace(Kept("cards-1", 0), clipLess),
            "F19: a run without the test clip keeps a full result of these cards that measured decoding; it replaces none, another card's or driver's, a quick check, or one that decoded nothing");
        var measureAsync = SourceProbe.Member(SourceProbe.Src("Services", "HardwareBenchmarkService.cs"), "private static async Task<HardwareBenchmarkResult?> MeasureAsync(");
        expect(measureAsync.Contains("if (clip.Length > 0 || ClipLessMayReplace(LoadSaved(), result))\n            Save(result);", StringComparison.Ordinal) && measureAsync.TrimEnd().TrimEnd('}').TrimEnd().EndsWith("return result;", StringComparison.Ordinal),
            "F19: the full test saves its result when it had the clip or may replace the kept one, and returns it to show either way");

        // 5.6A review (F16): a Settings test stopped or failed redraws the stage's cards (its partial figures dropped, the System tier back).
        var click = SourceProbe.Member(SourceProbe.Src("Dialogs", "SettingsWindow.Hardware.cs"), "private async Task Benchmark_ClickAsync(");
        var cleanup = click.IndexOf("finally", StringComparison.Ordinal);
        expect(cleanup > 0 && click.IndexOf("_benchmarkPartial = null;", cleanup, StringComparison.Ordinal) is > 0 and var cleared
               && click.IndexOf("ShowStageRoles();", cleared, StringComparison.Ordinal) is > 0 and var redrawn && redrawn < click.IndexOf("if (result is null)", StringComparison.Ordinal),
            "F16: Test & Auto-Tune Hardware's cleanup (stopped, failed or done) clears the partial figures, then redraws the stage's cards");
    }

    // ----- role-cards-panel -----

    private static HardwareBenchmarkResult DesktopWithNpu() => new()
    {
        Clip = "H.264 1920x1080 60 fps, 90 frames", MediaFoundationDecodeCodecs = [.. MediaFoundationAll],
        Adapters =
        [
            new() { Index = 0, Name = "AMD Radeon RX 7900 XTX", VendorId = 0x1002, DedicatedVideoMemoryMb = 24560, DecodeCodecs = [.. AllCodecs], EncodeCodecs = [.. AllCodecs], HardwareDecodeFps = 1170, SoftwareDecodeFps = 680, CompositorMs = 0.551, ReadbackMBps = 9870 },
            new() { Index = 1, Name = "Intel(R) Arc(TM) A310 Graphics", VendorId = 0x8086, DedicatedVideoMemoryMb = 3956, DecodeCodecs = [.. AllCodecs], EncodeCodecs = [.. AllCodecs], HardwareDecodeFps = 1067, SoftwareDecodeFps = 680, ReadbackMBps = 7020 },
        ],
        Npus = [new HardwareBenchmarkNpu { Name = "Hexagon NPU", Vendor = "Qualcomm" }],
        Recommended = new HardwareBenchmarkRoles { Compositor = 0 },
    };

    private static void Idle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child))
                yield return below;
        }
    }

    private static void RoleCardsPanelCase(Action<bool, string> expect)
    {
        var result = DesktopWithNpu();
        var devices = new RoleDevice[] { new(0, "AMD Radeon RX 7900 XTX"), new(1, "Intel(R) Arc(TM) A310 Graphics") };
        string Menu(string role) => string.Join(" / ", RoleCardsPanel.ChoicesFor(role, devices).Select(c => $"{c.Header}={c.Value}"));
        expect(Menu(HardwareRoles.Compositor) == "Auto (Recommended)=-1 / 0: AMD Radeon RX 7900 XTX=0 / 1: Intel(R) Arc(TM) A310 Graphics=1"
               && Menu(HardwareRoles.Background) == "Auto (Recommended)=-1 / Processor Only=-2 / 0: AMD Radeon RX 7900 XTX=0 / 1: Intel(R) Arc(TM) A310 Graphics=1"
               && Menu(HardwareRoles.Npu) == "Auto (Recommended)=auto / NPU=npu / Graphics Card=gpu / Processor=cpu",
            $"the menus: Compositor {Menu(HardwareRoles.Compositor)}; Background {Menu(HardwareRoles.Background)}; NPU {Menu(HardwareRoles.Npu)}");

        var panel = new RoleCardsPanel { Devices = devices, HasNpu = true, ItemsSource = FirstRunWindow.RoleCardsOf(result, measuring: false) };
        var host = new Window { Left = -20000, Top = -20000, Width = 520, Height = 420, Content = panel, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            host.Show();
            Idle();
            string[] ids = ["CompositorRole", "DecodeRole", "BackgroundRole", "ExportRole", "NpuRole"];
            expect(panel.Cards.Select(AutomationProperties.GetAutomationId).SequenceEqual(ids) && panel.Cards.All(c => c.Focusable && c.ActualHeight > 0),
                "five cards, focusable, with their automation ids: " + string.Join(", ", ids));
            var cards = ((IEnumerable<FirstRunRoleCard>)panel.ItemsSource!).ToList();
            var pills = Descendants(panel.Cards[0]).OfType<Border>().Select(AutomationProperties.GetName).Where(n => n.Length > 0).ToList();
            expect(cards[0] is { Tier: { Grade: TierGrade.S }, Measured: true } && pills.Contains("Compositor 0.55 ms a frame · S (92)")
                   && cards[4] is { Tier: null, Badge: "Detected", Measured: false, Value: "auto" },
                $"the Compositor card: measured, its S pill (\"{cards[0].TierTip}\"); the NPU: no pill, Detected");

            var decodeMenu = panel.MenuFor(cards[1], panel.Cards[1]);
            var items = decodeMenu.Items.OfType<MenuItem>().ToList();
            var npuItems = panel.MenuFor(cards[4], panel.Cards[4]).Items.OfType<MenuItem>().ToList();
            expect(items.Select(i => (string)i.Header).SequenceEqual(["Auto (Recommended)", "0: AMD Radeon RX 7900 XTX", "1: Intel(R) Arc(TM) A310 Graphics"])
                   && items[0].IsChecked && !items[2].IsChecked && npuItems.Count == 4 && npuItems[1].IsEnabled,
                "Decode's menu: Auto (Recommended) checked, then each card; the NPU's four, NPU offered (there is one)");

            var raised = new List<string>();
            panel.RoleChosen += (role, value) => raised.Add($"{role}={value}");
            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            expect(raised.SequenceEqual(["Decode=1"]), "choosing the A310 raises RoleChosen(Decode, 1): " + string.Join(", ", raised));
        }
        finally
        {
            host.Close();
        }

        // The first run keeps a pick, shows it, and Start Editing writes it (a window never shown: nothing starts).
        var keptTheme = AppSettings.Current.Theme;
        var window = new FirstRunWindow(live: false);
        try
        {
            window.ShowProfile(result);
            var first = ((IEnumerable<FirstRunRoleCard>)window.RoleCards.ItemsSource!).ToList();
            void Pick(int card, string header)
            {
                var item = window.RoleCards.MenuFor(first[card], window.RoleCards.Cards[card]).Items.OfType<MenuItem>().Single(i => (string)i.Header == header);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }

            Pick(1, "1: Intel(R) Arc(TM) A310 Graphics");
            Pick(2, "Processor Only");
            Pick(4, "Processor");
            var picked = ((IEnumerable<FirstRunRoleCard>)window.RoleCards.ItemsSource!).ToList();
            expect(window.Picks == new FirstRunRolePicks(Decode: 1, Background: -2, Npu: "cpu")
                   && picked[1] is { Card: "Intel(R) Arc(TM) A310 Graphics", Value: "1", TierTip: "Decode 1,067 fps · S (77)" }
                   && picked[2] is { Card: "Processor Only", Value: "-2", TierTip: "Processor 680 fps · B (46)" } && picked[4] is { Value: "cpu", Detail: "Auto-Captions on the processor" }
                   && picked[0].Card == "AMD Radeon RX 7900 XTX" && picked[3].Card == "Auto · AMD Radeon RX 7900 XTX",
                "picked on the cards: Decode on the A310 (its own pill), Background on the processor, Auto-Captions on the processor; the rest as recommended");

            var settings = new AppSettings { CompositorGpuAdapter = 0, DecodeGpuAdapter = -1, BackgroundGpu = -1, ExportGpuAdapter = -1 };
            FirstRunWindow.WritePicks(window.Picks, settings, [0, 1]);
            var unknown = new AppSettings();
            FirstRunWindow.WritePicks(new FirstRunRolePicks(Compositor: 5), unknown, [0, 1]);
            expect((settings.CompositorGpuAdapter, settings.DecodeGpuAdapter, settings.BackgroundGpu, settings.ExportGpuAdapter, settings.CaptionAccelerator) == (0, 1, -2, -1, "cpu")
                   && unknown.CompositorGpuAdapter == -1,
                "WritePicks writes only what was picked (Decode 1, Background Processor Only, Auto-Captions cpu), a card not listed as Auto");
        }
        finally
        {
            window.Close();
            AppSettings.Current.Theme = keptTheme;
        }

        // Settings' stage cards, from its copy of the settings and a result of these cards (pure).
        var gpus = new List<GpuAdapter> { new(0, "AMD Radeon RX 7900 XTX") { DedicatedVideoMemoryMb = 24560 }, new(1, "Intel(R) Arc(TM) A310 Graphics") { DedicatedVideoMemoryMb = 3956 } };
        var copy = new AppSettings { CompositorGpuAdapter = -1, DecodeGpuAdapter = 1, BackgroundGpu = -2, HardwareDecodeAdapter = -1, CaptionAccelerator = "npu" };
        var stage = SettingsWindow.StageRoleCardsOf(copy, gpus, "Hexagon NPU", result, runningCompositor: 0);
        var untested = SettingsWindow.StageRoleCardsOf(new AppSettings(), gpus, null, null, runningCompositor: -1);
        expect(stage.Select(c => c.Card).SequenceEqual(["Auto · AMD Radeon RX 7900 XTX", "Intel(R) Arc(TM) A310 Graphics", "Processor Only", "Auto · AMD Radeon RX 7900 XTX", "Hexagon NPU"])
               && stage[0].TierTip == "Compositor 0.55 ms a frame · S (92)" && stage[1].TierTip == "Decode 1,067 fps · S (77)" && stage[2].Value == "-2" && stage[4].Value == "npu"
               && untested.Take(4).All(c => c is { Badge: "Not tested", Tier: null }) && untested[0].Card == "Auto · AMD Radeon RX 7900 XTX",
            "Settings' stage cards: each role as its copy has it (Decode on the A310, Background on the processor, Auto-Captions on the NPU) with the result's pills; untested: Not tested, no pills");

        var settingsXaml = File.ReadAllText(Path.Combine(SourceProbe.Root, "src", "Dialogs", "SettingsWindow.xaml"));
        var stagePreview = SourceProbe.Src("Controls", "SettingsStagePreview.cs");
        expect(settingsXaml.IndexOf("<local:RoleCardsPanel x:Name=\"StageRoleCards\"", StringComparison.Ordinal) is > 0 and var at
               && at < settingsXaml.IndexOf("<local:SettingsStagePreview x:Name=\"StagePreview\" />", StringComparison.Ordinal)
               && Regex.Matches(settingsXaml, "<local:PlaybackSyncCard ").Count == 1 && settingsXaml.Contains("x:Name=\"StageSyncText\"", StringComparison.Ordinal)
               && !stagePreview.Contains("GpuAdapters.", StringComparison.Ordinal) && stagePreview.Contains("$\"Compositor {card.CompositorMs:0.00} ms\"", StringComparison.Ordinal),
            "Settings' stage: the role cards above the picture (which draws no role cards of its own, and lists no cards), a read-only sync line (one Sync Test card), the benchmark lines with the compositor's ms");

        ReviewCards(expect, result, stage);
    }

    // ----- 5.6A review (F15, F17, F18): the saved roles on a re-run, the menus checking the card shown, Processor Only measured -----

    private static void ReviewCards(Action<bool, string> expect, HardwareBenchmarkResult result, IReadOnlyList<FirstRunRoleCard> stage)
    {
        // F15: opened again (FirstRunComplete), the cards show the roles the settings hold, display only; a first start shows the recommendation.
        var savedSettings = new AppSettings { FirstRunComplete = true, CompositorGpuAdapter = -1, DecodeGpuAdapter = 1, BackgroundGpu = -1, ExportGpuAdapter = -1, CaptionAccelerator = "npu" };
        var saved = FirstRunWindow.SavedRoles(savedSettings);
        var reopened = FirstRunWindow.RoleCardsOf(result, result, false, FirstRunWindow.ShownPicks(new FirstRunRolePicks(), saved));
        var handPicked = FirstRunWindow.ShownPicks(new FirstRunRolePicks(Decode: -1), saved);
        expect(saved == new FirstRunRolePicks(-1, 1, -1, -1, "npu") && FirstRunWindow.SavedRoles(new AppSettings { DecodeGpuAdapter = 1 }) == new FirstRunRolePicks()
               && reopened[1] is { Card: "Intel(R) Arc(TM) A310 Graphics", Value: "1" } && reopened[0].Value == "-1" && reopened[3] is { Card: "Auto · AMD Radeon RX 7900 XTX", Value: "-1" }
               && reopened[4] is { Value: "npu" } && handPicked.Decode == -1,
            "F15: opened again, the cards show the roles saved (Decode on the A310, its menu checking it; Export Auto; Auto-Captions on the NPU), a hand pick over them; a first start: none");

        var kept = (AppSettings.Current.FirstRunComplete, AppSettings.Current.DecodeGpuAdapter, AppSettings.Current.Theme);
        AppSettings.Current.FirstRunComplete = true;
        AppSettings.Current.DecodeGpuAdapter = 1;
        var window = new FirstRunWindow(live: false);
        try
        {
            window.ShowProfile(result);
            var shown = ((IEnumerable<FirstRunRoleCard>)window.RoleCards.ItemsSource!).ToList();
            var written = new AppSettings { DecodeGpuAdapter = 1 };
            FirstRunWindow.WritePicks(window.Picks, written, [0, 1]);
            var finish = SourceProbe.Member(SourceProbe.Src("Dialogs", "FirstRunWindow.xaml.cs"), "private void Finish()");
            expect(shown[1] is { Card: "Intel(R) Arc(TM) A310 Graphics", Value: "1" } && window.Picks == new FirstRunRolePicks() && written.DecodeGpuAdapter == 1
                   && SourceProbe.Member(SourceProbe.Src("Dialogs", "FirstRunWindow.Profile.cs"), "private void ShowCards()").Contains("RoleCardsOf(roles, figures, _measuring, ShownPicks(_picks, _saved));", StringComparison.Ordinal)
                   && finish.Contains("WritePicks(_picks, settings,", StringComparison.Ordinal) && !finish.Contains("_saved", StringComparison.Ordinal),
                "F15: the window opened from Settings with Decode on the A310 shows it so; nothing picked, Start Editing writes nothing (the saved roles are display only)");
        }
        finally
        {
            window.Close();
            (AppSettings.Current.FirstRunComplete, AppSettings.Current.DecodeGpuAdapter, AppSettings.Current.Theme) = kept;
        }

        // F17: Export recommended on another card (it encodes more): its menu checks that card, not Auto; the Compositor's checks its card.
        var exportElsewhere = DesktopWithNpu();
        exportElsewhere.Recommended.Export = 1;
        var recommended = FirstRunWindow.RoleCardsOf(exportElsewhere, measuring: false);
        var gone = FirstRunWindow.RoleCardsOf(result, result, false, new FirstRunRolePicks(Decode: 7));
        expect(recommended[3] is { Card: "Intel(R) Arc(TM) A310 Graphics", Value: "1" } && recommended[0] is { Card: "AMD Radeon RX 7900 XTX", Value: "0" }
               && FirstRunWindow.RoleCardsOf(exportElsewhere, exportElsewhere, false, new FirstRunRolePicks(Export: -1))[3] is { Card: "Auto · AMD Radeon RX 7900 XTX", Value: "-1" }
               && gone[1] is { Card: "Auto · AMD Radeon RX 7900 XTX", Value: "-1" },
            "F17: nothing picked, the Export and Compositor menus check the card shown (the A310 that encodes more; the RX 7900 XTX); Auto picked: Auto; a role on a card not listed: Auto");

        // F18: Processor Only after a full result: the processor's tier pill and the green check, in the first run as in Settings.
        var processor = FirstRunWindow.RoleCardsOf(result, result, false, new FirstRunRolePicks(Background: HardwareRoles.ProcessorOnly));
        expect(processor[2] is { Card: "Processor Only", Measured: true, Tier: not null } && stage[2] is { Card: "Processor Only", Measured: true, Tier: not null },
            "F18: Background on Processor Only shows the processor's pill and the green check in both homes (the first run, Settings)");
    }
}
