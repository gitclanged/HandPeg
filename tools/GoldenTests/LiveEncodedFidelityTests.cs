using System.IO;
using System.Text;
using System.Windows;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4.4-Final, the Live Encoded Preview's full export fidelity (pure, any machine): the encoder family each FFmpeg name routes to and the
/// card it is looked for on first (QuickSync on the Intel card's LUID, AMF on the AMD card's, NVENC on the NVIDIA card's, Media Foundation on the
/// compositor's), the ENCODED pill's words, the export's frame size kept to the canvas's shape, a software encoder's one-frame FFmpeg command
/// (the export's own arguments, NV12 in, the raw bitstream out), its arguments packed for the control block, the P010 samples in and out, the
/// Export tab members watched, and the player's right-click (a stationary click, its menu's items). Every line says what it proved.
/// </summary>
public static class LiveEncodedFidelityTests
{
    public static IEnumerable<GraphResult> All()
    {
        var text = new StringBuilder("# Live Encoded Preview, full export fidelity (5.4.4-Final): routing, pill, size, software command, P010, watched settings, player menu.\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            Routing(Expect);
            Pill(Expect);
            Sizes(Expect);
            Software(Expect);
            TenBit(Expect);
            Watched(Expect);
            PlayerMenu(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("live-encoded-fidelity", text.ToString(), problems, 0);
    }

    private static void Routing(Action<bool, string> expect)
    {
        expect(ExportLoopbackSettings.FamilyOf("hevc_qsv") == ExportLoopbackSettings.FamilyQuickSync && ExportLoopbackSettings.FamilyOf("h264_amf") == ExportLoopbackSettings.FamilyAmf
               && ExportLoopbackSettings.FamilyOf("av1_nvenc") == ExportLoopbackSettings.FamilyNvenc && ExportLoopbackSettings.FamilyOf("hevc_mf") == ExportLoopbackSettings.FamilyMediaFoundation
               && ExportLoopbackSettings.FamilyOf("libx264") == ExportLoopbackSettings.FamilySoftware && ExportLoopbackSettings.FamilyOf("libvpx-vp9") == ExportLoopbackSettings.FamilySoftware,
            "FamilyOf: *_qsv QuickSync, *_amf AMF, *_nvenc NVENC, *_mf Media Foundation, lib* software");
        expect(ExportLoopbackSettings.VendorOf(ExportLoopbackSettings.FamilyQuickSync) == 0x8086 && ExportLoopbackSettings.VendorOf(ExportLoopbackSettings.FamilyAmf) == 0x1002
               && ExportLoopbackSettings.VendorOf(ExportLoopbackSettings.FamilyNvenc) == 0x10DE && ExportLoopbackSettings.VendorOf(ExportLoopbackSettings.FamilyMediaFoundation) == 0,
            "VendorOf: QuickSync on the Intel card, AMF on the AMD card, NVENC on the NVIDIA card, Media Foundation on none (the compositor's)");

        // The Desktop: an RX 7900 XTX (the compositor) and an Arc A310.
        GpuAdapter[] desktop = [new(0, "AMD Radeon RX 7900 XTX") { VendorId = 0x1002, Luid = 0x1111 }, new(1, "Intel(R) Arc(TM) A310") { VendorId = 0x8086, Luid = 0x2222 }];
        var qsv = MfEncoderMft.SearchOrder(ExportLoopbackSettings.FamilyQuickSync, 0x1111, "AMD Radeon RX 7900 XTX", desktop);
        expect(qsv is [{ Luid: 0x2222, Asked: true }, { Luid: 0x1111, Asked: false }, { Luid: 0, Asked: false }],
            "hevc_qsv on the Desktop: the Arc's LUID first (the card asked for), then the compositor's card, then any card (stand-ins)");
        var amf = MfEncoderMft.SearchOrder(ExportLoopbackSettings.FamilyAmf, 0x1111, "AMD Radeon RX 7900 XTX", desktop);
        expect(amf is [{ Luid: 0x1111, Asked: true }, { Luid: 0, Asked: false }], "hevc_amf there: the AMD card (the compositor's too) once, then any card");
        var auto = MfEncoderMft.SearchOrder(ExportLoopbackSettings.FamilyMediaFoundation, 0x1111, "AMD Radeon RX 7900 XTX", desktop);
        expect(auto is [{ Luid: 0x1111, Asked: true }, { Luid: 0, Asked: false }], "Media Foundation / Auto: the compositor's card first (asked for), then any card");
        var nvenc = MfEncoderMft.SearchOrder(ExportLoopbackSettings.FamilyNvenc, 0x1111, "AMD Radeon RX 7900 XTX", desktop);
        expect(nvenc is [{ Luid: 0x1111, Asked: false }, { Luid: 0, Asked: false }], "NVENC with no NVIDIA card: the compositor's card and any card, both stand-ins");
        expect(GpuAdapters.OfVendor(0) is null, "GpuAdapters.OfVendor(0): no card for a family that names no vendor");
    }

    private static void Pill(Action<bool, string> expect)
    {
        var qsv = ExportLoopbackSettings.From("hevc_qsv", false, false, 12000, 22, "veryslow", 60, out _)!;
        var cbr = ExportLoopbackSettings.From("h264_amf", false, true, 6500, 22, "quality", 60, out _)!;
        var x264 = ExportLoopbackSettings.From("libx264", true, false, 8000, 22, "veryslow", 60, out _)!;
        var svt = ExportLoopbackSettings.From("libsvtav1", true, false, 8000, 30, "6", 60, out _)!;
        var nvencCq = ExportLoopbackSettings.From("av1_nvenc", true, false, 8000, 28, "p7", 60, out _)!;
        expect(qsv.PillText == "ENCODED · HEVC QSV · 12 Mbps" && cbr.PillText == "ENCODED · H.264 AMF · CBR 6.5 Mbps",
            $"the pill names the encoder and its rate: \"{qsv.PillText}\", \"{cbr.PillText}\"");
        expect(x264.PillText == "ENCODED · x264 veryslow · CRF 22" && svt.ShortName == "SVT-AV1 6" && nvencCq.RateShort == "CQ 28",
            $"a software encoder by its library and preset (\"{x264.PillText}\", \"{svt.ShortName}\"); a hardware CQ as CQ ({nvencCq.RateShort})");
        var running = new ExportLoopbackStatus(ExportLoopbackState.Running, "Intel Quick Sync Video H.265 Encoder MFT", 6, 10, 0, 0);
        expect(MainWindow.EncodedPillText(qsv, running, playing: false) == qsv.PillText
               && MainWindow.EncodedPillText(qsv, running with { OnAskedCard = false }, false) == "ENCODED · HEVC · STAND-IN · 12 Mbps"
               && MainWindow.EncodedPillText(x264, running with { PausedOnly = true }, playing: true) == "RAW WHILE PLAYING · x264 veryslow"
               && MainWindow.EncodedPillText(x264, running with { PausedOnly = true }, playing: false) == x264.PillText,
            "EncodedPillText: running on the card asked for, the settings' pill; a stand-in says so; a software encoder while playing says the picture is the plain one");
        expect(ExportLoopbackSettings.BitrateText(800) == "800 kbps" && ExportLoopbackSettings.BitrateText(35000) == "35 Mbps", "bitrates in words: 800 kbps, 35 Mbps");
    }

    private static void Sizes(Action<bool, string> expect)
    {
        expect(ExportLoopbackSettings.EncodedSizeOf(1920, 1080, 0, 0) == (1920, 1080) && ExportLoopbackSettings.EncodedSizeOf(1920, 1080, 1920, 1080) == (1920, 1080),
            "no export size, or the canvas's: encoded at the canvas's size");
        expect(ExportLoopbackSettings.EncodedSizeOf(1920, 1080, 1280, 720) == (1280, 720) && ExportLoopbackSettings.EncodedSizeOf(2560, 1440, 1280, 720) == (1280, 720),
            "a 720p export of a 1080p or 1440p Sequence: encoded at 1280 x 720");
        expect(ExportLoopbackSettings.EncodedSizeOf(1920, 1080, 720, 1280) == (1280, 720),
            "a turned export (720 x 1280) of a landscape canvas: its short side (720) taken, the canvas's shape kept");
        expect(ExportLoopbackSettings.EncodedSizeOf(1921, 1081, 1280, 720) is var (w, h) && w % 2 == 0 && h % 2 == 0 && ExportLoopbackSettings.EncodedSizeOf(100, 100, 10, 10) == (64, 64),
            "sides even, at least 64");
    }

    private static void Software(Action<bool, string> expect)
    {
        var arguments = FfmpegLoopbackEncoder.SplitArguments("-c:v libx264 -preset veryslow -tune film -crf 22 -x264-params \"keyint=60 bframes=0\"");
        expect(arguments is ["-c:v", "libx264", "-preset", "veryslow", "-tune", "film", "-crf", "22", "-x264-params", "keyint=60 bframes=0"]
               && FfmpegLoopbackEncoder.EncoderOf(arguments) == "libx264",
            "the export's arguments split as a command line (a quoted value kept whole); the encoder named after -c:v");
        var command = FfmpegLoopbackEncoder.CommandFor(arguments, "h264", 1280, 720, 59.94);
        var line = string.Join(' ', command);
        expect(line.StartsWith("-hide_banner -nostats -loglevel error -f rawvideo -pix_fmt nv12 -video_size 1280x720 -framerate 59.94 -i pipe:0 -frames:v 1 -an -sn -dn -c:v libx264 -preset veryslow", StringComparison.Ordinal)
               && line.EndsWith("-f h264 pipe:1", StringComparison.Ordinal) && !command.Any(a => a is "-vf" or "-af" or "-filter_complex"),
            $"one frame: NV12 of the encoded size in, the export's own arguments, Annex B out; no filter named (Law 4): {line}");
        expect(FfmpegLoopbackEncoder.MuxerOf(MfGuids.H264) == "h264" && FfmpegLoopbackEncoder.MuxerOf(MfGuids.Hevc) == "hevc"
               && FfmpegLoopbackEncoder.MuxerOf(MfGuids.Av1) == "obu" && FfmpegLoopbackEncoder.MuxerOf(MfGuids.Vp9) == "ivf",
            "raw bitstreams: h264 and hevc (Annex B), AV1 as OBUs, VP9 in IVF");

        // An IVF file of one frame: its 32-byte header, then the frame's 12 (its size, its time) and the frame.
        var ivf = new byte[32 + 12 + 5];
        "DKIF"u8.CopyTo(ivf);
        BitConverter.GetBytes((ushort)32).CopyTo(ivf, 6);
        BitConverter.GetBytes(5u).CopyTo(ivf, 32);
        new byte[] { 9, 8, 7, 6, 5 }.CopyTo(ivf, 44);
        expect(FfmpegLoopbackEncoder.FirstIvfFrame(ivf) is [9, 8, 7, 6, 5] && FfmpegLoopbackEncoder.FirstIvfFrame([1, 2, 3]) is null,
            "VP9's frame taken out of its IVF file; anything else is no frame");

        var packed = GpuPreviewEngine.PackArguments(string.Join(' ', Enumerable.Repeat("-x264-params a=1", 30)));
        expect(packed.Length <= 255 && Encoding.UTF8.GetString(packed).EndsWith("a=1", StringComparison.Ordinal)
               && GpuPreviewEngine.PackArguments(" -c:v libx265 ").SequenceEqual("-c:v libx265"u8.ToArray()),
            $"the arguments packed for the control block: at most 255 bytes, cut between two arguments ({packed.Length} bytes), trimmed");
    }

    private static unsafe void TenBit(Action<bool, string> expect)
    {
        byte[] eight = [0, 16, 128, 235, 255];
        var wide = new byte[eight.Length * 2];
        fixed (byte* from = eight)
        fixed (byte* to = wide)
            ExportLoopbackPreview.CopyRow(from, to, eight.Length, p010: true);
        var samples = Enumerable.Range(0, eight.Length).Select(i => BitConverter.ToUInt16(wide, i * 2)).ToArray();
        expect(samples is [0, 0x1000, 0x8000, 0xEB00, 0xFF00], "into a 10-bit encoder: each 8-bit value at the top of its 16 bits (16 -> 0x1000, 235 -> 0xEB00)");

        ushort[] p010 = [0x1000, 0xEB00, 0xEB7F, 0xEB80, 0xFFC0];
        var back = new byte[p010.Length];
        fixed (ushort* from = p010)
        fixed (byte* to = back)
            MfDecoderMft.TopBytes(from, to, p010.Length);
        expect(back is [16, 235, 235, 236, 255], "out of a 10-bit decoder: each sample's top byte, rounded (0xEB80 -> 236), 0xFFC0 held at 255");

        var settings = ExportLoopbackSettings.From("hevc_nvenc", false, false, 12000, 22, "p7", 60, out _)! with { TenBit = true };
        expect(settings.TenBit && settings != settings with { TenBit = false }, "10-bit is part of the settings: turning it on or off sends new ones (and makes the encoder anew)");
    }

    private static void Watched(Action<bool, string> expect)
    {
        string[] asked = ["VideoEncoder", "EncoderPreset", "RateControl", "TargetBitrate", "TargetFileSize", "Crf", "TenBit", "ResolutionPreset", "OutputWidth", "OutputHeight",
            "UseVerticalResolution", "SelectedPreset", "EffectiveBitrateKbps", "FrameWidth", "FrameHeight"];
        var missing = asked.Where(n => !MainViewModel.LoopbackSettingNames.Contains(n)).ToList();
        expect(missing.Count == 0, $"every Export tab member the user listed sends the preview new settings ({(missing.Count == 0 ? "all watched" : "missing: " + string.Join(", ", missing))})");
        var source = File.ReadAllText(Path.Combine(Src, "MainWindow.LoopbackPreview.cs"));
        expect(source.Contains("private const int LoopbackSettingsGatherMs = 25;", StringComparison.Ordinal) && source.Contains("GatherLoopbackSettings();", StringComparison.Ordinal),
            "a run of changes is gathered for 25 ms, then sent once (PushLoopbackSettings)");
        var engine = File.ReadAllText(Path.Combine(Src, "Services", "GpuPreviewEngine.Loopback.cs"));
        expect(engine.Contains("if (was is null || settings is null)", StringComparison.Ordinal) && engine.Contains("Interlocked.Increment(ref _loopbackSettingsStamp);", StringComparison.Ordinal),
            "new settings while on keep the content stamp (no scene drawn again) and raise the settings' own stamp");
        var worker = File.ReadAllText(Path.Combine(Src, "Compositor", "ExportLoopbackPreview.cs"));
        expect(worker.Contains("var redraw = made || _playing || header.LoopbackContentStamp != _renderedStamp", StringComparison.Ordinal)
               && worker.Contains("got |= _decoder.DrainHeld();", StringComparison.Ordinal) && worker.Contains("private const int DrainAfterMs = 0;", StringComparison.Ordinal),
            "the worker draws the scene again only when it may have changed, drains the decoder on a paused frame held back, and the encoder at once");
    }

    private static void PlayerMenu(Action<bool, string> expect)
    {
        expect(MainWindow.IsStationaryClick(new Point(10, 10), new Point(13, 12), 4, 4) && !MainWindow.IsStationaryClick(new Point(10, 10), new Point(15, 10), 4, 4),
            "a right-click within the drag distance opens the player's menu; one that moved does not");
        expect(MainWindow.PlayerMenuItems is ["Export Frame as Image...", "Copy Frame to Clipboard", "Ping-to-Mark Highlight Here", null,
                "Full Screen Preview", "3D Exploded View", "Live Encoded Preview", "Safe Margins", "Reset Player Zoom & Pan"],
            "its items, in the user's order, a separator after Ping-to-Mark");
    }

    // The repository's src folder, found upwards from the built program.
    private static string Src
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "HandPegApp.csproj")))
                    return Path.Combine(dir.FullName, "src");
            }

            throw new DirectoryNotFoundException("the repository (HandPegApp.csproj) above " + AppContext.BaseDirectory);
        }
    }
}
