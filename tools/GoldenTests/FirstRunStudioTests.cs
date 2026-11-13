using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.5A lane B: the two-phase hardware profile (an instant estimate, then a quick background measurement) and the two-screen 940 x 600 first-run studio welcome.
/// hardware-roles-primary (pure): HardwareBenchmark.Recommend on the Desktop (RX 7900 XTX + Arc A310), the Plex Server (GTX 1070), the XPS 13
/// (Adreno, unified memory, an NPU), a card Windows lists twice, a laptop's two cards and a second card that encodes more: Decode and
/// Background always Auto, the estimate's roles the same as the measurement's with its "measured next" reason; the three-argument
/// WriteRoles; the result's Kind append-only in its JSON. first-run-quick-targets (pure and source): the cards the quick check measures (never
/// a card with no role), its command line, ParseAdapters, a quick result never replacing a current full one, the Background job and the
/// shared gate. first-run-profile-off-ui-thread (source, and the real estimate worker run as HandPeg runs it): nothing in the window lists
/// the cards in its own process, the estimate from Task.Run, the workers killed by Start Editing and by closing, the estimate makes no
/// Direct3D 11 device and asks for no fingerprint. first-run-window-fit (pure): the window at 100, 125, 150 and 200 % and a 150 % UI Scale
/// inside the work area, centred. first-run-encoder-estimate (on the WPF Application, windows never shown): the encoders estimated from each
/// card's Media Foundation encoders, the Export card's family first, FFmpeg's answer always winning whichever comes first. first-run-studio-layout
/// (a window shown off-screen, its component panel left out so nothing reaches the network): 940 x 600, the two screens, the five swatches,
/// the live miniature following the pointer, the role cards' badges from Estimated to their figures. The 5.5A addenda (first-run-studio-layout):
/// no way to use an FFmpeg already on the PC in the first run (Settings keeps its own), and Prompt to save's tooltip saying Keep open is off at first.
/// </summary>
public static class FirstRunStudioTests
{
    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("hardware-roles-primary", "# The GPU roles (5.5A): Decode and Background always Auto (the compositor's card), the estimate's roles the measurement's, WriteRoles against the profile's own cards.", RolesPrimary);
        yield return Case("first-run-quick-targets", "# The quick check (5.5A): the compositor's card and a different Export card only, its command line, never over a current full result, in the background job behind the one gate.", QuickTargets);
        yield return Case("first-run-profile-off-ui-thread", "# The hardware profile never in the UI process (5.5A): no card listed here, the estimate from a pool thread in a worker with no Direct3D device, both workers killed before the main window.", OffUiThread);
        yield return Case("first-run-window-fit", "# The first-run window (5.5A): 940 x 600 HandPeg DIPs as UiScale sizes it, then kept inside the work area and centred in it.", WindowFit);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("first-run-encoder-estimate", "# The default encoder (5.5A): estimated from each card's Media Foundation encoders before FFmpeg, the Export card's family first, FFmpeg's answer always winning.", EncoderEstimate);
        yield return Case("first-run-studio-layout", "# The studio welcome (5.5A): two screens in 940 x 600, the live miniature under the pointer, five swatches, the role cards from Estimated to their figures.", StudioLayout);
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

    // ----- The machines -----

    private static readonly string[] AllCodecs = ["H.264", "HEVC", "AV1"];

    private static HardwareBenchmarkAdapter Card(int index, string name, uint vendor, uint device, long videoMb, string[] encodes,
        double fps = 0, double readback = 0, bool unified = false, int sameAs = -1) => new()
    {
        Index = index, Name = name, VendorId = vendor, DeviceId = device, DedicatedVideoMemoryMb = videoMb, Vendor = NpuAdapters.VendorName(vendor),
        EncodeCodecs = [.. encodes], DecodeCodecs = ["H.264", "HEVC"], HardwareDecodeFps = fps, ReadbackMBps = readback, UploadMBps = readback / 2,
        UnifiedMemory = unified, SameCardAs = sameAs, Note = sameAs >= 0 ? $"The same card as #{sameAs}, listed twice by Windows." : "",
    };

    private static HardwareBenchmarkResult Machine(string kind, params HardwareBenchmarkAdapter[] cards)
    {
        var result = new HardwareBenchmarkResult { Kind = kind, Adapters = [.. cards], Clip = kind == HardwareBenchmarkResult.KindEstimate ? "" : "H.264 1920x1080 60 fps, 90 frames", TotalSeconds = 1.4 };
        result.Recommended = HardwareBenchmark.Recommend(result);
        return result;
    }

    // The Desktop: an RX 7900 XTX that composites, an Arc A310 beside it that decodes faster (across the bus faster too).
    private static HardwareBenchmarkResult Desktop(string kind)
    {
        var measured = kind != HardwareBenchmarkResult.KindEstimate;
        var result = Machine(kind,
            Card(0, "AMD Radeon RX 7900 XTX", 0x1002, 0x744C, 24560, AllCodecs, measured ? 870.4 : 0, measured ? 9870 : 0),
            Card(1, "Intel(R) Arc(TM) A310 Graphics", 0x8086, 0x56A6, 3956, AllCodecs, kind == "" ? 1412.6 : 0, kind == "" ? 7020 : 0));
        if (kind == "")
        {
            result.CrossAdapter = new HardwareBenchmarkCrossAdapter { DecodeAdapter = 1, CompositeAdapter = 0, CrossDecodeFps = 1180, LocalDecodeFps = 870.4, CrossIsFaster = true };
            result.Recommended = HardwareBenchmark.Recommend(result);
        }

        return result;
    }

    private static HardwareBenchmarkResult PlexServer(string kind) =>
        Machine(kind, Card(0, "NVIDIA GeForce GTX 1070", 0x10DE, 0x1B81, 8192, ["H.264", "HEVC"], kind == "" ? 610 : 0, kind == "" ? 6100 : 0));

    private static HardwareBenchmarkResult Xps13(string kind)
    {
        var result = Machine(kind, Card(0, "Qualcomm(R) Adreno(TM) X1-85 GPU", 0x4D4F4351, 0x36334330, 128, AllCodecs, kind == "" ? 652 : 0, kind == "" ? 14308 : 0, unified: true));
        result.Npus = [new HardwareBenchmarkNpu { Name = "Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Hexagon(TM) NPU", Vendor = "Qualcomm", UsedFor = NpuAdapters.UsedFor }];
        return result;
    }

    // One RX 7900 XTX listed twice (a headset's display on it) and the processor's own Intel graphics.
    private static HardwareBenchmarkResult Twin(string kind) => Machine(kind,
        Card(0, "AMD Radeon RX 7900 XTX", 0x1002, 0x744C, 24560, AllCodecs, kind == "" ? 870 : 0),
        Card(1, "AMD Radeon RX 7900 XTX", 0x1002, 0x744C, 24560, AllCodecs, sameAs: 0),
        Card(2, "Intel(R) UHD Graphics 770", 0x8086, 0x4680, 128, AllCodecs, kind == "" ? 990 : 0));

    // A laptop: the processor's Intel graphics listed first, an RTX 4060 the high-performance card.
    private static HardwareBenchmarkResult Laptop(string kind) => Machine(kind,
        Card(0, "Intel(R) UHD Graphics", 0x8086, 0x9A49, 128, AllCodecs, kind == "" ? 700 : 0),
        Card(1, "NVIDIA GeForce RTX 4060 Laptop GPU", 0x10DE, 0x28E0, 7948, AllCodecs, kind == "" ? 1100 : 0));

    // A second card that encodes more (AV1) than the compositor's: it takes Export, and only Export.
    private static HardwareBenchmarkResult ExportElsewhere(string kind) => Machine(kind,
        Card(0, "NVIDIA GeForce GTX 1070", 0x10DE, 0x1B81, 8192, ["H.264", "HEVC"], kind == "" ? 610 : 0),
        Card(1, "Intel(R) Arc(TM) A380 Graphics", 0x8086, 0x56A5, 6144, AllCodecs, kind == "" ? 1300 : 0));

    private static readonly (string Name, Func<string, HardwareBenchmarkResult> Make, int Compositor, int Export)[] Machines =
    [
        ("the Desktop (RX 7900 XTX + Arc A310, the A310 faster across the bus)", Desktop, 0, -1),
        ("the Plex Server (GTX 1070 alone)", PlexServer, 0, -1),
        ("the XPS 13 (Adreno X1-85, unified memory, a Hexagon NPU)", Xps13, 0, -1),
        ("an RX 7900 XTX listed twice beside Intel UHD 770", Twin, 0, -1),
        ("a laptop (Intel UHD listed first, RTX 4060)", Laptop, 1, -1),
        ("a GTX 1070 beside an Arc A380 that also encodes AV1", ExportElsewhere, 0, 1),
    ];

    private static string Role(int index) => index switch { -1 => "Auto", -2 => "Processor", _ => index.ToString(CultureInfo.InvariantCulture) };

    // ----- hardware-roles-primary -----

    private static void RolesPrimary(Action<bool, string> expect)
    {
        foreach (var (name, make, compositor, export) in Machines)
        {
            var measured = make("").Recommended;
            var estimate = make(HardwareBenchmarkResult.KindEstimate).Recommended;
            expect(measured.Compositor == compositor && measured.Decode == -1 && measured.Background == -1 && measured.Export == export,
                $"{name}: Compositor {Role(measured.Compositor)}, Decode {Role(measured.Decode)}, Background {Role(measured.Background)}, Export {Role(measured.Export)}");
            expect((estimate.Compositor, estimate.Decode, estimate.Background, estimate.Export) == (measured.Compositor, measured.Decode, measured.Background, measured.Export)
                   && estimate.Reasons.Contains("Decode: Auto, the compositor's card (measured next)."),
                $"{name}: the estimate (nothing measured) gives the same four roles, its decode reason \"measured next\"");
        }

        var desktop = Desktop("").Recommended;
        expect(desktop.Reasons.Any(r => r.StartsWith("Decode: Auto, the compositor's card. Card 1 decoding across the bus measured faster", StringComparison.Ordinal))
               && desktop.Reasons.Any(r => r.StartsWith("Background: Auto, the compositor's card. Intel(R) Arc(TM) A310 Graphics also decodes", StringComparison.Ordinal)),
            "the Desktop's A310 measured faster, and still never chosen for Decode or Background (CLAUDE.md 8: the 0x116 TDR)");
        var twin = Twin("").Recommended;
        expect(new[] { twin.Compositor, twin.Decode, twin.Background, twin.Export }.All(r => r != 1),
            "a card Windows lists twice is given no role by its second listing");

        var quickBusOnly = Machine(HardwareBenchmarkResult.KindQuick, Card(0, "NVIDIA GeForce GTX 1070", 0x10DE, 0x1B81, 8192, ["H.264", "HEVC"], 0, 6100));
        quickBusOnly.Clip = "";
        quickBusOnly.Recommended = HardwareBenchmark.Recommend(quickBusOnly);
        expect(quickBusOnly.Recommended.Reasons.Contains("Decode: Auto, the compositor's card (its decoding is measured once FFmpeg is here).")
               && quickBusOnly.Recommended.Compositor == 0,
            "a quick check with no test clip (no FFmpeg yet) measured the bus only, and says the decoding comes once FFmpeg is here");

        // ----- WriteRoles against the profile's own cards -----
        var settings = new AppSettings { CompositorGpuAdapter = 1, DecodeGpuAdapter = 1, BackgroundGpu = 1, HardwareDecodeAdapter = 1 };
        HardwareBenchmarkService.WriteRoles(desktop, settings, [0, 1]);
        expect((settings.CompositorGpuAdapter, settings.DecodeGpuAdapter, settings.BackgroundGpu, settings.ExportGpuAdapter, settings.HardwareDecodeAdapter) == (0, -1, -1, -1, -1),
            "WriteRoles(roles, settings, cards here): the Desktop's roles written as Compositor 0 and Decode, Background and Export Auto, over a second card chosen before");
        HardwareBenchmarkService.WriteRoles(new HardwareBenchmarkRoles { Compositor = 3, Decode = -1, Background = -2, Export = 1 }, settings, [0, 1]);
        expect((settings.CompositorGpuAdapter, settings.BackgroundGpu, settings.ExportGpuAdapter) == (-1, -2, 1),
            "a card that is not here (3) is written as Auto, Background's Processor Only (-2) is kept, a card here (Export 1) is written");

        // 5.5A review (B4): Start Editing writes Decode and Background Auto whatever the profile shown holds (an older saved result).
        var older = new HardwareBenchmarkRoles { Compositor = 0, Decode = 1, Background = 1, Export = 1, Reasons = ["Compositor: kept."] };
        var firstRun = HardwareBenchmarkService.DecodeAndBackgroundAuto(older);
        HardwareBenchmarkService.WriteRoles(firstRun, settings, [0, 1]);
        expect((firstRun.Compositor, firstRun.Decode, firstRun.Background, firstRun.Export) == (0, -1, -1, 1) && firstRun.Reasons.SequenceEqual(older.Reasons)
               && (settings.CompositorGpuAdapter, settings.DecodeGpuAdapter, settings.BackgroundGpu, settings.ExportGpuAdapter) == (0, -1, -1, 1),
            "a saved result from before 5.4.4 naming the A310 (1) for Decode and Background: the first-run roles write them Auto, the Compositor (0) and Export (1) as shown");

        // ----- 5.5A review (B1): a quick check never moves a role onto a card it did not measure -----
        var listing = Desktop(HardwareBenchmarkResult.KindEstimate);
        listing.Kind = HardwareBenchmarkResult.KindQuick;
        listing.Clip = "";
        var listed = HardwareBenchmark.Recommend(listing);
        listing.Adapters[0].Note = "The Direct3D 11 device could not be made (0x887A0020).";
        var shifted = HardwareBenchmark.Recommend(listing);
        var kept = HardwareBenchmark.QuickRoles(listing, listed, [0]);
        expect(listed.Compositor == 0 && shifted.Compositor == 1
               && (kept.Compositor, kept.Decode, kept.Background, kept.Export) == (-1, -1, -1, -1)
               && kept.Reasons[0].StartsWith("Compositor: Auto. AMD Radeon RX 7900 XTX could not be measured (The Direct3D 11 device could not be made (0x887A0020))", StringComparison.Ordinal),
            "the Desktop, its RX 7900 XTX's device failing in the quick check: Recommend alone would give the never-measured A310 the Compositor; QuickRoles leaves it Auto (Decode, Background, Export Auto)");
        var elsewhere = ExportElsewhere(HardwareBenchmarkResult.KindEstimate);
        elsewhere.Kind = HardwareBenchmarkResult.KindQuick;
        var elsewhereListed = HardwareBenchmark.Recommend(elsewhere);
        elsewhere.Adapters[1].Note = "Device removed.";
        var exportFailed = HardwareBenchmark.QuickRoles(elsewhere, elsewhereListed, [1]);
        elsewhere.Adapters[1].Note = "";
        elsewhere.Adapters[0].Note = "Device removed.";
        var compositorFailed = HardwareBenchmark.QuickRoles(elsewhere, elsewhereListed, [0]);
        expect((exportFailed.Compositor, exportFailed.Export) == (0, -1) && exportFailed.Reasons.Last().StartsWith("Export: Auto, the compositor's card. Intel(R) Arc(TM) A380 Graphics could not be measured", StringComparison.Ordinal)
               && (compositorFailed.Compositor, compositorFailed.Export) == (-1, 1) && compositorFailed.Reasons.Last() == elsewhereListed.Reasons.Last(),
            "a GTX 1070 and an Export A380: the A380 failing leaves Export Auto and the Compositor the 1070; the 1070 failing leaves the Compositor Auto and Export, measured, the A380");
        elsewhere.Adapters[0].Note = "";
        var none = HardwareBenchmark.QuickRoles(elsewhere, elsewhereListed, []);
        expect((none.Compositor, none.Decode, none.Background, none.Export) == (elsewhereListed.Compositor, -1, -1, elsewhereListed.Export),
            "every card measured: the roles Recommend gives (the listing's own)");

        // ----- 5.5A review (B3): what a quick check measured, from its numbers -----
        var decodedAndBus = Desktop(HardwareBenchmarkResult.KindQuick);
        var clipFailed = Desktop(HardwareBenchmarkResult.KindQuick);
        clipFailed.Adapters[0].HardwareDecodeFps = 0;
        var nothing = Desktop(HardwareBenchmarkResult.KindQuick);
        (nothing.Adapters[0].HardwareDecodeFps, nothing.Adapters[0].ReadbackMBps) = (0, 0);
        expect(HardwareBenchmarkService.QuickMeasured(decodedAndBus) == "decoding and the bus" && HardwareBenchmarkService.QuickDecoded(decodedAndBus)
               && HardwareBenchmarkService.QuickMeasured(clipFailed) == "the bus" && !HardwareBenchmarkService.QuickDecoded(clipFailed) && clipFailed.Clip.Length > 0
               && HardwareBenchmarkService.QuickMeasured(nothing) == "",
            "QuickMeasured: decoding and the bus; the bus alone when the test clip (named in the result) did not decode, which is not decoded for the re-run; nothing when no card gave a number");

        // ----- The result's Kind, append-only in its JSON -----
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var old = JsonSerializer.Deserialize<HardwareBenchmarkResult>("""{"measuredAt":"2026-01-02T03:04:05Z","fingerprint":"1002:744C:1","adapters":[]}""", json)!;
        var written = JsonSerializer.Serialize(new HardwareBenchmarkResult { Kind = HardwareBenchmarkResult.KindQuick }, json);
        expect(old.Kind == "" && written.Contains("\"kind\":\"quick\"", StringComparison.Ordinal) && HardwareBenchmarkResult.KindEstimate == "estimate",
            "a result saved before 5.5A reads as the full test (Kind \"\"); a quick one is written with \"kind\":\"quick\"");
    }

    // ----- first-run-quick-targets -----

    private static void QuickTargets(Action<bool, string> expect)
    {
        foreach (var (name, make, compositor, export) in Machines)
        {
            var targets = HardwareBenchmarkService.QuickTargets(make(HardwareBenchmarkResult.KindEstimate));
            var wanted = export >= 0 && export != compositor ? new[] { compositor, export } : [compositor];
            expect(targets.SequenceEqual(wanted), $"{name}: the quick check measures card(s) {string.Join(", ", targets)}");
        }

        var none = new HardwareBenchmarkResult { Kind = HardwareBenchmarkResult.KindEstimate, Adapters = [Card(0, "AMD Radeon RX 7900 XTX", 0x1002, 0x744C, 24560, AllCodecs, sameAs: 5)] };
        var unnamed = new HardwareBenchmarkResult { Kind = HardwareBenchmarkResult.KindEstimate, Adapters = [Card(0, "Intel(R) UHD Graphics", 0x8086, 0x9A49, 128, AllCodecs), Card(1, "NVIDIA GeForce RTX 4060 Laptop GPU", 0x10DE, 0x28E0, 7948, AllCodecs)] };
        expect(HardwareBenchmarkService.QuickTargets(none).Count == 0 && HardwareBenchmarkService.QuickTargets(unnamed).SequenceEqual([1]),
            "no first listing: nothing measured; roles naming no card: the first listing with the most video memory, and it alone");

        expect(HardwareBenchmarkService.QuickArguments("", [0]) == "--hw-benchmark --quick --adapters 0",
            "with no test clip (no FFmpeg yet): --hw-benchmark --quick --adapters 0 (the bus only)");
        expect(HardwareBenchmarkService.QuickArguments(@"C:\cache\benchmark\bench-1080p60-h264.mp4", [0, 2]) == "--hw-benchmark \"C:\\cache\\benchmark\\bench-1080p60-h264.mp4\" --quick --adapters 0,2",
            "with one: --hw-benchmark \"<clip>\" --quick --adapters 0,2");
        expect(HardwareBenchmark.ParseAdapters("0,2").SetEquals([0, 2]) && HardwareBenchmark.ParseAdapters("").Count == 0 && HardwareBenchmark.ParseAdapters("1, x,3").SetEquals([1, 3]),
            "ParseAdapters: \"0,2\" is cards 0 and 2, nothing is none, a word that is not a number is left out");

        var full = new HardwareBenchmarkResult { Fingerprint = "1002:744C:31.0" };
        var quick = new HardwareBenchmarkResult { Kind = HardwareBenchmarkResult.KindQuick, Fingerprint = "1002:744C:31.0" };
        expect(!HardwareBenchmarkService.QuickMayReplace(full, quick) && !HardwareBenchmarkService.QuickMayReplace(new HardwareBenchmarkResult(), quick),
            "a quick check never replaces a full result of these same cards and drivers (nor one from before fingerprints)");
        expect(HardwareBenchmarkService.QuickMayReplace(null, quick) && HardwareBenchmarkService.QuickMayReplace(new HardwareBenchmarkResult { Fingerprint = "10DE:1B81:27.21" }, quick)
               && HardwareBenchmarkService.QuickMayReplace(new HardwareBenchmarkResult { Kind = HardwareBenchmarkResult.KindQuick, Fingerprint = quick.Fingerprint }, quick),
            "it is kept when there is none, when the full one was measured on other cards or drivers, and over an earlier quick one");

        var service = Read("Services", "HardwareBenchmarkService.cs");
        var runQuick = Method(service, "public static async Task<HardwareBenchmarkResult?> RunQuickAsync(");
        var measureQuick = Method(service, "private static async Task<HardwareBenchmarkResult?> MeasureQuickOffThreadAsync(");
        expect(runQuick.Contains("Interlocked.Exchange(ref _running, 1)", StringComparison.Ordinal) && runQuick.Contains("Volatile.Write(ref _running, 0)", StringComparison.Ordinal)
               && Method(service, "public static async Task<HardwareBenchmarkResult?> RunAsync(").Contains("Interlocked.Exchange(ref _running, 1)", StringComparison.Ordinal),
            "the quick check takes Test & Auto-Tune Hardware's own gate: one benchmark at a time");
        expect(runQuick.Contains("WorkLane.Background", StringComparison.Ordinal) && measureQuick.Contains("ProcessClass.Background", StringComparison.Ordinal)
               && Method(Read("Services", "WorkerProcess.cs"), "public static async Task<int> RunLinesAsync(").Contains("Start(arguments, readOutput: true, jobClass)", StringComparison.Ordinal),
            "in the Background lane and the background job (WorkerProcess.RunLinesAsync passes the job class on)");
        expect(service.Contains("Task.Run(() => MeasureQuickOffThreadAsync(work, cards, progress), CancellationToken.None)", StringComparison.Ordinal),
            "its worker started from a pool thread: the window's thread never waits on its CreateProcess, its clip or its saving");
        expect(Method(service, "private static void SaveQuick(").Contains("QuickMayReplace(LoadSaved(), quick)", StringComparison.Ordinal),
            "saved only where QuickMayReplace allows");

        var worker = Read("Compositor", "HardwareBenchmark.Profile.cs");
        var quickBody = Method(worker, "private static int MeasureQuick(");
        expect(quickBody.Contains("quick: true", StringComparison.Ordinal) && quickBody.Contains("only.Contains(a.Index)", StringComparison.Ordinal)
               && !quickBody.Contains("CrossAdapter", StringComparison.Ordinal) && !quickBody.Contains("Compare(", StringComparison.Ordinal) && !quickBody.Contains("hardware: false", StringComparison.Ordinal),
            "the worker's quick mode decodes on the cards named only (frames per second, no seeks or accuracy frames), with no processor decode and no two-card test");
        var npus = quickBody.IndexOf("result.Npus = ListNpus();", StringComparison.Ordinal);
        expect(npus >= 0 && npus < quickBody.IndexOf("Report(\"adapters\"", StringComparison.Ordinal)
               && quickBody.IndexOf("ListNpus()", StringComparison.Ordinal) == quickBody.LastIndexOf("ListNpus()", StringComparison.Ordinal),
            "5.5A review (B2): the quick check lists the NPUs before its first line (the window shows that line over the estimate's NPU), once");
        var listedAt = quickBody.IndexOf("var listed = Recommend(result);", StringComparison.Ordinal);
        expect(listedAt >= 0 && quickBody.IndexOf("Recommend(result)", StringComparison.Ordinal) == quickBody.LastIndexOf("Recommend(result)", StringComparison.Ordinal)
               && Regex.Matches(quickBody, @"QuickRoles\(result, listed, failed\)").Count == 3
               && quickBody.Contains("failed.Add(adapter.Index);", StringComparison.Ordinal),
            "5.5A review (B1): the roles listed before measuring, then QuickRoles over the cards that failed after each card measured, each failure and at the end: never Recommend over cards it did not measure");

        var profile = Read("Dialogs", "FirstRunWindow.Profile.cs");
        var loaded = Method(profile, "private async void Profile_Loaded(");
        expect(loaded.Contains("if (Owner is null)", StringComparison.Ordinal) && loaded.Contains("MeasureQuickly();", StringComparison.Ordinal)
               && !Method(profile, "private void StartProfile()").Contains("RunQuickAsync", StringComparison.Ordinal),
            "the window runs the quick check only on the first start (no owner), never opened from Settings");
    }

    // ----- first-run-profile-off-ui-thread -----

    private static void OffUiThread(Action<bool, string> expect)
    {
        var window = string.Join("\n", new[] { "FirstRunWindow.xaml.cs", "FirstRunWindow.Screens.cs", "FirstRunWindow.Profile.cs" }.Select(f => Read("Dialogs", f)));
        string[] forbidden = ["GpuAdapters.", "NpuAdapters.Enumerate", "DxCore", "HardwareBenchmark.EnumerateAdapters", "MediaFoundation.", "EncoderOption.FromHardwareName)"];
        expect(!forbidden.Any(f => window.Contains(f, StringComparison.Ordinal)) && !Regex.IsMatch(window, @"FromHardwareName\(\s*\w+\s*\)"),
            "the first-run window lists no card, NPU or encoder in its own process (no GpuAdapters, NpuAdapters.Enumerate, DxCore, Media Foundation, one-argument FromHardwareName)");

        var profile = Read("Dialogs", "FirstRunWindow.Profile.cs");
        expect(Method(profile, "private void StartProfile()").Contains("Task.Run(() => HardwareBenchmarkService.EstimateAsync(progress, token)", StringComparison.Ordinal),
            "the estimate is started from a pool thread (Task.Run): CreateProcess never waits on the window's thread");

        var code = Read("Dialogs", "FirstRunWindow.xaml.cs");
        var finish = Method(code, "private void Finish()");
        var cancel = finish.IndexOf("_profileCancellation.Cancel();", StringComparison.Ordinal);
        expect(cancel >= 0 && cancel < finish.IndexOf("DialogResult = true;", StringComparison.Ordinal) && cancel < finish.IndexOf("settings.SaveAsCurrent();", StringComparison.Ordinal)
               && Regex.Matches(code, @"_profileCancellation\.Cancel\(\);").Count >= 2,
            "Start Editing kills the profile's workers before the settings are saved and the main window is made, and closing the window kills them too");
        expect(finish.Contains("HardwareBenchmarkService.WriteRoles(HardwareBenchmarkService.DecodeAndBackgroundAuto(roles), settings, [.. profile.Adapters.Select(a => a.Index)]);", StringComparison.Ordinal)
               && finish.Contains("if (firstStart && _benchmarkRoles is { } roles", StringComparison.Ordinal),
            "the roles are written on a first start only, against the profile's own cards (the three-argument WriteRoles), Decode and Background Auto (5.5A review)");
        expect(!code.Contains("MaxHeight = SystemParameters", StringComparison.Ordinal)
               && Read("Dialogs", "FirstRunWindow.Screens.cs").Contains("Loaded += (_, _) => FitToWorkArea(center: true);", StringComparison.Ordinal),
            "no work-area height set in the constructor (UiScale would scale it again): FitToWorkArea runs as an instance Loaded handler, after UiScale's");
        var app = Read("App.xaml.cs");
        expect(!app.Contains("HardwareBenchmark", StringComparison.Ordinal) && !app.Contains("EstimateAsync", StringComparison.Ordinal),
            "App's start-up path has no profiling of its own (only the first-run window, shown before the main window exists)");

        var worker = Read("Compositor", "HardwareBenchmark.Profile.cs");
        var estimate = Method(worker, "private static int Estimate()");
        expect(estimate.Contains("EnumerateAdapters(decoderProfiles: false)", StringComparison.Ordinal) && !estimate.Contains("GpuDevice.Create", StringComparison.Ordinal)
               && !estimate.Contains("Fingerprint", StringComparison.Ordinal) && !estimate.Contains("DecodeRun", StringComparison.Ordinal) && !estimate.Contains("_ffmpeg", StringComparison.Ordinal),
            "the estimate makes no Direct3D 11 device, decodes nothing, asks for no fingerprint and runs no FFmpeg");

        // The real worker, as the window starts it (this program hosts the --hw-benchmark entry point as HandPeg.exe does).
        var clock = Stopwatch.StartNew();
        var result = Task.Run(() => HardwareBenchmarkService.EstimateAsync(null, CancellationToken.None)).GetAwaiter().GetResult();
        var wall = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine(result is null
            ? "        the estimate worker gave no result"
            : string.Create(CultureInfo.InvariantCulture, $"        this PC's estimate: {result.Adapters.Count} card(s) ({string.Join(", ", result.Adapters.Select(a => a.Name))}), {result.Npus.Count} NPU(s), {result.TotalSeconds * 1000:0} ms in the worker, {wall:0} ms with its start; encoders {string.Join(", ", HandPegApp.FirstRunWindow.EstimatedEncoders(result))}"));
        expect(result is { Kind: HardwareBenchmarkResult.KindEstimate, Error.Length: 0, Fingerprint.Length: 0 } && result.TotalSeconds > 0
               && result.Recommended is { Decode: -1, Background: -1 } && result.Adapters.All(a => a.DecodeCodecs.Count == 0 && a.HardwareDecodeFps == 0),
            "the real estimate worker answers with Kind estimate, Decode and Background Auto, no card's decoders listed (no device made), nothing measured, no fingerprint");
    }

    // ----- first-run-window-fit -----

    private static void WindowFit(Action<bool, string> expect)
    {
        var settings = AppSettings.Current;
        var kept = (settings.UiScalePercent, settings.FollowWindowsDpiScaling);
        try
        {
            settings.FollowWindowsDpiScaling = false;
            (string Screen, double Dpi, int Pixels, Rect WorkPixels, int UiScale)[] screens =
            [
                ("a 1920 x 1080 monitor at 100 %", 1, 1920, new Rect(0, 0, 1920, 1040), 100),
                ("a 1366 x 768 laptop at 125 %", 1.25, 1366, new Rect(0, 0, 1366, 728), 100),
                ("a 1920 x 1080 monitor at 150 %", 1.5, 1920, new Rect(0, 0, 1920, 1032), 100),
                ("the XPS 13's 2880 x 1800 at 200 %", 2, 2880, new Rect(0, 0, 2880, 1752), 100),
                ("a 1366 x 768 laptop at 100 %, UI Scale 150 %", 1, 1366, new Rect(0, 0, 1366, 728), 150),
                ("a second monitor at the left, 2560 x 1440 at 100 %", 1, 2560, new Rect(-2560, 0, 2560, 1400), 100),
            ];
            foreach (var (screen, dpi, pixels, workPixels, uiScale) in screens)
            {
                settings.UiScalePercent = uiScale;
                var scale = UiScale.For(dpi, pixels);
                var work = new Rect(workPixels.X / dpi, workPixels.Y / dpi, workPixels.Width / dpi, workPixels.Height / dpi);
                var placed = FirstRunWindow.FitTo(work, FirstRunWindow.DesignSizeAt(scale));
                var inside = placed.Left >= work.Left - 1e-6 && placed.Top >= work.Top - 1e-6 && placed.Right <= work.Right + 1e-6 && placed.Bottom <= work.Bottom + 1e-6;
                var centred = Math.Abs(placed.Left + placed.Width / 2 - (work.Left + work.Width / 2)) <= 1 && Math.Abs(placed.Top + placed.Height / 2 - (work.Top + work.Height / 2)) <= 1;
                var whole = Math.Abs(placed.Width - Math.Min(940 * scale, work.Width)) < 1e-6 && Math.Abs(placed.Height - Math.Min(600 * scale, work.Height)) < 1e-6;
                expect(inside && centred && whole, string.Create(CultureInfo.InvariantCulture,
                    $"{screen}: scale {scale:0.###}, the window {placed.Width:0.#} x {placed.Height:0.#} DIPs ({placed.Width * dpi:0} x {placed.Height * dpi:0} px) at {placed.Left:0.#}, {placed.Top:0.#}, inside its work area of {work.Width:0.#} x {work.Height:0.#}, centred"));
            }

            // 5.5A review (B5): fitted to a small work area, then moved to a roomy monitor: the design size again, never the shrunk one.
            settings.UiScalePercent = 100;
            var small = FirstRunWindow.FitTo(new Rect(0, 0, 910, 485), FirstRunWindow.DesignSizeAt(1));
            var roomy = FirstRunWindow.FitTo(new Rect(0, 0, 1920, 1040), FirstRunWindow.DesignSizeAt(1));
            var unknown = FirstRunWindow.DesignSizeAt(double.NaN);
            expect((small.Width, small.Height) == (910, 485) && (roomy.Width, roomy.Height) == (940, 600) && (unknown.Width, unknown.Height) == (940, 600)
                   && FirstRunWindow.DesignSizeAt(1.5) == new Size(1410, 900),
                "a 1366 x 768 laptop at 150 % (910 x 485 DIPs of work area) fits 910 x 485; on a 1920 x 1080 monitor the same window is 940 x 600 again (the design size, not the fitted one); a UI scale not known: 940 x 600");
            var fit = Method(Read("Dialogs", "FirstRunWindow.Screens.cs"), "private void FitToWorkArea(bool center)");
            expect(fit.Contains("var placed = FitTo(work, DesignSizeAt(scale));", StringComparison.Ordinal) && fit.Contains("LayoutTransform is ScaleTransform transform ? transform.ScaleX : 1", StringComparison.Ordinal)
                   && !fit.Contains("ActualWidth", StringComparison.Ordinal) && !fit.Contains("Math.Clamp", StringComparison.Ordinal)
                   && fit.IndexOf("(MaxWidth, MaxHeight) = (work.Width, work.Height);", StringComparison.Ordinal) < fit.IndexOf("(Width, Height) = (placed.Width, placed.Height);", StringComparison.Ordinal),
                "FitToWorkArea fits the design size at the window's UI scale (its root's LayoutTransform), never its current size; MaxWidth and MaxHeight first; no Math.Clamp");
        }
        finally
        {
            (settings.UiScalePercent, settings.FollowWindowsDpiScaling) = kept;
        }
    }

    // ----- first-run-encoder-estimate -----

    private static void EncoderEstimate(Action<bool, string> expect)
    {
        (string Name, HardwareBenchmarkResult Result, string Expected)[] estimates =
        [
            ("the Plex Server's GTX 1070", PlexServer(HardwareBenchmarkResult.KindEstimate), "h264_nvenc, hevc_nvenc"),
            ("the XPS 13's Adreno", Xps13(HardwareBenchmarkResult.KindEstimate), "h264_mf, hevc_mf, av1_mf"),
            ("the Desktop's RX 7900 XTX and Arc A310", Desktop(HardwareBenchmarkResult.KindEstimate), "h264_qsv, hevc_qsv, av1_qsv, h264_amf, hevc_amf, av1_amf"),
            ("an RX 7900 XTX listed twice beside Intel UHD 770", Twin(HardwareBenchmarkResult.KindEstimate), "h264_qsv, hevc_qsv, av1_qsv, h264_amf, hevc_amf, av1_amf"),
            ("an H.264-only NVIDIA card beside an Adreno", Machine(HardwareBenchmarkResult.KindEstimate,
                Card(0, "NVIDIA card", 0x10DE, 1, 4096, ["H.264"]), Card(1, "Qualcomm(R) Adreno(TM) GPU", 0x4D4F4351, 2, 128, AllCodecs, unified: true)), "h264_nvenc, hevc_mf, av1_mf"),
        ];
        foreach (var (name, result, expected) in estimates)
        {
            var names = FirstRunWindow.EstimatedEncoders(result);
            expect(string.Join(", ", names) == expected && names.All(EncoderProber.HardwareEncoders.Contains),
                $"{name}: {string.Join(", ", names)} (a Media Foundation one only for a codec no vendor's covers; every name one the probe knows)");
        }

        expect(FirstRunWindow.ExportEncoderSuffix(Desktop(HardwareBenchmarkResult.KindEstimate)) == "_amf" && FirstRunWindow.ExportEncoderSuffix(ExportElsewhere(HardwareBenchmarkResult.KindEstimate)) == "_qsv"
               && FirstRunWindow.ExportEncoderSuffix(Laptop(HardwareBenchmarkResult.KindEstimate)) == "_nvenc" && FirstRunWindow.ExportEncoderSuffix(Xps13(HardwareBenchmarkResult.KindEstimate)) == "_mf"
               && FirstRunWindow.ExportEncoderSuffix(null) is null,
            "the Export card's family: AMF on the Desktop (Export Auto: the RX 7900 XTX), QuickSync where the Arc A380 exports, NVENC on the laptop, Media Foundation on the Adreno");

        string[] desktopFfmpeg = ["h264_qsv", "hevc_qsv", "av1_qsv", "h264_amf", "hevc_amf", "av1_amf"];
        var (ordered, best) = FirstRunWindow.RankEncoders(desktopFfmpeg, "_amf");
        expect(best == "h264_amf" && string.Join(", ", ordered) == "h264_amf, hevc_amf, av1_amf, h264_qsv, hevc_qsv, av1_qsv",
            "the Desktop: AMF first and h264_amf chosen (the card that composites and exports), not the Arc A310's QuickSync");
        expect(FirstRunWindow.RankEncoders(desktopFfmpeg, null).Best == "h264_qsv" && FirstRunWindow.RankEncoders(["h264_nvenc", "h264_qsv"], "_qsv").Best == "h264_qsv"
               && FirstRunWindow.RankEncoders([], "_amf").Best == "libx264" && FirstRunWindow.RankEncoders(["hevc_amf"], "_amf").Best == "libx264",
            "with no profile, NVENC, QuickSync, AMF, Media Foundation as before; with no H.264 hardware encoder, libx264");

        // On windows of their own (never shown): the estimate and FFmpeg's answer, in either order. FFmpeg's always ends chosen.
        var settings = AppSettings.Current;
        var keptEncoder = settings.DefaultVideoEncoder;
        settings.DefaultVideoEncoder = "";
        try
        {
            var first = new FirstRunWindow(live: false);
            first.ShowProfile(Desktop(HardwareBenchmarkResult.KindEstimate));
            var estimated = Shown(first);
            var said = first.EncoderStatusText.Text;
            first.ShowVerifiedEncoders(["h264_qsv", "hevc_qsv"]);
            var verified = Shown(first);
            first.ShowProfile(Desktop(HardwareBenchmarkResult.KindQuick));
            var after = Shown(first);
            expect(estimated == "h264_amf* hevc_amf av1_amf h264_qsv hevc_qsv av1_qsv" && said.StartsWith("Estimated from the graphics cards", StringComparison.Ordinal),
                "the estimate first: the Desktop's six estimated, h264_amf chosen, said to be estimated");
            expect(verified == "h264_qsv* hevc_qsv" && after == verified,
                "then FFmpeg's answer (no AMF in this FFmpeg): its two only, h264_qsv chosen; a later profile changes nothing");

            var reverse = new FirstRunWindow(live: false);
            reverse.ShowVerifiedEncoders(["h264_amf", "hevc_amf", "h264_qsv"]);
            var before = Shown(reverse);
            reverse.ShowProfile(Desktop(HardwareBenchmarkResult.KindEstimate));
            var ranked = Shown(reverse);
            expect(before == "h264_qsv* h264_amf hevc_amf" && ranked == "h264_amf* hevc_amf h264_qsv",
                "FFmpeg's answer first, then the estimate: still FFmpeg's three only, ranked for the Export card once the profile names it (h264_amf chosen)");

            reverse.EncoderBox.SelectedItem = ((IEnumerable<HandPegApp.Models.EncoderOption>)reverse.EncoderBox.ItemsSource).First(e => e.Name == "hevc_amf");
            reverse.ShowProfile(Desktop(HardwareBenchmarkResult.KindQuick));
            reverse.ShowVerifiedEncoders(["h264_amf", "hevc_amf"]);
            expect(Shown(reverse) == "h264_amf hevc_amf*", "an encoder chosen by hand stays chosen through a new profile and a new answer");

            var adreno = new FirstRunWindow(live: false);
            adreno.ShowProfile(Xps13(HardwareBenchmarkResult.KindEstimate));
            var options = ((IEnumerable<HandPegApp.Models.EncoderOption>)adreno.EncoderBox.ItemsSource).ToList();
            expect(Shown(adreno) == "h264_mf* hevc_mf av1_mf" && options[0].DisplayName == "H.264 (Qualcomm Adreno)",
                "the XPS 13: the Media Foundation encoders named for the Adreno (from the profile's own cards), h264_mf chosen");
            foreach (var window in new[] { first, reverse, adreno })
                window.Close();
        }
        finally
        {
            settings.DefaultVideoEncoder = keptEncoder;
        }
    }

    // The hardware encoders a window offers, the chosen one starred (an EncoderOption is a record: the one chosen is the one equal to it).
    private static string Shown(FirstRunWindow window) => string.Join(" ",
        ((IEnumerable<HandPegApp.Models.EncoderOption>)window.EncoderBox.ItemsSource).Where(e => e.IsHardware)
        .Select(e => e.Name + (Equals(e, window.EncoderBox.SelectedItem) ? "*" : "")));

    // ----- first-run-studio-layout -----

    private static void StudioLayout(Action<bool, string> expect)
    {
        var settings = AppSettings.Current;
        var keptTheme = settings.Theme;
        var window = new FirstRunWindow(live: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            Idle();

            // ----- The size: 940 x 600 of HandPeg's DIPs, as UiScale scales every dialog; inside the work area -----
            var root = (FrameworkElement)window.Content;
            var scale = root.LayoutTransform is ScaleTransform { ScaleX: var s } ? s : 1;
            var work = window.WorkAreaHere();
            var clamped = 940 * scale > work.Width || 600 * scale > work.Height;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"        this monitor: the window {window.Width:0.#} x {window.Height:0.#} DIPs at a UI scale of {scale:0.###}, work area {work.Width:0.#} x {work.Height:0.#}{(clamped ? " (fitted)" : "")}"));
            expect(clamped ? window.Width <= work.Width + 0.5 && window.Height <= work.Height + 0.5
                   : Math.Abs(window.Width / scale - 940) < 0.5 && Math.Abs(window.Height / scale - 600) < 0.5,
                "the window is 940 x 600 HandPeg DIPs (its size over its UI scale), or the work area where that is smaller");
            expect(Math.Abs(window.MaxWidth - work.Width) < 0.5 && Math.Abs(window.MaxHeight - work.Height) < 0.5 && window.ResizeMode == ResizeMode.NoResize,
                "its largest size is the work area in its own DIPs (set after UiScale: never scaled twice), and it is not resized by hand");

            // ----- Screen 1 -----
            expect(window.Screen == 1 && window.Screen1Panel.IsVisible && window.Screen2Panel.Visibility == Visibility.Collapsed
                   && window.ScreenTitle.Text == "Workspace, Theme & Core Engines" && window.StepText.Text == "Step 1 of 2",
                "it opens on Screen 1, Workspace, Theme & Core Engines (Step 1 of 2); Screen 2 collapsed");
            expect(window.NextButton.IsVisible && window.NextButton.IsDefault && window.BackButton.Visibility == Visibility.Hidden && !window.StartButton.IsVisible
                   && Inside(window.NextButton, root),
                "Next is the default button, inside the window; no Back, no Start Editing yet");
            expect(window.Swatches.Select(s => s.Theme).SequenceEqual(ThemeManager.Themes) && window.Swatches.Count(s => s.Checked) == 1
                   && window.Swatches.Single(s => s.Checked).Theme == ThemeManager.Normalize(settings.Theme),
                "five theme swatches, ThemeManager's themes in order, the theme in effect checked: " + string.Join(", ", window.Swatches.Select(s => s.Theme)));
            expect(window.StagePreview.Kind == StageKind.Workspace && window.StagePreview.WorkspaceName == "Standard Editing" && window.StagePreview.ActualHeight > 100,
                "the live miniature draws the chosen layout, Standard Editing");

            var layouts = MainWindow.BuiltInLayoutStates(["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"]).ToDictionary(l => l.Name, l => l.Layout.Main);
            var cards = new[] { window.StandardLayoutCard, window.ColorLayoutCard, window.AssemblyLayoutCard, window.ShortsLayoutCard, window.VerticalCreatorLayoutCard };
            foreach (var card in cards)
            {
                var name = (string)card.Tag;
                var centre = card.TranslatePoint(new Point(card.ActualWidth / 2, card.ActualHeight / 2), window);
                var hit = window.InputHitTest(centre) as DependencyObject;
                window.HoverAt(centre);
                expect(Within(hit, card) && window.StagePreview.WorkspaceName == name && window.StagePreview.Workspace is { } drawn
                       && DockManager.Shape(drawn) == DockManager.Shape(layouts[name]) && Inside(card, root),
                    $"the pointer at the middle of the {name} card hits that card, and the miniature draws that layout's shape");
            }

            var swatch = window.ThemeSwatches.Items.OfType<RadioButton>().Single(r => (string)r.Tag == ThemeManager.Reno);
            window.HoverAt(swatch.TranslatePoint(new Point(swatch.ActualWidth / 2, swatch.ActualHeight / 2), window));
            var hovered = window.StagePreview.ThemeOverride;
            window.HoverAt(null);
            expect(hovered == ThemeManager.Reno && window.StagePreview.ThemeOverride == ThemeManager.Normalize(settings.Theme) && window.StagePreview.WorkspaceName == "Standard Editing",
                "over the Reno swatch the miniature is drawn in Reno; the pointer gone, the chosen layout and theme come back");

            window.ColorLayoutCard.IsChecked = true;
            var chosen = window.StagePreview.WorkspaceName;
            window.StandardLayoutCard.IsChecked = true;
            expect(chosen == "Color & Effects" && window.StagePreview.WorkspaceName == "Standard Editing", "a card chosen is the layout drawn when the pointer is elsewhere");

            swatch.IsChecked = true;
            expect(settings.Theme == ThemeManager.Reno && window.StagePreview.ThemeOverride == ThemeManager.Reno, "a swatch checked puts its theme into effect at once (Reno), the miniature with it");

            // The miniature draws (OnRender's Workspace kind) into a bitmap: more than a blank.
            var stage = window.StagePreview;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(stage.ActualWidth), (int)Math.Ceiling(stage.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(stage);
            var pixels = new int[bitmap.PixelWidth * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            expect(pixels.Distinct().Count() > 8, "the miniature is drawn: a window, its panes, the player's frame, the timeline's Clips");

            // ----- Screen 2 -----
            window.GoToScreen(2);
            Idle();
            expect(window.Screen == 2 && window.Screen2Panel.IsVisible && window.Screen1Panel.Visibility == Visibility.Collapsed
                   && window.ScreenTitle.Text == "Hardware Auto-Tune & Default Export" && window.StepText.Text == "Step 2 of 2",
                "Next: Screen 2, Hardware Auto-Tune & Default Export (Step 2 of 2)");
            expect(window.StartButton.IsVisible && window.StartButton.IsDefault && (string)window.StartButton.Content == "Start Editing" && !window.NextButton.IsVisible
                   && window.BackButton.IsVisible && Inside(window.StartButton, root),
                "Start Editing is the default button, inside the window; Back beside it");
            expect(window.EncoderBox.IsVisible && window.AudioEncoderBox.IsVisible && window.PromptBox.IsVisible && window.FallbackBox.IsVisible && window.CrashReportsBox.IsVisible,
                "the encoder and audio boxes, Prompt to save, the software fallback and crash reports are on it");

            window.ShowProfile(Desktop(HardwareBenchmarkResult.KindEstimate));
            Idle();
            var estimated = Cards(window);
            expect(estimated.Count == 5 && estimated.Select(c => c.Role).SequenceEqual(["Compositor", "Decode", "Background", "Export", "NPU"])
                   && estimated.Take(4).All(c => c is { Badge: "Estimated", Measured: false }),
                "the estimate: five role cards (Compositor, Decode, Background, Export, NPU), each GPU badge Estimated");
            expect(estimated[0].Card == "AMD Radeon RX 7900 XTX" && estimated[1].Card == "Auto · AMD Radeon RX 7900 XTX" && estimated[2].Card == "Auto · AMD Radeon RX 7900 XTX"
                   && estimated[3].Card == "Auto · AMD Radeon RX 7900 XTX" && estimated[4] is { Card: "No NPU found", Badge: "" },
                "the Desktop: the RX 7900 XTX composites; Decode, Background and Export read Auto · the RX 7900 XTX; no NPU");
            expect(new[] { "CompositorRole", "DecodeRole", "BackgroundRole", "ExportRole", "NpuRole" }.All(id => Descendants(window.RoleCards).Any(e => AutomationProperties.GetAutomationId(e) == id)),
                "each card is an inspector card with its automation id (CompositorRole ... NpuRole)");

            var measuring = FirstRunWindow.RoleCardsOf(Desktop(HardwareBenchmarkResult.KindEstimate), measuring: true);
            expect(measuring.Take(4).All(c => c.Badge == "Measuring...") && measuring[4].Badge == "",
                "while the quick check runs, the GPU badges read Measuring...");

            window.ShowProfile(Desktop(HardwareBenchmarkResult.KindQuick));
            Idle();
            var quick = Cards(window);
            window.ShowProfile(Desktop(HardwareBenchmarkResult.KindEstimate));
            expect(quick.Take(4).All(c => c is { Badge: "870 fps · 9,870 MB/s", Measured: true }) && Cards(window).SequenceEqual(quick),
                "the quick check's figures: 870 fps · 9,870 MB/s on the compositor's card, for Decode and Background too; a late estimate changes nothing");

            var xps = FirstRunWindow.RoleCardsOf(Xps13(HardwareBenchmarkResult.KindEstimate), measuring: false);
            var bus = Desktop(HardwareBenchmarkResult.KindQuick);
            bus.Adapters[0].HardwareDecodeFps = 0;
            expect(xps[0].Detail.StartsWith("Shares the computer's memory", StringComparison.Ordinal) && xps[4] is { Badge: "Detected" } && xps[4].Card.Contains("Hexagon", StringComparison.Ordinal)
                   && FirstRunWindow.RoleCardsOf(bus, measuring: false)[0].Badge == "9,870 MB/s",
                "the XPS 13: the Adreno shares the computer's memory, the Hexagon NPU Detected; the bus alone before FFmpeg: 9,870 MB/s");

            // 5.5A review (B1): a quick check whose compositor's card failed: the role cards say Auto and Not measured on the card the
            // listing named, never the A310 it did not measure.
            var failedQuick = Desktop(HardwareBenchmarkResult.KindQuick);
            (failedQuick.Adapters[0].HardwareDecodeFps, failedQuick.Adapters[0].ReadbackMBps, failedQuick.Adapters[0].Note) = (0, 0, "Device removed.");
            failedQuick.Recommended = HardwareBenchmark.QuickRoles(failedQuick, Desktop(HardwareBenchmarkResult.KindEstimate).Recommended, [0]);
            var failedCards = FirstRunWindow.RoleCardsOf(failedQuick, measuring: false);
            expect(failedCards[0] is { Card: "Auto · AMD Radeon RX 7900 XTX", Badge: "Not measured", Measured: false } && failedCards.Take(4).All(c => !c.Card.Contains("A310", StringComparison.Ordinal)),
                "the RX 7900 XTX's device failing in the quick check: the Compositor card reads Auto · AMD Radeon RX 7900 XTX, Not measured; no role card names the A310");

            // 5.5A review (B3): the quick check's status lines say what was measured, from the numbers.
            var clipFailed = Desktop(HardwareBenchmarkResult.KindQuick);
            clipFailed.Adapters[0].HardwareDecodeFps = 0;
            var noFfmpeg = Desktop(HardwareBenchmarkResult.KindQuick);
            (noFfmpeg.Adapters[0].HardwareDecodeFps, noFfmpeg.Clip) = (0, "");
            (string What, string Said, string Expected)[] lines =
            [
                ("decoded", FirstRunWindow.QuickStatus(Desktop(HardwareBenchmarkResult.KindQuick)), "Measured in the background in 1.4 s: decoding and the bus on the cards that hold a role."),
                ("the clip did not decode", FirstRunWindow.QuickStatus(clipFailed), "The bus measured in the background in 1.4 s; the test clip did not decode in hardware."),
                ("no FFmpeg yet", FirstRunWindow.QuickStatus(noFfmpeg), "The bus measured in the background; the decoding is measured once FFmpeg is downloaded."),
                ("nothing measured", FirstRunWindow.QuickStatus(failedQuick), "Estimated from the cards: the quick check could not measure them, and a card it could not measure holds no role."),
                ("Settings, decoded", SettingsWindow.QuickResultStatus(Desktop(HardwareBenchmarkResult.KindQuick)), "A quick check at the first start (decoding and the bus on the cards that hold a role): run the test for the full measurements."),
                ("Settings, no FFmpeg yet", SettingsWindow.QuickResultStatus(noFfmpeg), "A quick check at the first start (the bus on the cards that hold a role): run the test for the full measurements."),
                ("Settings, nothing measured", SettingsWindow.QuickResultStatus(failedQuick), "A quick check at the first start could not measure the cards: run the test for the full measurements."),
            ];
            foreach (var (what, said, expected) in lines)
                expect(said == expected, $"the quick check's status, {what}: \"{said}\"");

            // 5.5.0 (the user's Q10 b): a failed quick check says so in a warning line, and Settings sets the recommended roles in one click.
            expect(window.ProfileWarningText.Visibility == Visibility.Collapsed
                   && FirstRunWindow.ProfileWarning("Device removed.") == "Warning: the quick hardware check failed (Device removed). The roles below are estimates: run Test & Auto-Tune Hardware in Settings > Export & Hardware > GPU & NPU Roles, then Apply Recommended Roles.",
                $"(5.5.0, Q10 b) the warning line: hidden while nothing failed; a failed quick check: \"{FirstRunWindow.ProfileWarning("Device removed.")}\"");
            var profileSource = Read("Dialogs", "FirstRunWindow.Profile.cs");
            expect(profileSource.Contains("ShowProfileWarning(why);", StringComparison.Ordinal) && profileSource.Contains("ShowProfileWarning(null);", StringComparison.Ordinal)
                   && Read("Dialogs", "FirstRunWindow.xaml").Contains("x:Name=\"ProfileWarningText\" Visibility=\"Collapsed\" Foreground=\"{DynamicResource WarningTextBrush}\"", StringComparison.Ordinal),
                "(5.5.0, Q10 b) shown in the warning colour when the quick check fails (or the cards could not be looked at), hidden again when a later one measures");
            var rolesXaml = Read("Dialogs", "SettingsWindow.xaml");
            expect(SettingsWindow.CanApplyRoles(Desktop(HardwareBenchmarkResult.KindQuick)) == HardwareBenchmarkService.IsCurrent(Desktop(HardwareBenchmarkResult.KindQuick))
                   && !SettingsWindow.CanApplyRoles(null) && !SettingsWindow.CanApplyRoles(new HardwareBenchmarkResult { Error = "Stopped." })
                   && rolesXaml.Contains("x:Name=\"ApplyRolesButton\" Content=\"Apply Recommended Roles\"", StringComparison.Ordinal)
                   && rolesXaml.IndexOf("x:Name=\"BenchmarkButton\"", StringComparison.Ordinal) < rolesXaml.IndexOf("x:Name=\"ApplyRolesButton\"", StringComparison.Ordinal)
                   && rolesXaml.IndexOf("x:Name=\"ApplyRolesButton\"", StringComparison.Ordinal) < rolesXaml.IndexOf("x:Name=\"CompositorGpuBox\"", StringComparison.Ordinal),
                "(5.5.0, Q10 b) Settings > Export & Hardware > GPU & NPU Roles: Apply Recommended Roles beside the test, for a kept result that holds for these cards (none, a stopped one, or one of other cards: off)");

            // 5.5A review (C-8): Prompt to save says when it is asked (only with Keep open project tabs off, the Settings box it names).
            var tip = window.PromptBox.ToolTip as string ?? "";
            var settingsXaml = Read("Dialogs", "SettingsWindow.xaml");
            expect(tip.Contains("Asked only when Keep open project tabs suspended between sessions is off (Settings > General > Projects and saving)", StringComparison.Ordinal)
                   && tip.Contains("closing keeps every tab, saved or not, for the next start", StringComparison.Ordinal)
                   && settingsXaml.Contains("<CheckBox Content=\"Keep open project tabs suspended between sessions", StringComparison.Ordinal)
                   && settingsXaml.IndexOf("<TabItem Header=\"General\">", StringComparison.Ordinal) < settingsXaml.IndexOf("Text=\"Projects and saving\"", StringComparison.Ordinal)
                   && settingsXaml.IndexOf("Text=\"Projects and saving\"", StringComparison.Ordinal) < settingsXaml.IndexOf("<TabItem Header=\"Timeline &amp; Player\">", StringComparison.Ordinal),
                "Prompt to save before closing: asked only when Keep open project tabs suspended between sessions is off (Settings > General > Projects and saving, where that box is); while on, closing keeps every tab");
            expect(tip.Contains("is off (Settings > General > Projects and saving), as it is at first", StringComparison.Ordinal)
                   && tip.Contains("one dialog lists them all and can keep every tab for the next start instead", StringComparison.Ordinal) && !tip.Contains("while that is on, as it is at first", StringComparison.Ordinal),
                "(5.5A addenda) and that box is off at first, so closing asks: with two tabs or more, in one dialog that can keep every tab for the next start");

            // 5.5A addenda (AD-3, the user's call): no way in the first run to use an FFmpeg already on the PC; Settings keeps its own.
            var panel = window.Dependencies;
            var shownWords = Words(panel).ToList();
            expect(!panel.OffersExistingFfmpeg && panel.UseExistingButton.Visibility == Visibility.Collapsed,
                "(addenda) the first run offers no existing FFmpeg: the component panel's Use Existing button is not there (OffersExistingFfmpeg off)");
            expect(shownWords.Count > 0 && !shownWords.Any(w => w.Contains("existing", StringComparison.OrdinalIgnoreCase) || w.Contains("already have", StringComparison.OrdinalIgnoreCase)
                       || w.Contains("local folder", StringComparison.OrdinalIgnoreCase)),
                "(addenda) none of the words the panel shows mentions one");
            Console.WriteLine($"        the component panel shows: {string.Join(" | ", shownWords)}");
            var panelCode = Read("Controls", "DependencyPanel.xaml.cs").Split('\n').Where(l => l.Contains("already have", StringComparison.Ordinal)).ToList();
            expect(panelCode.Count == 2 && panelCode.All(l => l.Contains("(OffersExistingFfmpeg ? ", StringComparison.Ordinal)),
                "(addenda) and its messages (a failed look online, a failed download) speak of one only while OffersExistingFfmpeg is on");
            var firstRunCode = Read("Dialogs", "FirstRunWindow.xaml.cs") + Read("Dialogs", "FirstRunWindow.Screens.cs") + Read("Dialogs", "FirstRunWindow.Profile.cs");
            expect(!Read("Dialogs", "FirstRunWindow.xaml").Contains("OffersExistingFfmpeg=\"True\"", StringComparison.Ordinal)
                   && !firstRunCode.Contains("UseExisting", StringComparison.Ordinal) && !firstRunCode.Contains("Use Existing Local Folder", StringComparison.Ordinal)
                   && !firstRunCode.Contains("OpenFolderDialog", StringComparison.Ordinal),
                "(addenda) the label, the hook and the folder picker are gone from the first run");
            expect(Method(firstRunCode, "private void StartLiveWork(") is var live && live.Contains("DependencyUpdater.Installed += installed;", StringComparison.Ordinal)
                   && live.Contains("MeasureAgainWithDecoding();", StringComparison.Ordinal) && firstRunCode.Split("MeasureAgainWithDecoding();").Length == 2,
                "(addenda) the quick check measures again with decoding only once FFmpeg is installed (DependencyUpdater.Installed), its one call");
            expect(settingsXaml.Contains("AutomationProperties.Name=\"ffmpeg.exe Path\"", StringComparison.Ordinal) && settingsXaml.Contains("Tag=\"{Binding ElementName=FfmpegPathBox}\" Click=\"Browse_Click\"", StringComparison.Ordinal),
                "(addenda) Settings keeps its way to use a local FFmpeg: the ffmpeg.exe Path box with Browse...");

            window.GoToScreen(1);
            Idle();
            expect(window.Screen == 1 && window.Screen1Panel.IsVisible && window.NextButton.IsDefault && !window.StartButton.IsVisible, "Back to Screen 1");
        }
        finally
        {
            window.Close();
            Idle();
        }

        expect(settings.Theme == keptTheme, "closed without Start Editing, the theme in effect before comes back");
    }

    private static List<FirstRunRoleCard> Cards(FirstRunWindow window) => [.. ((IEnumerable<FirstRunRoleCard>)window.RoleCards.ItemsSource)];

    // The words an element shows (its text, its content, its tooltip), and those of everything in it not collapsed or hidden (the logical
    // tree: the component panel is out of the goldens' window, so never laid out).
    private static IEnumerable<string> Words(DependencyObject element)
    {
        if (element is UIElement { Visibility: not Visibility.Visible })
            yield break;
        if (element is TextBlock { Text: { Length: > 0 } text })
            yield return text;
        if (element is ContentControl { Content: string { Length: > 0 } content })
            yield return content;
        if (element is FrameworkElement { ToolTip: string { Length: > 0 } tip })
            yield return tip;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
        {
            foreach (var word in Words(child))
                yield return word;
        }
    }

    // Whether an element is inside another's bounds (the window's content), in that one's DIPs.
    private static bool Inside(FrameworkElement element, FrameworkElement root)
    {
        var box = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return box.Width > 0 && box.Left >= -0.5 && box.Top >= -0.5 && box.Right <= root.ActualWidth + 0.5 && box.Bottom <= root.ActualHeight + 0.5;
    }

    // Whether a hit element is the element given or inside it.
    private static bool Within(DependencyObject? hit, DependencyObject element)
    {
        for (var at = hit; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, element))
                return true;
        }

        return false;
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

    // The window's thread turned until it is idle: layout, Loaded and the bindings done.
    private static void Idle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
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
}
