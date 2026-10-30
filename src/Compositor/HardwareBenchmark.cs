using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// HandPeg.exe --hw-benchmark &lt;clip&gt; (Pass 5.0-Core, micro-step 5.0-0): measures the graphics cards for the GPU roles,
/// in a process of its own (a driver that falls over takes only it down), and writes one JSON line per step on its
/// standard output (<see cref="HardwareBenchmarkLine"/>, read by HardwareBenchmarkService). In about two seconds:
/// <list type="bullet">
/// <item>every card (DXGI), its video memory, the decoders its driver offers (ID3D11VideoDevice profiles) and the
/// hardware encoders Media Foundation lists for it;</item>
/// <item>the test clip (1080p60 H.264) decoded by the processor once, and by each card into video memory: frames per
/// second, the median time of six seeks, and the cards' frames against the processor's (PSNR, SSIM, bit-exactness: a
/// driver with a chroma or stride fault shows here);</item>
/// <item>each card's bus: a frame read back and one uploaded (MB/s); with two cards, decoding on the second and handing
/// the frames to the first, against decoding on the first;</item>
/// <item>the four roles that follows from it.</item>
/// </list>
/// </summary>
public static unsafe class HardwareBenchmark
{
    public const string Argument = "--hw-benchmark";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly double[] SeekPoints = [0.37, 1.13, 0.71, 1.41, 0.05, 0.93];
    private static readonly double[] AccuracyPoints = [0.25, 0.75, 1.25];

    private static StreamWriter? _out;
    private static readonly Stopwatch Clock = new();

    public static int Run(string[] args)
    {
        _ = CompositorWorker.SetErrorMode(0x0001 | 0x0002);
        var clip = args.Length > 1 ? args[1] : "";
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var exitCode = 0;
        var thread = new Thread(() => exitCode = Measure(clip)) { Name = "Benchmark" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        _out.Dispose();
        return exitCode;
    }

    private static int Measure(string clip)
    {
        Clock.Start();
        var result = new HardwareBenchmarkResult { MeasuredAt = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), Clip = "H.264 1920x1080 60 fps" };
        try
        {
            _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
            result.Adapters = EnumerateAdapters();
            result.Fingerprint = GpuAdapters.Fingerprint;
            Report("adapters", -1, 0.1, $"{result.Adapters.Count} graphics card(s)", result);
            if (!File.Exists(clip))
                throw new FileNotFoundException("The benchmark clip is missing.", clip);

            // A card DXGI lists twice is measured once.
            foreach (var repeat in result.Adapters.Where(a => a.SameCardAs >= 0))
                repeat.Note = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The same card as #{repeat.SameCardAs}, listed twice by Windows.");
            var hardware = result.Adapters.Where(a => a.Note.Length == 0).ToList();
            var share = 0.75 / Math.Max(hardware.Count + 1, 1);

            // The processor's decode, once: the reference for every card's pictures.
            byte[][]? reference = null;
            double softwareFps = 0, softwareSeek = 0;
            using (var gpu = GpuDevice.Create(hardware.Count > 0 ? hardware[0].Index : -1))
            {
                (softwareFps, softwareSeek, reference) = DecodeRun(gpu, clip, hardware: false, out var frames);
                result.Clip = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"H.264 1920x1080 60 fps, {frames} frames");
            }

            foreach (var adapter in result.Adapters)
                (adapter.SoftwareDecodeFps, adapter.SoftwareSeekMs) = (Math.Round(softwareFps, 1), Math.Round(softwareSeek, 2));
            Report("decode-sw", -1, 0.1 + share, $"Processor: {softwareFps:0} fps", result);

            for (var i = 0; i < hardware.Count; i++)
            {
                var adapter = hardware[i];
                try
                {
                    using var gpu = GpuDevice.Create(adapter.Index, allowWarp: false);
                    var (fps, seek, frames) = DecodeRun(gpu, clip, hardware: true, out _);
                    (adapter.HardwareDecodeFps, adapter.HardwareSeekMs) = (Math.Round(fps, 1), Math.Round(seek, 2));
                    Report("decode-hw", adapter.Index, 0.1 + share * (i + 1.5), $"{adapter.Name}: {fps:0} fps", result);

                    if (reference is not null && frames is not null)
                        Compare(adapter, reference, frames);
                    (adapter.ReadbackMBps, adapter.UploadMBps) = Bus(gpu);
                    Report("transfer", adapter.Index, 0.1 + share * (i + 2), $"{adapter.Name}: PSNR {(adapter.BitExact ? "bit-exact" : adapter.PsnrDb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))}", result);
                }
                catch (Exception ex) when (ex is GpuException or IOException or InvalidDataException)
                {
                    adapter.Note = ex.Message;
                }
            }

            if (hardware.Where(a => a.DecodesInHardware && a.SameCardAs < 0).Select(a => (a.VendorId, a.DeviceId)).Distinct().Count() >= 2)
            {
                try
                {
                    result.CrossAdapter = CrossAdapter(clip, hardware);
                    Report("cross-adapter", -1, 0.92, "Two cards", result);
                }
                catch (Exception ex) when (ex is GpuException or IOException or InvalidOperationException)
                {
                    // The cards' own results stand; only the comparison is missing.
                    Report("cross-adapter", -1, 0.92, $"The two-card test stopped short: {ex.Message}", result);
                }
            }

            result.Recommended = Recommend(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
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

    // ----- The cards -----

    private static List<HardwareBenchmarkAdapter> EnumerateAdapters()
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
                        Vendor = desc.VendorId switch { 0x1002 => "AMD", 0x8086 => "Intel", 0x10DE => "NVIDIA", 0x5143 or 0x4D4F4351 => "Qualcomm", 0x1414 => "Microsoft", _ => $"0x{desc.VendorId:X4}" },
                    };
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

        // One physical card listed twice: the repeats point at the first listing.
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

    /// <summary>The decoders the card's driver offers (ID3D11VideoDevice::GetVideoDecoderProfile).</summary>
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
            var count = ((delegate* unmanaged<nint, uint>)Com.Table(video)[11])(video);
            for (uint i = 0; i < count; i++)
            {
                Guid profile;
                if (((delegate* unmanaged<nint, uint, Guid*, int>)Com.Table(video)[12])(video, i, &profile) < 0)
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

    /// <summary>The hardware encoders Media Foundation has for each card: MFTEnum2 narrowed to its LUID (MFT_ENUM_ADAPTER_LUID).</summary>
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

        foreach (var adapter in adapters)
        {
            nint attributes;
            if (Mf.MFCreateAttributes(&attributes, 1) < 0)
                continue;
            try
            {
                var luid = adapter.Luid;
                Mf.SetBlob(attributes, MfGuids.EnumAdapterLuid, &luid, sizeof(long));
                foreach (var (subtype, codec) in new[] { (MfGuids.H264, "H.264"), (MfGuids.Hevc, "HEVC"), (MfGuids.Av1, "AV1") })
                {
                    var output = new MftRegisterTypeInfo { MajorType = MfGuids.Video, Subtype = subtype };
                    nint* activates;
                    uint count;
                    if (Mf.MFTEnum2(MfGuids.VideoEncoderCategory, Mf.EnumHardware | Mf.EnumSortAndFilter, null, &output, attributes, &activates, &count) < 0)
                        continue;
                    if (count > 0 && !adapter.EncodeCodecs.Contains(codec))
                        adapter.EncodeCodecs.Add(codec);
                    for (var i = 0; i < count; i++)
                    {
                        var activate = activates[i];
                        Com.Release(ref activate);
                    }

                    Com.CoTaskMemFree((nint)activates);
                }
            }
            catch (GpuException)
            {
                // No encoders known for this card.
            }
            finally
            {
                Com.Release(ref attributes);
            }
        }
    }

    // ----- Decoding -----

    /// <summary>The clip decoded front to back (frames per second), six seeks (median ms), and the frames at the accuracy points.</summary>
    private static (double Fps, double SeekMs, byte[][]? Frames) DecodeRun(GpuDevice gpu, string clip, bool hardware, out int frameCount)
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

    /// <summary>Waits for the card to finish what it was given (an event query).</summary>
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

    /// <summary>The card's frames against the processor's: PSNR over every plane, SSIM over luma, bit-exactness.</summary>
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

    // ----- The bus -----

    /// <summary>A 1080p BGRA frame (8 MB) read back and uploaded, eight times each: MB/s both ways.</summary>
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

    /// <summary>Decoding on the second card, every frame carried to the first over the bus, against decoding on the first.</summary>
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

    // ----- The roles -----

    /// <summary>The four roles the measurements point to, each with its reason.</summary>
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

        if (result.CrossAdapter is { CrossIsFaster: true } cross)
        {
            roles.Decode = cross.DecodeAdapter;
            roles.Reasons.Add($"Decode: card {cross.DecodeAdapter}, whose decoding plus the copy across ({cross.CrossDecodeFps:0} fps) beat the compositor's own ({cross.LocalDecodeFps:0} fps).");
        }
        else
        {
            roles.Decode = compositor.DecodesInHardware ? compositor.Index : -1;
            roles.Reasons.Add(compositor.DecodesInHardware
                ? $"Decode: the compositor's card ({compositor.HardwareDecodeFps:0} fps): frames stay in its memory, nothing crosses the bus."
                : "Decode: Auto (the compositor's card did not decode the test clip in hardware).");
        }

        var second = working.Where(a => (a.VendorId, a.DeviceId) != (compositor.VendorId, compositor.DeviceId) && a.DecodesInHardware)
            .OrderByDescending(a => a.HardwareDecodeFps).FirstOrDefault();
        roles.Background = second?.Index ?? -1;
        roles.Reasons.Add(second is not null
            ? $"Background: {second.Name}, so thumbnails, waveforms, proxies and captions never slow the preview."
            : "Background: Auto (there is no second card that decodes).");

        static int Score(HardwareBenchmarkAdapter a) => (a.EncodeCodecs.Contains("AV1") ? 4 : 0) + (a.EncodeCodecs.Contains("HEVC") ? 2 : 0) + (a.EncodeCodecs.Contains("H.264") ? 1 : 0);
        var encoder = working.Where(a => a.EncodeCodecs.Count > 0).OrderByDescending(Score).ThenBy(a => a == compositor ? 1 : 0).ThenBy(a => a.Index).FirstOrDefault();
        roles.Export = encoder?.Index ?? -1;
        roles.Reasons.Add(encoder is not null
            ? $"Export: {encoder.Name} ({string.Join(", ", encoder.EncodeCodecs)} in hardware){(encoder != compositor ? ", leaving the compositor's card free" : "")}."
            : "Export: Auto (no hardware encoder was listed).");
        return roles;
    }
}
