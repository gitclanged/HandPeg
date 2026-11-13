using System.Globalization;
using System.IO;
using System.Text.Json;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- The first-run window's two-phase hardware profile (Pass 5.5A) -----
//
// Phase 1, HandPeg.exe --hw-benchmark --estimate: the cards (DXGI's descriptions, whether each shares the computer's memory), the
// hardware encoders Media Foundation has for each (MFTEnum2 by LUID) and the NPUs, and the roles that follow, with nothing measured:
// no Direct3D 11 device (no card's video driver loaded), no clip, no FFmpeg, no GpuAdapters.Fingerprint. Its own time is in the result
// (TotalSeconds; under 100 ms is the aim), and the cards arrive on the first "adapters" line, so the window fills its role cards at once.
//
// Phase 2, HandPeg.exe --hw-benchmark [clip] --quick --adapters 0[,2]: only the cards named (the compositor's, and the Export card when
// it is another: never a card with no role, CLAUDE.md 8), each its hardware decoding of the test clip (when there is one: FFmpeg makes
// it) and its bus, in about a second and a half. No processor decode, no seeks, no accuracy frames, no decoding across two cards.
public static unsafe partial class HardwareBenchmark
{
    /// <summary>The estimate (Phase 1): the cards, their encoders, the NPUs and the roles; nothing measured.</summary>
    private static int Estimate()
    {
        Clock.Start();
        var result = new HardwareBenchmarkResult
        {
            Kind = HardwareBenchmarkResult.KindEstimate,
            MeasuredAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        try
        {
            // The JSON writer's first line costs it about 20 ms of looking at the types: done beside the listing, not after it.
            var warm = Task.Run(() => JsonSerializer.Serialize(new HardwareBenchmarkLine { Type = "progress", Progress = new() { Partial = new() { Adapters = [new()], Npus = [new()] } } }, Json));
            _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
            result.Adapters = EnumerateAdapters(decoderProfiles: false);
            NoteRepeats(result);
            result.Npus = ListNpus();
            result.Recommended = Recommend(result);
            warm.Wait();
            Report("adapters", -1, 0.9, CardsAndNpus(result), result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Error = ex.Message;
        }

        result.TotalSeconds = Math.Round(Clock.Elapsed.TotalSeconds, 3);
        Write(new HardwareBenchmarkLine { Type = "result", Result = result });
        return result.Error.Length == 0 ? 0 : 1;
    }

    /// <summary>
    /// The quick check (Phase 2): every card listed as the full test lists it (its decoders too: what the editor reads from the saved
    /// result), and on the cards in <paramref name="only"/> the hardware decoding of <paramref name="clip"/> (frames per second; none
    /// without a clip) and the bus. A card whose device cannot be made is noted, as in the full test.
    /// </summary>
    private static int MeasureQuick(string clip, IReadOnlySet<int> only)
    {
        Clock.Start();
        var result = new HardwareBenchmarkResult
        {
            Kind = HardwareBenchmarkResult.KindQuick,
            MeasuredAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        try
        {
            _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
            result.Adapters = EnumerateAdapters();
            NoteRepeats(result);

            // 5.5A review: the NPUs before the first line, as the estimate has them (the window shows this line over the estimate).
            result.Npus = ListNpus();

            // The roles the listing names, before anything is measured: what a card that fails to be measured falls back on (QuickRoles).
            var listed = Recommend(result);
            result.Recommended = listed;
            var targets = result.Adapters.Where(a => only.Contains(a.Index) && a.SameCardAs < 0).ToList();
            Report("adapters", -1, 0.1, CardsAndNpus(result), result);

            var decodes = clip.Length > 0 && File.Exists(clip);
            var share = 0.8 / Math.Max(targets.Count, 1);
            var failed = new List<int>();
            for (var i = 0; i < targets.Count; i++)
            {
                var adapter = targets[i];
                try
                {
                    using var gpu = GpuDevice.Create(adapter.Index, allowWarp: false);
                    if (decodes)
                    {
                        // A card that will not decode the clip in hardware still has its bus measured: no note, the decode left at 0.
                        try
                        {
                            var (fps, _, _) = DecodeRun(gpu, clip, hardware: true, out var frames, quick: true);
                            adapter.HardwareDecodeFps = Math.Round(fps, 1);
                            result.Clip = string.Create(CultureInfo.InvariantCulture, $"H.264 1920x1080 60 fps, {frames} frames");
                        }
                        catch (Exception ex) when (ex is GpuException or IOException or InvalidDataException)
                        {
                            result.Clip = "H.264 1920x1080 60 fps";
                        }

                        Report("decode-hw", adapter.Index, 0.1 + share * (i + 0.5), string.Create(CultureInfo.InvariantCulture, $"{adapter.Name}: {adapter.HardwareDecodeFps:0} fps"), result);
                    }

                    (adapter.ReadbackMBps, adapter.UploadMBps) = Bus(gpu);
                    result.Recommended = QuickRoles(result, listed, failed);
                    Report("transfer", adapter.Index, 0.1 + share * (i + 1), string.Create(CultureInfo.InvariantCulture, $"{adapter.Name}: {adapter.ReadbackMBps:0} MB/s"), result);
                }
                catch (Exception ex) when (ex is GpuException or IOException or InvalidDataException)
                {
                    adapter.Note = ex.Message;
                    failed.Add(adapter.Index);

                    // The progress lines after it carry the roles without it already (the window may write them on Start Editing).
                    result.Recommended = QuickRoles(result, listed, failed);
                }
            }

            result.MediaFoundationDecodeCodecs = MediaFoundationDecoders();
            result.Fingerprint = GpuAdapters.Fingerprint;
            result.Recommended = QuickRoles(result, listed, failed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Error = ex.Message;
        }

        result.TotalSeconds = Math.Round(Clock.Elapsed.TotalSeconds, 2);
        Write(new HardwareBenchmarkLine { Type = "result", Result = result });
        return result.Error.Length == 0 ? 0 : 1;
    }

    /// <summary>The cards a quick check measures, from its --adapters word ("0,2"); a word that is not a number is left out.</summary>
    internal static IReadOnlySet<int> ParseAdapters(string list)
    {
        var cards = new HashSet<int>();
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                cards.Add(index);
        }

        return cards;
    }

    /// <summary>
    /// The roles a quick check gives (5.5A review): <see cref="Recommend"/> over what it listed and measured while every card it measured
    /// worked. Once one failed (its device not made, its bus not measured: <paramref name="failed"/>), the roles the listing named before
    /// anything was measured (<paramref name="listed"/>), the failed card's role(s) Auto, Decode and Background Auto as always: Recommend
    /// would leave the failed card out and move its role onto a card the quick check never measured (the Desktop's Arc A310 beside an RX
    /// 7900 XTX whose device failed: CLAUDE.md 8). Pure: the goldens check it.
    /// </summary>
    internal static HardwareBenchmarkRoles QuickRoles(HardwareBenchmarkResult result, HardwareBenchmarkRoles listed, IReadOnlyCollection<int> failed)
    {
        if (failed.Count == 0)
            return Recommend(result);

        string NotMeasured(int index) => result.Adapters.FirstOrDefault(a => a.Index == index) is { } card
            ? card.Note.Length > 0 ? $"{card.Name} could not be measured ({card.Note.TrimEnd('.')})" : $"{card.Name} could not be measured"
            : string.Create(CultureInfo.InvariantCulture, $"Card {index} could not be measured");
        string Kept(string role, string otherwise) => listed.Reasons.FirstOrDefault(r => r.StartsWith(role + ":", StringComparison.Ordinal)) ?? otherwise;

        var compositorFailed = listed.Compositor >= 0 && failed.Contains(listed.Compositor);
        var exportFailed = listed.Export >= 0 && failed.Contains(listed.Export);
        var roles = new HardwareBenchmarkRoles
        {
            Compositor = compositorFailed ? -1 : listed.Compositor,
            Decode = -1,
            Background = -1,
            Export = exportFailed ? -1 : listed.Export,
        };
        roles.Reasons.Add(compositorFailed
            ? $"Compositor: Auto. {NotMeasured(listed.Compositor)}, and the quick check never gives a role to a card it did not measure: run the test in Settings > Export & Hardware."
            : Kept("Compositor", "Compositor: Auto."));
        roles.Reasons.Add("Decode: Auto, the compositor's card.");
        roles.Reasons.Add("Background: Auto, the compositor's card.");
        roles.Reasons.Add(exportFailed ? $"Export: Auto, the compositor's card. {NotMeasured(listed.Export)}." : Kept("Export", "Export: Auto."));
        return roles;
    }

    /// <summary>A card DXGI lists twice is measured, and given a role, once: each repeat says which card it repeats.</summary>
    private static void NoteRepeats(HardwareBenchmarkResult result)
    {
        foreach (var repeat in result.Adapters.Where(a => a.SameCardAs >= 0))
            repeat.Note = string.Create(CultureInfo.InvariantCulture, $"The same card as #{repeat.SameCardAs}, listed twice by Windows.");
    }

    /// <summary>The NPUs DXCore lists, each with what it is used for.</summary>
    private static List<HardwareBenchmarkNpu> ListNpus() =>
    [
        .. NpuAdapters.Enumerate().Select(n => new HardwareBenchmarkNpu
        {
            Name = n.Name, Vendor = n.Vendor, VendorId = n.VendorId, DeviceId = n.DeviceId, DriverVersion = n.DriverVersion,
            SharedSystemMemoryMb = n.SharedMemoryMb, GenericMl = n.GenericMl, UsedFor = NpuAdapters.UsedFor,
        }),
    ];

    private static string CardsAndNpus(HardwareBenchmarkResult result) => result.Npus.Count > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{result.Adapters.Count} graphics card(s), {result.Npus.Count} NPU(s)")
        : string.Create(CultureInfo.InvariantCulture, $"{result.Adapters.Count} graphics card(s)");
}
