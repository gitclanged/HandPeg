using System.Globalization;
using System.Text;
using HandPegApp.Compositor;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// hardware-tier (Pass 5.2-Arm64AndNpu): the rules that follow from the hardware, as pure functions, each answer compared with
/// what it should be as well as with the golden file. Unified memory (a Snapdragon X's Adreno: a 128 MB carve-out, the
/// computer's memory shared) and the frame cache's Auto budget it gets (an eighth of the computer's memory, 1.5 to 2.5 GB from
/// 12 GB up, less below; a discrete card's rule unchanged); what Auto-Captions run on (the NPU, the graphics card, the
/// processor) from the choice and what is installed; the colour FFmpeg's fallback decoder reports, in Media Foundation's
/// numbers. What this PC itself has goes to the console only, so the golden text is the same everywhere.
/// </summary>
public static class HardwareTierTests
{
    private const long Mb = 1L << 20, Gb = 1L << 30;

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("hardware-tier",
            "# Hardware tiers (5.2-Arm64AndNpu): unified memory and the frame cache's Auto budget, what Auto-Captions run on, FFmpeg's colour in Media Foundation's numbers.",
            Tier);
        yield return Case("mf-enumeration",
            "# Media Foundation's lists of transforms (5.2E): a codec no decoder takes, after one that has decoders, is counted as none and frees nothing (the heap corruption of the hardware benchmark on a Windows without HEVC, VP9 or AV1 decoders).",
            Enumeration);
    }

    // A Windows 11 IoT LTSC PC (no HEVC, VP9 or AV1 decoder) ended the hardware benchmark with 0xC0000374 every time, and passes
    // since the lists are counted by CountMfts: a list that finds nothing must free nothing, whatever the call leaves in the pointer
    // (the loop's local kept the pointer freed on the pass before). Here a subtype no decoder takes follows one that has decoders, twice
    // over. (Windows on ARM's MFTEnumEx sets the pointer to null when it finds nothing, as the console note says, so the old code
    // survived there: what Windows LTSC does, or MFTEnum2 with a card's LUID there, left it standing.)
    private static void Enumeration(StringBuilder text, List<string> problems)
    {
        MediaFoundation.Start();
        var none = new Guid("00000000-1111-2222-3333-444444444444");
        var h264 = HardwareBenchmark.CountDecoders(HandPegApp.Compositor.Native.MfGuids.H264);
        var (first, second) = (HardwareBenchmark.CountDecoders(none), HardwareBenchmark.CountDecoders(none));
        var listed = HardwareBenchmark.MediaFoundationDecoders();
        Console.WriteLine($"  note  this PC's Media Foundation decoders: {string.Join(", ", listed)} ({h264} for H.264)");

        // The mechanism, on this Windows (console only: it is the OS's behaviour, not HandPeg's): a pointer set before a call that finds
        // nothing, as the loop's last pass left it.
        unsafe
        {
            var input = new HandPegApp.Compositor.Native.MftRegisterTypeInfo { MajorType = HandPegApp.Compositor.Native.MfGuids.Video, Subtype = none };
            var kept = (nint*)0x1234;
            uint found = 7;
            var hr = HandPegApp.Compositor.Native.Mf.MFTEnumEx(HandPegApp.Compositor.Native.MfGuids.VideoDecoderCategory, HandPegApp.Compositor.Native.Mf.EnumAll | HandPegApp.Compositor.Native.Mf.EnumSortAndFilter, &input, null, &kept, &found);
            Console.WriteLine($"  note  MFTEnumEx finding nothing: hr 0x{hr:X8}, count {found}, the array pointer {(kept == (nint*)0x1234 ? "left as it was (freed again by the old loop)" : kept == null ? "set to null" : "set")}");
        }
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        Expect(h264 > 0 && first == 0 && second == 0, "a subtype no decoder takes counts none, twice, right after one with decoders: nothing freed that the call did not give");
        Expect(listed.Contains("H.264"), "the decoders' list is still made (H.264 is in every Windows)");
    }

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }

    private static void Line(StringBuilder text, FormattableString line) =>
        text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Expect<T>(List<string> problems, string what, T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: {actual}, not {expected}"));
    }

    private static void Tier(StringBuilder text, List<string> problems)
    {
        // ----- Which cards share the computer's memory -----
        text.Append("unified memory (DXCore's IsIntegrated, else a carve-out of at most 1 GB with four times as much shared):\n");
        (string Card, bool? Integrated, long Dedicated, long Shared, bool Warp, bool Expected)[] cards =
        [
            ("Adreno X1-85 (DXCore: integrated; 128 MB, 7990 MB shared)", true, 128 * Mb, 7990 * Mb, false, true),
            ("Adreno X1-85 without DXCore (128 MB, 7990 MB shared)", null, 128 * Mb, 7990 * Mb, false, true),
            ("a laptop chip's graphics without DXCore (512 MB, 8 GB shared)", null, 512 * Mb, 8 * Gb, false, true),
            ("RTX 4090 (24 GB, 32 GB shared)", null, 24 * Gb, 32 * Gb, false, false),
            ("a 512 MB discrete card DXCore calls discrete (8 GB shared)", false, 512 * Mb, 8 * Gb, false, false),
            ("WARP", null, 0, 8 * Gb, true, false),
        ];
        foreach (var card in cards)
        {
            var unified = MemoryTier.IsUnifiedMemory(card.Integrated, card.Dedicated, card.Shared, card.Warp);
            Line(text, $"  {card.Card}: {(unified ? "unified" : "its own")}");
            Expect(problems, $"unified memory of {card.Card}", unified, card.Expected);
        }

        // ----- The unified-memory cache -----
        text.Append("frame cache, unified memory (Auto): an eighth of the computer's memory, 1.5 to 2.5 GB from 12 GB up, proportionally less below:\n");
        (string Memory, long Bytes, long Expected)[] memories =
        [
            ("64 GB", 64 * Gb, 2560 * Mb),
            ("32 GB", 32 * Gb, 2560 * Mb),
            ("16 GB", 16 * Gb, 2048 * Mb),
            ("16 GB as Windows counts it on a 16 GB laptop (16755752960 bytes)", 16755752960, 16755752960 / 8),
            ("12 GB", 12 * Gb, 1536 * Mb),
            ("8 GB", 8 * Gb, 1024 * Mb),
            ("4 GB", 4 * Gb, 512 * Mb),
            ("not known", 0, 512 * Mb),
        ];
        foreach (var memory in memories)
        {
            var budget = MemoryTier.UnifiedBudget(memory.Bytes);
            Line(text, $"  {memory.Memory}: {budget / (double)Mb:0.#} MB");
            Expect(problems, $"the unified budget with {memory.Memory}", budget, memory.Expected);
        }

        // ----- Every kind of card, through the whole Auto rule -----
        text.Append("frame cache Auto budget (before Windows' caps):\n");
        (string Card, bool Warp, bool Unified, long Dedicated, long Physical, bool Busy, long Free, long Expected)[] budgets =
        [
            ("WARP", true, false, 0, 16 * Gb, false, 0, 512 * Mb),
            ("Adreno X1-85, 16 GB computer", false, true, 128 * Mb, 16 * Gb, false, 0, 2048 * Mb),
            ("Adreno X1-85, 16 GB computer, Windows asked (7 GB free)", false, true, 128 * Mb, 16 * Gb, false, 7 * Gb, 2048 * Mb),
            ("a 256 MB card of its own", false, false, 256 * Mb, 16 * Gb, false, 0, 512 * Mb),
            ("an 8 GB card, Windows not asked yet", false, false, 8 * Gb, 32 * Gb, false, 0, 1024 * Mb),
            ("an 8 GB card, a game on it", false, false, 8 * Gb, 32 * Gb, true, 2 * Gb, 1024 * Mb),
            ("a 24 GB card, 22 GB free (80% of the card, max(2 GB, 20%) kept free)", false, false, 24 * Gb, 64 * Gb, false, 22 * Gb, 22 * Gb - 24 * Gb / 5),
            ("a 24 GB card, Windows not asked yet", false, false, 24 * Gb, 64 * Gb, false, 0, 3 * Gb),
        ];
        foreach (var b in budgets)
        {
            var budget = MemoryTier.AutoBudget(b.Warp, b.Unified, b.Dedicated, b.Physical, b.Busy, b.Free);
            Line(text, $"  {b.Card}: {budget / (double)Mb:0.#} MB");
            Expect(problems, $"the Auto budget of {b.Card}", budget, b.Expected);
        }

        // ----- What Auto-Captions run on -----
        text.Append("Auto-Captions (whisper.cpp, its silence detection inside it): choice, NPU build, graphics back end, fell over before -> device, --no-gpu, why:\n");
        (string Choice, bool Npu, string Backend, bool Fell, string Device, bool NoGpu)[] plans =
        [
            (AppSettings.SpeechOnAuto, false, "OpenCL", false, AppSettings.SpeechOnGpu, false),
            (AppSettings.SpeechOnAuto, false, "OpenCL", true, AppSettings.SpeechOnCpu, true),
            (AppSettings.SpeechOnAuto, false, "", false, AppSettings.SpeechOnCpu, false),
            (AppSettings.SpeechOnAuto, true, "OpenCL", false, AppSettings.SpeechOnNpu, true),
            (AppSettings.SpeechOnNpu, false, "OpenCL", false, AppSettings.SpeechOnGpu, false),
            (AppSettings.SpeechOnNpu, false, "", false, AppSettings.SpeechOnCpu, false),
            (AppSettings.SpeechOnNpu, true, "", false, AppSettings.SpeechOnNpu, true),
            (AppSettings.SpeechOnGpu, false, "CUDA", true, AppSettings.SpeechOnGpu, false),
            (AppSettings.SpeechOnGpu, false, "", false, AppSettings.SpeechOnCpu, false),
            (AppSettings.SpeechOnCpu, false, "OpenCL", false, AppSettings.SpeechOnCpu, true),
        ];
        foreach (var p in plans)
        {
            var plan = CaptionGenerator.ChooseSpeechAccelerator(p.Choice, p.Npu, p.Backend, p.Fell);
            var backend = p.Backend.Length > 0 ? p.Backend : "none";
            Line(text, $"  {p.Choice}, {(p.Npu ? "NPU build" : "no NPU build")}, {backend}, {(p.Fell ? "fell over" : "fine")} -> {plan.Device}{(plan.NoGpu ? " --no-gpu" : "")}: {plan.Why}");
            Expect(problems, $"the device for {p.Choice} ({backend})", plan.Device, p.Device);
            Expect(problems, $"--no-gpu for {p.Choice} ({backend})", plan.NoGpu, p.NoGpu);
        }

        // ----- FFmpeg's colour in Media Foundation's numbers -----
        text.Append("FFmpeg's colour as Media Foundation numbers it (the compositor's YUV to RGB reads these):\n");
        (string Space, int Matrix)[] spaces = [("bt709", 1), ("smpte170m", 2), ("bt470bg", 2), ("smpte240m", 3), ("bt2020nc", 4), ("bt2020c", 4), ("unknown", 1), ("", 1)];
        foreach (var (space, matrix) in spaces)
        {
            var got = FfmpegVideoDecoder.MatrixOf(space);
            Line(text, $"  matrix {(space.Length > 0 ? space : "(not said)")}: {got}");
            Expect(problems, $"the matrix of {space}", got, matrix);
        }

        (string Transfer, int Function)[] transfers = [("bt709", 5), ("smpte170m", 5), ("smpte2084", 15), ("arib-std-b67", 16), ("iec61966-2-1", 7), ("bt2020-10", 13), ("unknown", 0)];
        foreach (var (transfer, function) in transfers)
        {
            var got = FfmpegVideoDecoder.TransferOf(transfer);
            Line(text, $"  transfer {transfer}: {got}");
            Expect(problems, $"the transfer of {transfer}", got, function);
        }

        // This PC, for a look: the console only (the golden text is the same everywhere).
        var physical = SystemCommit.PhysicalMemory();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        this PC: {physical / (double)Gb:0.0} GB of memory, a unified-memory cache of {MemoryTier.UnifiedBudget(physical) / (double)Mb:0} MB; NPUs: {string.Join(", ", NpuAdapters.Enumerate().Select(NpuAdapters.Describe).DefaultIfEmpty("none"))}; Auto-Captions: {CaptionGenerator.PlanSpeech().Why}"));
    }
}
