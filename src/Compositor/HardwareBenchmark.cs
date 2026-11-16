using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// HandPeg.exe --hw-benchmark &lt;clip&gt;: measures the adapters for the GPU roles in a separate process (a crashing driver takes only it
/// down) and writes one JSON line per step to stdout (<see cref="HardwareBenchmarkLine"/>, read by HardwareBenchmarkService). In about two
/// seconds:
/// <list type="bullet">
/// <item>every DXGI adapter, its VRAM, driver decode profiles (ID3D11VideoDevice) and MF hardware encoders;</item>
/// <item>the 1080p60 H.264 test clip decoded once on the CPU and on each adapter into VRAM: fps, median of six seeks, and GPU frames vs. the
/// CPU reference (PSNR, SSIM, bit-exactness; exposes chroma or stride driver faults);</item>
/// <item>each adapter's bus: one frame read back and one uploaded (MB/s); with two adapters, decoding on the second and copying to the first
/// vs. decoding on the first;</item>
/// <item>the four recommended roles;</item>
/// <item>unified-memory flag per adapter, the codecs MF decodes and those the compositor's FFmpeg fallback decodes instead
/// (--ffmpeg &lt;path&gt;: its decoder list), and NPUs via DXCore (listed, not measured).</item>
/// </list>
/// The first-run window's faster modes are in HardwareBenchmark.Profile.cs: --estimate (adapters, encoders and roles, nothing measured) and
/// --quick (decode and bus on the adapters holding a role).
/// </summary>
public static unsafe partial class HardwareBenchmark
{
    public const string Argument = "--hw-benchmark";

    /// <summary>First-run window modes: instant estimate, quick check, and the adapters the quick check measures ("0,2").</summary>
    public const string EstimateOption = "--estimate", QuickOption = "--quick", AdaptersOption = "--adapters";

    // The app's FFmpeg (--ffmpeg), for the codecs its fallback decodes; empty when not given.
    private static string _ffmpeg = "";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly double[] SeekPoints = [0.37, 1.13, 0.71, 1.41, 0.05, 0.93];
    private static readonly double[] AccuracyPoints = [0.25, 0.75, 1.25];

    private static StreamWriter? _out;
    private static readonly Stopwatch Clock = new();

    public static int Run(string[] args)
    {
        _ = HandPegApp.Native.Kernel32.SetErrorMode(0x0001 | 0x0002);

        // Log unhandled exceptions (e.g. a driver call on a timing thread) with their stack before exit; the result line carries only
        // the message.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception unhandled)
                AppLog.Write("Hardware test: unhandled exception", unhandled);
        };

        // The clip is optional (none for the estimate; a quick check without one measures only the bus); the mode selects the body.
        var clip = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "";
        _ffmpeg = CompositorWorker.OptionValue(args, CompositorWorker.FfmpegOption);
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var exitCode = 0;
        Func<int> body = args.Contains(EstimateOption) ? Estimate
            : args.Contains(QuickOption) ? () => MeasureQuick(clip, ParseAdapters(CompositorWorker.OptionValue(args, AdaptersOption)))
            : () => Measure(clip, ParseAdapters(CompositorWorker.OptionValue(args, AdaptersOption)));
        var thread = new Thread(() => exitCode = body()) { Name = "Benchmark" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        _out.Dispose();
        return exitCode;
    }

    /// <summary>
    /// Full test. The clip is optional (a fresh install has no FFmpeg to generate it): without it adapters are listed, buses measured and the
    /// compositor timed, with no decoding (the window reruns once FFmpeg is installed). The compositor stage times the recommended adapters
    /// and those in <paramref name="chosen"/> (--adapters: roles chosen in Settings).
    /// </summary>
    private static int Measure(string clip, IReadOnlySet<int> chosen)
    {
        Clock.Start();
        var decodes = clip.Length > 0 && File.Exists(clip);
        var result = new HardwareBenchmarkResult { MeasuredAt = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), Clip = decodes ? "H.264 1920x1080 60 fps" : "" };
        try
        {
            _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
            result.Adapters = EnumerateAdapters();
            result.Fingerprint = GpuAdapters.Fingerprint;
            result.Npus = ListNpus();
            result.MediaFoundationDecodeCodecs = MediaFoundationDecoders();
            result.FfmpegFallbackCodecs = FfmpegFallbackCodecs(result.MediaFoundationDecodeCodecs);
            Report("adapters", -1, 0.1, result.Npus.Count > 0
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{result.Adapters.Count} graphics card(s), {result.Npus.Count} NPU(s)")
                : $"{result.Adapters.Count} graphics card(s)", result);

            // An adapter DXGI lists twice is measured once.
            NoteRepeats(result);
            var hardware = result.Adapters.Where(a => a.Note.Length == 0).ToList();
            var share = 0.6 / Math.Max(hardware.Count + 1, 1);

            // CPU decode once: the reference for every adapter's frames (test clip only).
            byte[][]? reference = null;
            if (decodes)
            {
                double softwareFps = 0, softwareSeek = 0;
                using (var gpu = GpuDevice.Create(hardware.Count > 0 ? hardware[0].Index : -1))
                {
                    (softwareFps, softwareSeek, reference) = DecodeRun(gpu, clip, hardware: false, out var frames);
                    result.Clip = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"H.264 1920x1080 60 fps, {frames} frames");
                }

                foreach (var adapter in result.Adapters)
                    (adapter.SoftwareDecodeFps, adapter.SoftwareSeekMs) = (Math.Round(softwareFps, 1), Math.Round(softwareSeek, 2));
                Report("decode-sw", -1, 0.1 + share, $"Processor: {softwareFps:0} fps", result);
            }

            for (var i = 0; i < hardware.Count; i++)
            {
                var adapter = hardware[i];
                try
                {
                    using var gpu = GpuDevice.Create(adapter.Index, allowWarp: false);
                    if (decodes)
                    {
                        var (fps, seek, frames) = DecodeRun(gpu, clip, hardware: true, out _);
                        (adapter.HardwareDecodeFps, adapter.HardwareSeekMs) = (Math.Round(fps, 1), Math.Round(seek, 2));
                        Report("decode-hw", adapter.Index, 0.1 + share * (i + 1.5), $"{adapter.Name}: {fps:0} fps", result);

                        if (reference is not null && frames is not null)
                            Compare(adapter, reference, frames);
                    }

                    (adapter.ReadbackMBps, adapter.UploadMBps) = Bus(gpu);
                    Report("transfer", adapter.Index, 0.1 + share * (i + 2), decodes
                        ? $"{adapter.Name}: PSNR {(adapter.BitExact ? "bit-exact" : adapter.PsnrDb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))}"
                        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{adapter.Name}: {adapter.ReadbackMBps:0} MB/s"), result);
                }
                catch (Exception ex) when (ex is GpuException or IOException or InvalidDataException)
                {
                    adapter.Note = ex.Message;
                }
            }

            // Time the compositor's all-slots frame on adapters holding a role (HardwareBenchmark.Compositor.cs).
            TimeCompositors(result, CompositorTargets(result, chosen), 0.7, 0.88);

            if (decodes && hardware.Where(a => a.DecodesInHardware && a.SameCardAs < 0).Select(a => (a.VendorId, a.DeviceId)).Distinct().Count() >= 2)
            {
                try
                {
                    result.CrossAdapter = CrossAdapter(clip, hardware);
                    Report("cross-adapter", -1, 0.92, "Two cards", result);
                }
                catch (Exception ex) when (ex is GpuException or IOException or InvalidOperationException)
                {
                    // Per-adapter results stand; only the comparison is missing.
                    Report("cross-adapter", -1, 0.92, $"The two-card test stopped short: {ex.Message}", result);
                }
            }

            result.Recommended = Recommend(result);
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

    private static void Report(string stage, int adapter, double fraction, string message, HardwareBenchmarkResult partial) =>
        Write(new HardwareBenchmarkLine
        {
            Type = "progress",
            Progress = new HardwareBenchmarkProgress { Stage = stage, Adapter = adapter, Fraction = Math.Clamp(fraction, 0, 1), Message = message, Partial = partial },
        });

    private static void Write(HardwareBenchmarkLine line) => _out!.WriteLine(JsonSerializer.Serialize(line, Json));

    // ----- Adapters -----

    /// <summary>
    /// Every DXGI adapter except the software renderer: memory, driver decode profiles and MF hardware encoders. Without
    /// <paramref name="decoderProfiles"/> (the estimate) no D3D11 device is created, so no video driver loads and the decoder list stays empty.
    /// </summary>
    internal static List<HardwareBenchmarkAdapter> EnumerateAdapters(bool decoderProfiles = true)
    {
        var adapters = new List<HardwareBenchmarkAdapter>();
        var factory = Dxgi.CreateFactory();
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                var adapter = Dxgi.EnumAdapter(factory, i);
                if (adapter == 0)
                    break;
                try
                {
                    var desc = Dxgi.Describe(adapter);
                    if ((desc.Flags & AdapterDesc1.FlagSoftware) != 0)
                        continue;
                    var entry = new HardwareBenchmarkAdapter
                    {
                        Index = (int)i,
                        Name = Dxgi.Name(desc),
                        VendorId = desc.VendorId,
                        DeviceId = desc.DeviceId,
                        Luid = desc.Luid,
                        DedicatedVideoMemoryMb = (long)(desc.DedicatedVideoMemory / (1024 * 1024)),
                        SharedSystemMemoryMb = (long)(desc.SharedSystemMemory / (1024 * 1024)),
                        UnifiedMemory = MemoryTier.IsUnifiedMemory(DxCore.IsIntegratedCard(desc.Luid), (long)desc.DedicatedVideoMemory, (long)desc.SharedSystemMemory),
                        Vendor = desc.VendorId switch { 0x1002 => "AMD", 0x8086 => "Intel", 0x10DE => "NVIDIA", 0x5143 or 0x4D4F4351 => "Qualcomm", 0x1414 => "Microsoft", _ => $"0x{desc.VendorId:X4}" },
                    };
                    if (decoderProfiles)
                        entry.DecodeCodecs = DecodeProfiles(adapter);
                    adapters.Add(entry);
                }
                finally
                {
                    Com.Release(ref adapter);
                }
            }
        }
        finally
        {
            Com.Release(ref factory);
        }

        EncodersByAdapter(adapters);

        // One physical adapter listed twice: repeats point at the first listing.
        foreach (var adapter in adapters)
        {
            var first = adapters.First(a => a.VendorId == adapter.VendorId && a.DeviceId == adapter.DeviceId);
            adapter.SameCardAs = first == adapter ? -1 : first.Index;
        }

        return adapters;
    }

    private static readonly (Guid Profile, string Codec)[] Profiles =
    [
        (new("1b81be68-a0c7-11d3-b984-00c04f2e73c5"), "H.264"),
        (new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0"), "HEVC"),
        (new("107af0e0-ef1a-4d19-aba8-67a163073d13"), "HEVC 10-bit"),
        (new("463707f8-a1d0-4585-876d-83aa6d60b89e"), "VP9"),
        (new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a"), "AV1"),
    ];

    /// <summary>Decode profiles the adapter's driver exposes (ID3D11VideoDevice::GetVideoDecoderProfile).</summary>
    private static List<string> DecodeProfiles(nint adapter)
    {
        var codecs = new List<string>();
        var levels = stackalloc uint[1] { D3D11.FeatureLevel11_0 };
        nint device = 0, context = 0;
        uint level;
        if (Dxgi.D3D11CreateDevice(adapter, D3D11.DriverUnknown, 0, D3D11.CreateVideoSupport, levels, 1, D3D11.SdkVersion, &device, &level, &context) < 0)
            return codecs;
        var video = Com.Query(device, Iid.D3D11VideoDevice);
        try
        {
            if (video == 0)
                return codecs;
            var count = ((delegate* unmanaged<nint, uint>)Com.Table(video)[ID3D11VideoDeviceSlot.GetVideoDecoderProfileCount])(video);
            for (uint i = 0; i < count; i++)
            {
                Guid profile;
                if (((delegate* unmanaged<nint, uint, Guid*, int>)Com.Table(video)[ID3D11VideoDeviceSlot.GetVideoDecoderProfile])(video, i, &profile) < 0)
                    continue;
                foreach (var (known, codec) in Profiles)
                {
                    if (profile == known && !codecs.Contains(codec))
                        codecs.Add(codec);
                }
            }
        }
        finally
        {
            Com.Release(ref video);
            Com.Release(ref context);
            Com.Release(ref device);
        }

        return [.. Profiles.Select(p => p.Codec).Where(codecs.Contains)];
    }

    private static readonly (Guid Subtype, string Codec)[] EncodedCodecs = [(MfGuids.H264, "H.264"), (MfGuids.Hevc, "HEVC"), (MfGuids.Av1, "AV1")];

    /// <summary>
    /// MF hardware encoders per adapter: MFTEnum2 filtered by LUID (MFT_ENUM_ADAPTER_LUID). All adapter/codec pairs are enumerated in
    /// parallel (each waits ~20 ms on driver registrations, so the estimate costs the slowest, not the sum), then sorted into codec order.
    /// </summary>
    private static void EncodersByAdapter(List<HardwareBenchmarkAdapter> adapters)
    {
        try
        {
            MediaFoundation.Start();
        }
        catch (GpuException)
        {
            return;
        }

        var found = new bool[adapters.Count * EncodedCodecs.Length];
        Parallel.For(0, found.Length, k => found[k] = HasHardwareEncoder(adapters[k / EncodedCodecs.Length].Luid, EncodedCodecs[k % EncodedCodecs.Length].Subtype));
        for (var k = 0; k < found.Length; k++)
        {
            var (adapter, codec) = (adapters[k / EncodedCodecs.Length], EncodedCodecs[k % EncodedCodecs.Length].Codec);
            if (found[k] && !adapter.EncodeCodecs.Contains(codec))
                adapter.EncodeCodecs.Add(codec);
        }
    }

    /// <summary>True when MF lists a hardware encoder for <paramref name="subtype"/> on adapter <paramref name="luid"/>.</summary>
    private static bool HasHardwareEncoder(long luid, Guid subtype)
    {
        nint attributes;
        if (Mf.MFCreateAttributes(&attributes, 1) < 0)
            return false;
        try
        {
            Mf.SetBlob(attributes, MfGuids.EnumAdapterLuid, &luid, sizeof(long));
            var output = new MftRegisterTypeInfo { MajorType = MfGuids.Video, Subtype = subtype };
            return CountMfts(MfGuids.VideoEncoderCategory, Mf.EnumHardware | Mf.EnumSortAndFilter, null, &output, attributes) > 0;
        }
        catch (GpuException)
        {
            // No encoders registered for this adapter.
            return false;
        }
        finally
        {
            Com.Release(ref attributes);
        }
    }

    private static readonly (Guid Subtype, string Codec)[] DecodedCodecs =
        [(MfGuids.H264, "H.264"), (MfGuids.Hevc, "HEVC"), (MfGuids.Vp9, "VP9"), (MfGuids.Av1, "AV1")];

    /// <summary>
    /// Codecs with an MF decoder of any kind (MFTEnumEx: hardware, software, Store extensions), i.e. what the compositor opens without FFmpeg.
    /// Driver decode profiles (DecodeProfiles) are not enough: Windows reaches the hardware HEVC decoder only via the HEVC Video Extensions.
    /// </summary>
    internal static List<string> MediaFoundationDecoders()
    {
        var codecs = new List<string>();
        try
        {
            MediaFoundation.Start();
        }
        catch (Exception ex) when (ex is GpuException or DllNotFoundException or EntryPointNotFoundException)
        {
            return codecs;
        }

        foreach (var (subtype, codec) in DecodedCodecs)
        {
            if (CountDecoders(subtype) > 0)
                codecs.Add(codec);
        }

        return codecs;
    }

    /// <summary>Number of MF decoders of any kind accepting <paramref name="subtype"/> (-1 on failure). Requires MF started.</summary>
    internal static int CountDecoders(Guid subtype)
    {
        var input = new MftRegisterTypeInfo { MajorType = MfGuids.Video, Subtype = subtype };
        return CountMfts(MfGuids.VideoDecoderCategory, Mf.EnumAll | Mf.EnumSortAndFilter, &input, null, 0);
    }

    /// <summary>
    /// Counts transforms listed by MFTEnumEx (no <paramref name="attributes"/>) or MFTEnum2, releasing each and freeing the array; -1 on
    /// failure. The pointer and count are reset before every call and only a returned array is freed: reusing a stale pointer across calls
    /// that leave it untouched caused a double free (heap corruption 0xC0000374) on systems lacking HEVC/VP9/AV1 decoders. Windows on ARM's
    /// MFTEnumEx nulls the pointer when nothing is found.
    /// </summary>
    private static int CountMfts(Guid category, uint flags, MftRegisterTypeInfo* input, MftRegisterTypeInfo* output, nint attributes)
    {
        nint* activates = null;
        uint count = 0;
        var result = attributes == 0
            ? Mf.MFTEnumEx(category, flags, input, output, &activates, &count)
            : Mf.MFTEnum2(category, flags, input, output, attributes, &activates, &count);
        if (activates is null)
            return result < 0 ? -1 : 0;

        for (var i = 0; i < count; i++)
        {
            var activate = activates[i];
            Com.Release(ref activate);
        }

        Com.CoTaskMemFree((nint)activates);
        return result < 0 ? -1 : (int)count;
    }

    // FFmpeg decoder names per codec (ffmpeg -decoders): native ones, plus dav1d/libaom for AV1 (FFmpeg's native AV1 decoder needs a GPU).
    private static readonly (string Codec, string[] Decoders)[] FfmpegDecoders =
        [("H.264", new[] { "h264" }), ("HEVC", new[] { "hevc" }), ("VP9", new[] { "vp9", "libvpx-vp9" }), ("AV1", new[] { "libdav1d", "libaom-av1" })];

    /// <summary>
    /// Codecs without an MF decoder that the compositor's FFmpeg fallback (FfmpegVideoDecoder) can decode, per the app FFmpeg's decoder list;
    /// empty when no FFmpeg was given, it is missing, or it fails to report.
    /// </summary>
    private static List<string> FfmpegFallbackCodecs(List<string> mediaFoundation)
    {
        var codecs = new List<string>();
        if (_ffmpeg.Length == 0 || !File.Exists(_ffmpeg))
            return codecs;

        string listed;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(_ffmpeg, "-hide_banner -decoders")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (process is null)
                return codecs;
            JobObjects.Assign(process.Id, ProcessClass.Foreground, efficiencyMode: false);
            var errors = process.StandardError.ReadToEndAsync();
            listed = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return codecs;
            }

            _ = errors.Wait(1000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException)
        {
            return codecs;
        }

        // One decoder per line: " V....D hevc   HEVC (High Efficiency Video Coding)".
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] == 'V')
                names.Add(parts[1]);
        }

        foreach (var (codec, decoders) in FfmpegDecoders)
        {
            if (!mediaFoundation.Contains(codec) && decoders.Any(names.Contains))
                codecs.Add(codec);
        }

        return codecs;
    }

    // ----- Decoding -----

    /// <summary>
    /// Decodes the clip start to end (fps), runs six seeks (median ms) and reads back the frames at the accuracy points; with
    /// <paramref name="quick"/> (first-run quick check) fps only.
    /// </summary>
    private static (double Fps, double SeekMs, byte[][]? Frames) DecodeRun(GpuDevice gpu, string clip, bool hardware, out int frameCount, bool quick = false)
    {
        using var decoder = new MfVideoDecoder(gpu, clip, 0, hardware);
        if (hardware && !decoder.Hardware)
            throw new GpuException(unchecked((int)0xC00D5212), "No hardware decoder for H.264 on this card");

        var duration = decoder.DurationSeconds > 0 ? decoder.DurationSeconds : 1.5;
        var step = decoder.FrameDuration;
        var clock = Stopwatch.StartNew();
        frameCount = 0;
        for (var t = 0.0; t < duration - step / 2; t += step)
        {
            if (!decoder.ShowAt(t))
                break;
            frameCount++;
        }

        Finish(gpu);
        var fps = frameCount / Math.Max(clock.Elapsed.TotalSeconds, 1e-6);
        if (quick)
            return (fps, 0, null);

        var seeks = new List<double>();
        foreach (var point in SeekPoints)
        {
            var at = Math.Min(point, duration - step);
            var started = Stopwatch.GetTimestamp();
            decoder.ShowAt(at);
            Finish(gpu);
            seeks.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        seeks.Sort();
        var frames = new byte[AccuracyPoints.Length][];
        for (var i = 0; i < AccuracyPoints.Length; i++)
        {
            decoder.ShowAt(Math.Min(AccuracyPoints[i], duration - step));
            frames[i] = decoder.ReadBack();
        }

        return (fps, (seeks[2] + seeks[3]) / 2, frames);
    }

    /// <summary>Waits for the GPU to drain submitted work (event query).</summary>
    private static void Finish(GpuDevice gpu)
    {
        var query = gpu.Device.CreateQuery(D3D11.QueryEvent);
        try
        {
            gpu.Context.End(query);
            int done;
            while (gpu.Context.GetData(query, &done, sizeof(int), 0) == Com.SFalse)
                Thread.SpinWait(32);
        }
        finally
        {
            Com.Release(ref query);
        }
    }

    /// <summary>Compares GPU frames to the CPU reference: PSNR over all planes, luma SSIM, bit-exactness.</summary>
    private static void Compare(HardwareBenchmarkAdapter adapter, byte[][] reference, byte[][] frames)
    {
        double squared = 0;
        long samples = 0;
        double ssim = 0;
        var exact = true;
        for (var i = 0; i < Math.Min(reference.Length, frames.Length); i++)
        {
            var (a, b) = (reference[i], frames[i]);
            if (a.Length != b.Length || a.Length == 0)
            {
                exact = false;
                continue;
            }

            for (var k = 0; k < a.Length; k++)
            {
                var d = a[k] - b[k];
                squared += d * d;
            }

            samples += a.Length;
            exact &= a.AsSpan().SequenceEqual(b);
            ssim += LumaSsim(a, b, 1920, 1080);
        }

        var mse = samples > 0 ? squared / samples : 0;
        adapter.BitExact = exact && samples > 0;
        adapter.PsnrDb = mse <= 0 ? 99 : Math.Round(Math.Min(10 * Math.Log10(255.0 * 255.0 / mse), 99), 2);
        adapter.Ssim = Math.Round(ssim / Math.Max(Math.Min(reference.Length, frames.Length), 1), 5);
    }

    private static double LumaSsim(byte[] a, byte[] b, int width, int height)
    {
        const double c1 = 6.5025, c2 = 58.5225;
        if (a.Length < width * height)
            return 0;
        double total = 0;
        var blocks = 0;
        for (var by = 0; by + 8 <= height; by += 8)
        {
            for (var bx = 0; bx + 8 <= width; bx += 8)
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (var y = 0; y < 8; y++)
                {
                    var row = (by + y) * width + bx;
                    for (var x = 0; x < 8; x++)
                    {
                        double p = a[row + x], q = b[row + x];
                        (sa, sb, saa, sbb, sab) = (sa + p, sb + q, saa + p * p, sbb + q * q, sab + p * q);
                    }
                }

                var (ma, mb) = (sa / 64, sb / 64);
                var (va, vb, cov) = (saa / 64 - ma * ma, sbb / 64 - mb * mb, sab / 64 - ma * mb);
                total += (2 * ma * mb + c1) * (2 * cov + c2) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                blocks++;
            }
        }

        return blocks > 0 ? total / blocks : 0;
    }

    // ----- Bus -----

    /// <summary>Reads back and uploads a 1080p BGRA frame (8 MB) eight times each: MB/s in both directions.</summary>
    private static (double Readback, double Upload) Bus(GpuDevice gpu)
    {
        const int width = 1920, height = 1080, rounds = 8;
        var bytes = (double)width * height * 4 * rounds / (1024 * 1024);
        using var texture = gpu.CreateTexture(width, height, DxgiFormat.B8G8R8A8Unorm, renderTarget: true);
        var staging = gpu.CreateStaging(width, height, DxgiFormat.B8G8R8A8Unorm);
        var upload = new byte[width * height * 4];
        try
        {
            gpu.Context.ClearRenderTargetView(texture.Rtv, 0.2f, 0.4f, 0.6f, 1);
            Finish(gpu);
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < rounds; i++)
            {
                gpu.Context.CopyResource(staging, texture.Texture);
                Com.Check(gpu.Context.Map(staging, 0, D3D11.MapRead, 0, out _), "Map");
                gpu.Context.Unmap(staging, 0);
            }

            var readback = bytes / Math.Max(clock.Elapsed.TotalSeconds, 1e-6);
            clock.Restart();
            fixed (byte* data = upload)
            {
                for (var i = 0; i < rounds; i++)
                    gpu.Context.UpdateSubresource(texture.Texture, 0, null, data, width * 4, 0);
            }

            Finish(gpu);
            var uploaded = bytes / Math.Max(clock.Elapsed.TotalSeconds, 1e-6);
            return (Math.Round(readback), Math.Round(uploaded));
        }
        finally
        {
            Com.Release(ref staging);
        }
    }

    /// <summary>Decoding on the second adapter with every frame copied to the first over the bus, vs. decoding on the first.</summary>
    private static HardwareBenchmarkCrossAdapter CrossAdapter(string clip, List<HardwareBenchmarkAdapter> hardware)
    {
        var cards = hardware.Where(a => a.SameCardAs < 0).ToList();
        var composite = cards.OrderByDescending(a => a.DedicatedVideoMemoryMb).First();
        var decode = cards.Where(a => (a.VendorId, a.DeviceId) != (composite.VendorId, composite.DeviceId) && a.DecodesInHardware)
            .OrderByDescending(a => a.HardwareDecodeFps).First();
        using var decodeGpu = GpuDevice.Create(decode.Index, allowWarp: false);
        using var compositeGpu = GpuDevice.Create(composite.Index, allowWarp: false);
        using var decoder = new MfVideoDecoder(decodeGpu, clip, 0, hardware: true);
        using var luma = compositeGpu.CreateTexture(decoder.Width, decoder.Height, DxgiFormat.R8Unorm, renderTarget: false);
        using var chroma = compositeGpu.CreateTexture(decoder.Width / 2, decoder.Height / 2, DxgiFormat.R8G8Unorm, renderTarget: false);
        if (!decoder.Hardware || decoder.TenBit)
            throw new GpuException(unchecked((int)0xC00D5212), "The decoding card gave no NV12 frames");
        var staging = decodeGpu.CreateStaging(decoder.Width, decoder.Height, DxgiFormat.Nv12);
        try
        {
            var frames = 0;
            var transfer = 0.0;
            var clock = Stopwatch.StartNew();
            var duration = decoder.DurationSeconds > 0 ? decoder.DurationSeconds : 1.5;
            for (var t = 0.0; t < duration - decoder.FrameDuration / 2 && frames < 60; t += decoder.FrameDuration)
            {
                if (!decoder.ShowAt(t) || decoder.FrameTexture == 0)
                    break;
                var started = Stopwatch.GetTimestamp();
                decodeGpu.Context.CopyResource(staging, decoder.FrameTexture);
                Com.Check(decodeGpu.Context.Map(staging, 0, D3D11.MapRead, 0, out var mapped), "Map");
                try
                {
                    compositeGpu.Context.UpdateSubresource(luma.Texture, 0, null, mapped.Data, mapped.RowPitch, 0);
                    compositeGpu.Context.UpdateSubresource(chroma.Texture, 0, null, mapped.Data + (long)mapped.RowPitch * decoder.Height, mapped.RowPitch, 0);
                }
                finally
                {
                    decodeGpu.Context.Unmap(staging, 0);
                }

                transfer += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                frames++;
            }

            Finish(compositeGpu);
            var crossFps = frames / Math.Max(clock.Elapsed.TotalSeconds, 1e-6);
            var perFrame = transfer / Math.Max(frames, 1);
            var megabytes = decoder.Width * decoder.Height * 1.5 / (1024 * 1024);
            return new HardwareBenchmarkCrossAdapter
            {
                DecodeAdapter = decode.Index,
                CompositeAdapter = composite.Index,
                CrossDecodeFps = Math.Round(crossFps, 1),
                LocalDecodeFps = composite.HardwareDecodeFps,
                TransferMsPerFrame = Math.Round(perFrame, 3),
                BandwidthMBps = Math.Round(megabytes / Math.Max(perFrame / 1000, 1e-6)),
                CrossIsFaster = crossFps > composite.HardwareDecodeFps,
            };
        }
        finally
        {
            Com.Release(ref staging);
        }
    }

    // ----- Roles -----

    /// <summary>The four recommended roles from the measurements, each with its reason.</summary>
    public static HardwareBenchmarkRoles Recommend(HardwareBenchmarkResult result)
    {
        var roles = new HardwareBenchmarkRoles();
        var working = result.Adapters.Where(a => a.Note.Length == 0 && a.SameCardAs < 0).ToList();
        if (working.Count == 0)
        {
            roles.Reasons.Add("No graphics card could be measured: every role is left on Auto.");
            return roles;
        }

        var compositor = working.OrderByDescending(a => a.DedicatedVideoMemoryMb).ThenBy(a => a.Index).First();
        roles.Compositor = compositor.Index;
        roles.Reasons.Add($"Compositor: {compositor.Name}, the card with the most video memory ({compositor.DedicatedVideoMemoryMb:N0} MB).");

        // Decode and Background stay Auto (the compositor GPU) whatever a secondary adapter measured: strip workers and decodes on a
        // secondary Intel Arc beside an RX 7900 XTX caused 0x116 VIDEO_TDR_FAILURE in the Arc driver. A secondary adapter is chosen only
        // manually in Settings.
        roles.Decode = -1;
        roles.Reasons.Add(result.CrossAdapter is { CrossIsFaster: true } cross
            ? $"Decode: Auto, the compositor's card. Card {cross.DecodeAdapter} decoding across the bus measured faster ({cross.CrossDecodeFps:0} against {cross.LocalDecodeFps:0} fps), but a second card is never chosen for you: pick it here if you want it."
            // The first-run estimate measures nothing (same roles, decoding measured next); its quick check without a test clip
            // (no FFmpeg yet) measures only the bus.
            : result.Kind == HardwareBenchmarkResult.KindEstimate
                ? "Decode: Auto, the compositor's card (measured next)."
            // The full test without a test clip (no FFmpeg yet) measures only bus and compositor, like the quick check.
            : result.Kind != HardwareBenchmarkResult.KindEstimate && !compositor.DecodesInHardware && result.Clip.Length == 0
                ? "Decode: Auto, the compositor's card (its decoding is measured once FFmpeg is here)."
            : compositor.DecodesInHardware
                ? $"Decode: Auto, the compositor's card ({compositor.HardwareDecodeFps:0} fps): frames stay in its memory, nothing crosses the bus."
                : "Decode: Auto (the compositor's card did not decode the test clip in hardware).");

        var second = working.Where(a => (a.VendorId, a.DeviceId) != (compositor.VendorId, compositor.DeviceId) && a.DecodesInHardware)
            .OrderByDescending(a => a.HardwareDecodeFps).FirstOrDefault();
        roles.Background = -1;
        roles.Reasons.Add(second is not null
            ? $"Background: Auto, the compositor's card. {second.Name} also decodes, but a second card is never chosen for you: pick it here if you want thumbnails, waveforms and proxies there."
            : "Background: Auto, the compositor's card.");

        static int Score(HardwareBenchmarkAdapter a) => (a.EncodeCodecs.Contains("AV1") ? 4 : 0) + (a.EncodeCodecs.Contains("HEVC") ? 2 : 0) + (a.EncodeCodecs.Contains("H.264") ? 1 : 0);
        // Exports are composited on the Export GPU (GpuExport.ExportAdapter), so the compositor adapter wins ties (e.g. two adapters both
        // encoding AV1, HEVC and H.264); another adapter is chosen only when it encodes more codecs in hardware, and the compositor's own
        // adapter is stored as Auto.
        var encoder = working.Where(a => a.EncodeCodecs.Count > 0).OrderByDescending(Score).ThenBy(a => a == compositor ? 0 : 1).ThenBy(a => a.Index).FirstOrDefault();
        roles.Export = encoder is not null && encoder != compositor ? encoder.Index : -1;
        roles.Reasons.Add(encoder is null
            ? "Export: Auto (no hardware encoder was listed)."
            : encoder == compositor
                ? $"Export: Auto, the compositor's card ({string.Join(", ", encoder.EncodeCodecs)} in hardware)."
                : $"Export: {encoder.Name} ({string.Join(", ", encoder.EncodeCodecs)} in hardware, more than the compositor's card), leaving the compositor's card free.");
        return roles;
    }
}
