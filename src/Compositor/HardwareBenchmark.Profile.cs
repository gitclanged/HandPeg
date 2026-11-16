using System.Globalization;
using System.IO;
using System.Text.Json;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Two-phase hardware profile for the first-run window -----
//
// Phase 1 (--hw-benchmark --estimate): adapters (DXGI descriptions, unified-memory flag), MF hardware encoders per adapter (MFTEnum2
// by LUID), NPUs and derived roles, with nothing measured: no D3D11 device (no video driver load), clip, FFmpeg or fingerprint. Target
// under 100 ms (TotalSeconds); adapters arrive on the first "adapters" line so the role cards fill at once.
//
// Phase 2 (--hw-benchmark [clip] --quick --adapters 0[,2]): only the named adapters (compositor, plus Export when different; never an
// adapter without a role), each measuring hardware decode of the test clip (when FFmpeg made one) and bus bandwidth in ~1.5 s.
// No software decode, seeks, accuracy frames or cross-adapter decode.
public static unsafe partial class HardwareBenchmark
{
    /// <summary>Phase 1: adapters, encoders, NPUs and roles; nothing measured.</summary>
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
            // Warm the JSON serializer (~20 ms of type inspection on first use) in parallel with enumeration.
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
            AppLog.Write("Hardware test failed", ex);
            result.Error = ex.Message;
        }

        result.TotalSeconds = Math.Round(Clock.Elapsed.TotalSeconds, 3);
        Write(new HardwareBenchmarkLine { Type = "result", Result = result });
        return result.Error.Length == 0 ? 0 : 1;
    }

    /// <summary>
    /// Phase 2: lists every adapter as the full test does (decoders included: the editor reads them from the saved result) and, on
    /// <paramref name="only"/>, measures hardware decode fps of <paramref name="clip"/> (skipped without a clip) and bus bandwidth.
    /// Adapters whose device creation fails get a note, as in the full test.
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

            // NPUs before the first line, matching the estimate (this line replaces it in the window).
            result.Npus = ListNpus();

            // Roles from the listing alone, before measuring: the fallback when an adapter fails (QuickRoles).
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
                        // A hardware decode failure leaves decode fps at 0 without a note; the bus is still measured.
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

                    // Later progress lines carry roles without the failed adapter (the window may persist them on Start Editing).
                    result.Recommended = QuickRoles(result, listed, failed);
                }
            }

            // All-slots compositor timing on the measured adapters (failed ones get no role).
            TimeCompositors(result, [.. targets.Where(a => !failed.Contains(a.Index))], 0.9, 0.97);

            result.MediaFoundationDecodeCodecs = MediaFoundationDecoders();
            result.Fingerprint = GpuAdapters.Fingerprint;
            result.Recommended = QuickRoles(result, listed, failed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Hardware test failed", ex);
            result.Error = ex.Message;
        }

        result.TotalSeconds = Math.Round(Clock.Elapsed.TotalSeconds, 2);
        Write(new HardwareBenchmarkLine { Type = "result", Result = result });
        return result.Error.Length == 0 ? 0 : 1;
    }

    /// <summary>Parses the --adapters list ("0,2"); non-numeric entries are skipped.</summary>
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
    /// Quick-check roles: <see cref="Recommend"/> while every measured adapter succeeded. After a failure (<paramref name="failed"/>),
    /// keeps the pre-measurement roles (<paramref name="listed"/>) with the failed adapter's role(s) and Decode/Background set to Auto,
    /// because Recommend would otherwise move the role onto an unmeasured secondary adapter (a small card beside a discrete primary
    /// whose device failed). Pure (goldens).
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

    /// <summary>Adapters DXGI lists twice are measured and given roles once; each repeat notes the adapter it duplicates.</summary>
    private static void NoteRepeats(HardwareBenchmarkResult result)
    {
        foreach (var repeat in result.Adapters.Where(a => a.SameCardAs >= 0))
            repeat.Note = string.Create(CultureInfo.InvariantCulture, $"The same card as #{repeat.SameCardAs}, listed twice by Windows.");
    }

    /// <summary>NPUs listed by DXCore, with their use.</summary>
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
