using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4B, the Export tab: export-presets-unified (one catalog of built-in Export Presets in two groups, then the user's own; a pick
/// applies its export settings only, while a project's settings and a Smart Rule's preset are still applied whole; the built-ins of 5.4A
/// leave presets.json while they are untouched, their old names renamed in Smart Rules; saving and a file round trip keep the format);
/// export-defaults (VBR at an automatic bitrate until one is typed, the best encoder step, veryslow for x264 and x265, Web Optimized on,
/// FLAC and the -14 / -16 LUFS target); export-hdr-passthrough (the rule, the state, and the encode's 10-bit BT.2020 tail with every SDR
/// command as it was); encoder-probe-vendor-filter (a probe problem of a vendor whose card is not here is not said, and never by listing
/// the cards before they are listed); export-tab-columns (MainWindow.xaml: three inspector cards holding every export control once, the
/// Video tab, the Easy cards, the dead -hwaccel switch and the crop cross gone, Interactive Crop on a clip's menu). Pass 5.4C: the catalog
/// held to its own names (an allow-list, never a list of anything else), every built-in at the slowest step with a hardware encoder first
/// (Master Quality, Fast Proxy, Small File) and the probe race; Media Foundation's -compression_level 100; the Export GPU; the Squisher's
/// Discord (10 MB); export-audio-only (FLAC, WAV and MP3 from the sound engine, no frame piped, no compositor). Every line says what it proved.
/// </summary>
public static class ExportPresetsTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Vbr = "Average Bitrate (ABR)", Cq = "Constant Quality (CQ)", Match = "Match Project Settings";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("export-presets-unified", "# One Export Presets system: the built-ins in two groups, then My Presets; a pick sets the export only; old built-ins migrate.", Unified);
        yield return Case("export-defaults", "# The export starts at VBR with an automatic bitrate, the encoder's best step (veryslow for x264 and x265), Web Optimized on.", Defaults);
        yield return Case("export-hdr-passthrough", "# An HDR project stays HDR with 10-bit HEVC or AV1 and HDR pictures only: P010 piped, tagged BT.2020; SDR commands unchanged.", Hdr);
        yield return Case("encoder-probe-vendor-filter", "# A hardware encoder's problem is said only for a vendor whose card is here, and the cards are never listed for it before the first frame.", VendorFilter);
        yield return Case("export-tab-columns", "# The Export tab: three inspector cards, every export control once; the Video tab, the Easy cards, -hwaccel and the crop cross are gone.", Columns);
        yield return Case("export-audio-only", "# Audio Only (5.4C): FLAC, WAV and MP3 of the Sequence's sound alone, rendered by the engine, one track, no frame piped, no compositor.", AudioOnly);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
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
            run(Expect, text);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    private static MainViewModel Empty()
    {
        var vm = new MainViewModel();
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [new TimelineDocument()]);
        return vm;
    }

    private static SequenceExportState ExportState(MainViewModel vm) =>
        (SequenceExportState)typeof(MainViewModel).GetMethod("CompileExportState", Hidden)!.Invoke(vm, null)!;

    private static EncodingPreset BuiltIn(string name) => ExportPresetCatalog.BuiltIns.First(p => p.Name == name);

    private static void Choose(MainViewModel vm, string name)
    {
        var option = vm.VideoEncoders.FirstOrDefault(e => e.Name == name);
        if (option is null)
            vm.VideoEncoders.Add(option = EncoderOption.FromHardwareName(name, qualcommAdreno: false));
        vm.VideoEncoder = option;
    }

    // ----- export-presets-unified -----

    private static void Unified(Action<bool, string> expect, StringBuilder text)
    {
        var builtIns = ExportPresetCatalog.BuiltIns;
        foreach (var group in builtIns.GroupBy(p => p.Group))
            text.Append("   ").Append(group.Key).Append(": ").Append(string.Join(" | ", group.Select(p => p.Name))).Append('\n');
        expect(builtIns.Select(p => p.Group).Distinct().SequenceEqual([EncodingPreset.PlatformCategory, EncodingPreset.StudioCategory])
               && EncodingPreset.PlatformCategory == "Platform & Social" && EncodingPreset.StudioCategory == "Studio & Archive" && EncodingPreset.UserCategory == "My Presets",
            "the built-ins come in two groups, Platform & Social then Studio & Archive; a user's preset is My Presets");
        string[] asked =
        [
            "TikTok (PC Web)", "TikTok (iOS)", "TikTok (Android)", "YouTube Shorts", "YouTube 1080p60", "YouTube 1440p60 (VP9 Trick)", "YouTube 4K60",
            "Instagram Reels", "Discord (10 MB)", "Discord (50 MB)", "Discord (500 MB)", "X / Twitter", "Reddit",
            ExportPresetCatalog.MasterQuality + " (H.264)", ExportPresetCatalog.MasterQuality + " (HEVC)", ExportPresetCatalog.MasterQuality + " (AV1)",
            "Lossless Stream Copy", "Animated GIF", "Animated WebP", "Audio Only (FLAC)", "Audio Only (WAV)", "Podcast / Voice (MP3, -16 LUFS)",
        ];
        expect(asked.All(n => builtIns.Any(p => p.Name == n)), "every preset the roadmap names is there (the three Audio Only ones since 5.4C, D-Q5)");
        expect(builtIns.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == builtIns.Count && builtIns.All(p => p.IsBuiltIn && p.Detail is { Length: > 0 }),
            "each built-in has its own name, a group and its one line for the selector");

        // 5.4C: the catalog holds its own names and no other (an allow-list of what it is meant to hold, never a list of what it must not).
        string[] allowed =
        [
            .. asked, "Mezzanine Archive (ProRes 422 HQ)", "Apple ProRes 422 Proxy", "DNxHR LB (Low Bandwidth)", "H.264 ALL-I Intra-Frame", "Fast Proxy", "Small File 720p",
        ];
        expect(builtIns.Count == allowed.Length && builtIns.All(p => allowed.Contains(p.Name, StringComparer.Ordinal)),
            $"the {builtIns.Count} built-ins are exactly the catalog's own names, each on the list this test holds (nothing else gets in)");
        expect(builtIns.Where(p => p.Container is "flac" or "wav" or "mp3").Select(p => (p.Name, p.Group, p.Container, p.AudioEncoder)).SequenceEqual(
                   [("Audio Only (FLAC)", EncodingPreset.StudioCategory, "flac", "flac"), ("Audio Only (WAV)", EncodingPreset.StudioCategory, "wav", "pcm_s16le"),
                    ("Podcast / Voice (MP3, -16 LUFS)", EncodingPreset.StudioCategory, "mp3", "libmp3lame")])
               && BuiltIn("Podcast / Voice (MP3, -16 LUFS)") is { AudioBitrate: "128k", NormalizeAudio: true, LoudnessTarget: -16, MergeAudioTracks: true }
               && BuiltIn("Audio Only (FLAC)").MergeAudioTracks && BuiltIn("Audio Only (WAV)").MergeAudioTracks,
            "the Audio Only presets are in Studio & Archive: FLAC (flac), WAV (pcm_s16le), and the podcast's MP3 at 128k, mixed into one and normalized to -16 LUFS");
        var slowest = builtIns.Where(p => p.EncoderPreset.Length > 0).Select(p => p.Name).ToList();
        expect(slowest.Count == 0, $"no built-in names a speed step: each encoder starts at its slowest, best one{(slowest.Count > 0 ? $" (named: {string.Join(", ", slowest)})" : "")}");
        expect(BuiltIn("Fast Proxy") is { PreferredCodec: "h264", VideoEncoder: "libx264", EncoderPreset: "", Quality: 28, SpeedFirst: true }
               && BuiltIn("Small File 720p") is { PreferredCodec: "hevc", VideoEncoder: "libx265", EncoderPreset: "", Quality: 26, SpeedFirst: false }
               && BuiltIn("H.264 ALL-I Intra-Frame") is { PreferredCodec: null, VideoEncoder: "libx264", Tune: "fastdecode", EncoderPreset: "" }
               && builtIns.Count(p => p.SpeedFirst) == 1,
            "Fast Proxy (H.264) and Small File 720p (HEVC) look for a hardware encoder first, Small File at the slowest step and Fast Proxy (5.4D) at the fastest, the only speed-first built-in (a flag, not a step); the ALL-I intermediate stays on x264 (its fastdecode tune is x264's)");
        expect(BuiltIn("TikTok (PC Web)") is { PreferredCodec: "h264", TargetBitrate: "16000", FramerateCap: 60, AudioBitrate: "192k" }
               && BuiltIn("TikTok (iOS)") is { PreferredCodec: "hevc,h264", TargetBitrate: "12000", Framerate: "60", AudioBitrate: "192k" }
               && BuiltIn("TikTok (Android)") is { PreferredCodec: "h264", TargetBitrate: "10000", Framerate: "60", AudioBitrate: "128k" },
            "TikTok by upload client (D-Q3): PC Web H.264 16 Mbps at the project's rate (60 at most), iOS HEVC on hardware else H.264 12 Mbps 60, Android H.264 10 Mbps 60");
        expect(builtIns.Where(p => p.Container is "mp4").All(p => p.WebOptimized == true), "every built-in written as .mp4 carries Web Optimized");
        expect(BuiltIn(ExportPresetCatalog.MasterQuality + " (HEVC)") is { TenBit: true, VideoEncoder: "libx265", EncoderPreset: "", PreferredCodec: "hevc" }
               && BuiltIn(ExportPresetCatalog.MasterQuality + " (AV1)") is { TenBit: true, VideoEncoder: "libsvtav1", PreferredCodec: "av1" }
               && BuiltIn(ExportPresetCatalog.MasterQuality + " (H.264)") is { VideoEncoder: "libx264", PreferredCodec: "h264", EncoderPreset: "" },
            "Master Quality H.264, HEVC and AV1 want their codec on a hardware encoder first (5.4C), the software one kept as the fallback; HEVC and AV1 are 10-bit (an HDR project then stays HDR), at the encoder's best step");
        expect(ExportPresetCatalog.RenamedBuiltIns.Values.All(n => builtIns.Any(p => p.Name == n))
               && ExportPresetCatalog.RenamedBuiltIns["Remux / Cut Only"] == "Lossless Stream Copy" && ExportPresetCatalog.RenamedBuiltIns["TikTok (Vertical)"] == "TikTok (PC Web)",
            "each old built-in name is renamed to a built-in that exists (Remux / Cut Only -> Lossless Stream Copy, TikTok (Vertical) -> TikTok (PC Web)...)");

        // The JSON: a built-in says its group, a user's preset (no group) is written as before; nothing of the dead -hwaccel switch is written.
        var user = new EncodingPreset { Name = "Mine" };
        var userJson = JsonSerializer.Serialize(new List<EncodingPreset> { user }, AppSettingsJsonContext.Default.ListEncodingPreset);
        expect(!userJson.Contains("\"Category\"") && !userJson.Contains("\"WebOptimized\"") && !userJson.Contains("\"PreferredCodec\"") && !userJson.Contains("HardwareDecoding")
               && !userJson.Contains("\"LoudnessTarget\""), "a user's preset is written without the new members while they are unset, and without HardwareDecoding");
        var read = JsonSerializer.Deserialize("[{\"Name\":\"Old\",\"HardwareDecoding\":false,\"RateControl\":\"Constant Quality (CQ)\",\"TargetBitrate\":\"5000\"}]", AppSettingsJsonContext.Default.ListEncodingPreset)!;
        expect(read[0] is { Name: "Old", RateControl: Cq, TargetBitrate: "5000", Category: null, WebOptimized: null }, "an old presets.json (with HardwareDecoding) reads as before");

        // Migration: the untouched old built-ins leave presets.json, a changed one and the user's stay, a name a built-in has now is renamed.
        var stored = PresetStore.LegacyBuiltIns();
        stored[1].Quality = 15;
        var changedName = stored[1].Name;
        stored.Add(new EncodingPreset { Name = "Reddit", VideoEncoder = "libx265" });
        stored.Add(new EncodingPreset { Name = "My Own", Category = EncodingPreset.PlatformCategory });
        var changed = PresetStore.Migrate(stored);
        text.Append("   presets.json after the migration: ").Append(string.Join(" | ", stored.Select(p => p.Name))).Append('\n');
        expect(changed && stored.Count == 3 && stored[0].Name == changedName && stored[1].Name == "Reddit (Custom)" && stored[2] is { Name: "My Own", Category: null },
            "the 11 untouched old built-ins go, the changed YouTube 4K stays as the user's, a user's \"Reddit\" becomes \"Reddit (Custom)\", nothing stored is a built-in");
        expect(!PresetStore.Migrate(stored), "and a second read changes nothing");
        var legacyFile = JsonSerializer.Deserialize("[{\"Name\":\"Discord (8MB)\",\"OutputHeight\":\"720\",\"VideoEncoder\":\"libx264\",\"EncoderPreset\":\"slow\",\"RateControl\":\"Average Bitrate (ABR)\","
            + "\"TargetBitrate\":\"5000\",\"TargetFileSize\":\"7.8\",\"ExtraVideoArguments\":\"-pix_fmt yuv420p\",\"AudioEncoder\":\"aac\",\"AudioBitrate\":\"96k\",\"Container\":\"mp4\",\"Framerate\":\"30\"}]",
            AppSettingsJsonContext.Default.ListEncodingPreset)!;
        expect(PresetStore.Migrate(legacyFile) && legacyFile.Count == 0, "an old built-in as a 5.3 presets.json holds it (HardwareDecoding and all) is taken out");
        var tuned = PresetStore.LegacyBuiltIns();
        tuned.Single(p => p.Name == "High Quality H.264").NormalizeAudio = true;
        tuned.Single(p => p.Name == "Fast Proxy").Quality = 24;
        var migrated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        PresetStore.Migrate(tuned, migrated);
        expect(tuned.Select(p => p.Name).SequenceEqual(["Fast Proxy (Custom)", "High Quality H.264"]) && migrated.Count == 1 && migrated["Fast Proxy"] == "Fast Proxy (Custom)",
            "an old built-in saved over with the user's own sound (Normalize Audio, export fields as before) stays theirs; a changed Fast Proxy becomes \"Fast Proxy (Custom)\"");
        var follows = new AppSettings
        {
            DefaultPreset = "Fast Proxy",
            SmartRules = [new SmartRule { Type = SmartRule.Keyword, Path = "hq", Preset = "High Quality H.264" }],
        };
        follows.RenamePresetReferences(PresetStore.ReferenceRenames(migrated), name => tuned.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        expect(follows.DefaultPreset == "Fast Proxy (Custom)" && follows.SmartRules[0].Preset == "High Quality H.264",
            "a reference to a preset renamed \"(Custom)\" follows it, never landing on the built-in Fast Proxy; one to the kept High Quality H.264 stays");
        var settings = new AppSettings
        {
            DefaultPreset = "Remux / Cut Only",
            SmartRules = [new SmartRule { Type = SmartRule.Keyword, Path = "tiktok", Preset = "TikTok (Vertical)" }, new SmartRule { Type = SmartRule.Keyword, Path = "yt", Preset = "YouTube 4K" }],
        };
        settings.RenamePresetReferences(ExportPresetCatalog.RenamedBuiltIns, name => name == "YouTube 4K");
        expect(settings.DefaultPreset == "Lossless Stream Copy" && settings.SmartRules[0].Preset == "TikTok (PC Web)" && settings.SmartRules[1].Preset == "YouTube 4K",
            "a Smart Rule and the default preset naming an old built-in take its new name, unless the user still has a preset of the old name");
        expect(PresetStore.UniqueName("YouTube 4K60", [new EncodingPreset { Name = "YouTube 4K60 (Custom)" }]) == "YouTube 4K60 (Custom 2)"
               && PresetStore.UniqueName("Mine", []) == "Mine", "a built-in's name is never a user's: \"(Custom)\", then \"(Custom 2)\"");

        // A pick applies the export only.
        var vm = Empty();
        expect(vm.Presets.Take(builtIns.Count).SequenceEqual(builtIns) && vm.PresetsView.Groups?.Count >= 2,
            "the view model's list starts with the built-ins, and the selector's view groups it");
        vm.Deinterlace = true;
        vm.LutPath = "D:/looks/teal.cube";
        vm.FadeIn = true;
        vm.SelectedPreset = BuiltIn("YouTube 1080p60");
        expect(vm.Deinterlace && vm.LutPath == "D:/looks/teal.cube" && vm.FadeIn, "picking YouTube 1080p60 leaves the filters, the look and the fades as they were (export fields only, D-Q2)");
        expect(vm.VideoEncoder.Name == "libx264" && vm.EncoderPreset == "veryslow" && vm.RateControl == Vbr && vm.TargetBitrate == "" && vm.Container == "mp4" && vm.WebOptimized
               && (vm.OutputWidth, vm.OutputHeight) == ("", "1080") && vm.Framerate == Match && vm.AudioBitrate == "256k",
            "and sets its export: x264 (no hardware H.264 here) at veryslow, VBR at the automatic bitrate, mp4, Web Optimized, 1080p on the short side, the project's rate, AAC 256k");
        vm.Document.FrameRate = FrameRate.Ntsc11988;
        vm.SelectedPreset = BuiltIn("TikTok (PC Web)");
        expect(vm.UseVerticalResolution && (vm.FrameWidth, vm.FrameHeight) == (1080, 1920) && vm.TargetBitrate == "16000" && vm.Framerate == "60",
            "TikTok (PC Web) on a 16:9 Sequence at 119.88 fps: turned 9:16 at 1080 x 1920, 16 Mbps, the rate capped at 60");
        vm.SelectedPreset = BuiltIn("Discord (10 MB)");
        expect(vm.TargetFileSize == "9.4" && vm.Framerate == "30" && vm.AudioBitrate == "96k" && vm.RateControl == Vbr && Math.Min(vm.FrameWidth, vm.FrameHeight) <= 720,
            "Discord (10 MB): aimed under 9.4 MB, 30 fps, AAC 96k, the short side at most 720");
        vm.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (H.264)");
        expect(vm.TargetFileSize == "" && vm.RateControl == Cq && vm.Crf == 18 && (vm.OutputWidth, vm.OutputHeight) == ("", "") && vm.Framerate == Match,
            "Master Quality (H.264): the project's size and rate, constant quality 18, the chat's target size let go");
        vm.SelectedPreset = BuiltIn("TikTok (PC Web)");
        vm.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (H.264)");
        var afterMaster = (vm.UseVerticalResolution, vm.FrameWidth, vm.FrameHeight);
        vm.SelectedPreset = BuiltIn("TikTok (PC Web)");
        vm.SelectedPreset = BuiltIn("YouTube 1080p60");
        expect(afterMaster == (false, 1920, 1080) && !vm.UseVerticalResolution && (vm.FrameWidth, vm.FrameHeight) == (1920, 1080),
            "on a 16:9 Sequence, Master Quality or YouTube 1080p60 picked after TikTok is 1920 x 1080: the turn TikTok left is not kept");

        // 5.4D: a vertical project (turned by hand) stays vertical under a preset that names no shape: Master Quality used to lay it on its side.
        var vertical = Empty();
        vertical.UseVerticalResolution = true;
        vertical.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (H.264)");
        var masterOnVertical = (vertical.UseVerticalResolution, vertical.FrameWidth, vertical.FrameHeight);
        vertical.SelectedPreset = BuiltIn("YouTube 1080p60");
        vertical.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (HEVC)");
        expect(masterOnVertical == (true, 1080, 1920) && vertical.UseVerticalResolution && (vertical.FrameWidth, vertical.FrameHeight) == (1080, 1920),
            "a vertical project keeps its 9:16 frame under Master Quality (H.264), then YouTube 1080p60 and Master Quality (HEVC): a preset naming no shape never turns it");

        // 5.4C: Master Quality on this PC's hardware first, at its slowest step; software (veryslow) only for a codec no hardware encoder here has.
        var hardware = Empty();
        foreach (var name in new[] { "h264_nvenc", "hevc_amf" })
            hardware.VideoEncoders.Add(EncoderOption.FromHardwareName(name, qualcommAdreno: false));
        hardware.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (H.264)");
        var nvencArgs = ExportState(hardware).VideoCodecArguments;
        expect(hardware.VideoEncoder.Name == "h264_nvenc" && hardware.EncoderPreset == "p7" && nvencArgs.Contains("-preset p7", StringComparison.Ordinal) && nvencArgs.Contains("-cq 18", StringComparison.Ordinal),
            "Master Quality (H.264) with NVENC here: h264_nvenc at p7 (-preset p7), constant quality 18 as its CQ");
        hardware.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (HEVC)");
        var amfArgs = ExportState(hardware).VideoCodecArguments;
        expect(hardware.VideoEncoder.Name == "hevc_amf" && hardware.EncoderPreset == "high_quality" && amfArgs.Contains("-quality high_quality", StringComparison.Ordinal),
            "Master Quality (HEVC) with AMF HEVC here: hevc_amf at high_quality (-quality high_quality)");
        hardware.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (AV1)");
        expect(hardware.VideoEncoder.Name == "libsvtav1", "Master Quality (AV1) with no AV1 hardware encoder here: SVT-AV1, the software fallback");
        hardware.SelectedPreset = BuiltIn("Small File 720p");
        expect(hardware.VideoEncoder.Name == "hevc_amf" && hardware.EncoderPreset == "high_quality", "Small File 720p takes the HEVC hardware encoder too, at its slowest step");
        var arm = Empty();
        arm.VideoEncoders.Add(EncoderOption.FromHardwareName("h264_mf", qualcommAdreno: true));
        arm.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (H.264)");
        var mfArgs = ExportState(arm).VideoCodecArguments;
        expect(arm.VideoEncoder.Name == "h264_mf" && mfArgs.Contains(MediaFoundationEncoder.SlowestStepArgument, StringComparison.Ordinal)
               && Regex.Matches(mfArgs, @"(?<!\S)-quality\s").Count == 1 && !mfArgs.Contains("-preset", StringComparison.Ordinal),
            $"on a Snapdragon (Media Foundation only): h264_mf at -compression_level 100, its one -quality the constant-quality level ({mfArgs})");

        // The probe race (5.4C): a built-in applied before the hardware probe listed anything falls back to software, and is resolved
        // again once, when the list comes, unless an encoder was picked by hand in between.
        var apply = typeof(MainViewModel).GetMethod("ApplyHardwareEncoders", Hidden)!;
        var early = Empty();
        early.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (HEVC)");
        var before = early.VideoEncoder.Name;
        apply.Invoke(early, [new List<string> { "hevc_nvenc", "h264_nvenc" }, new List<string>()]);
        expect(before == "libx265" && early.VideoEncoder.Name == "hevc_nvenc" && early.EncoderPreset == "p7",
            "Master Quality (HEVC) picked before the probe answered: x265 at first, then hevc_nvenc at p7 when the probe lists it");
        apply.Invoke(early, [new List<string> { "hevc_nvenc", "h264_nvenc" }, new List<string>()]);
        expect(early.VideoEncoder.Name == "hevc_nvenc", "a second probe keeps it (the wish is used once)");
        var picked = Empty();
        picked.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (HEVC)");
        Choose(picked, "libsvtav1");
        Choose(picked, "libx265");
        apply.Invoke(picked, [new List<string> { "hevc_nvenc" }, new List<string>()]);
        expect(picked.VideoEncoder.Name == "libx265", "an encoder picked by hand in between stands: the probe does not take it over");
        var late = Empty();
        apply.Invoke(late, [new List<string> { "h264_qsv" }, new List<string>()]);
        late.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (HEVC)");
        apply.Invoke(late, [new List<string> { "h264_qsv", "hevc_qsv" }, new List<string>()]);
        expect(late.VideoEncoder.Name == "libx265", "a preset applied after the probe answered is not resolved again by a later probe (only the race is)");

        // A project's settings, and a Smart Rule's preset, are still applied whole (ApplyPreset keeps its name, signature and behaviour).
        var applyPreset = typeof(MainViewModel).GetMethod("ApplyPreset", Hidden)!;
        expect(applyPreset.GetParameters() is [{ ParameterType: var p0 }, { ParameterType: var p1, Name: "withLayout" }] && p0 == typeof(EncodingPreset) && p1 == typeof(bool),
            "ApplyPreset(EncodingPreset, bool withLayout) is still there for a project's restore and a Smart Rule");
        applyPreset.Invoke(vm, [new EncodingPreset { Deinterlace = false, LutPath = "", FadeIn = false, Container = "mkv", WebOptimized = false }, false]);
        expect(!vm.Deinterlace && vm.LutPath == "" && !vm.FadeIn && vm.Container == "mkv" && !vm.WebOptimized, "and applies its filters and fades with the export (and, now, the container and Web Optimized)");

        // Saving carries the format; a file round trip keeps it; an imported file is the user's own, never a built-in's name.
        vm.Container = "mov";
        vm.WebOptimized = true;
        vm.LoudnessTarget = -14;
        var saved = vm.CurrentEncodingSettings();
        expect(saved is { Container: "mov", WebOptimized: true, LoudnessTarget: -14, Category: null }, "the settings saved as a preset carry the container, Web Optimized and the loudness target, as the user's own");
        var file = Path.Combine(Path.GetTempPath(), $"handpeg-export-preset-{Guid.NewGuid():N}.hppreset");
        try
        {
            vm.PresetName = "Round Trip";
            vm.ExportPreset(file);
            var json = File.ReadAllText(file);
            File.WriteAllText(file, json.Replace("\"Name\": \"Round Trip\"", "\"Name\": \"YouTube Shorts\", \"Category\": \"Platform & Social\"", StringComparison.Ordinal));
            vm.Container = "mkv";
            vm.ImportPreset(file);
            var imported = vm.Presets.Last();
            expect(imported is { Name: "YouTube Shorts (Custom)", Category: null, Container: "mov", WebOptimized: true } && vm.Container == "mov" && ReferenceEquals(vm.SelectedPreset, imported),
                "a .hppreset file round trip keeps the format; one naming a built-in (and claiming its group) is imported as \"YouTube Shorts (Custom)\", the user's own, and applied");
        }
        finally
        {
            File.Delete(file);
        }

        vm.PresetName = "Reddit";
        vm.SavePresetCommand.Execute(null);
        expect(vm.Presets.Count(p => p.Name == "Reddit") == 1 && vm.Presets.Any(p => p is { Name: "Reddit (Custom)", IsBuiltIn: false }) && BuiltIn("Reddit").IsBuiltIn,
            "saving under a built-in's name keeps the built-in and saves \"Reddit (Custom)\"");
        vm.PresetName = "Reddit";
        vm.SavePresetCommand.Execute(null);
        expect(vm.Presets.Count(p => p.Name.StartsWith("Reddit (Custom", StringComparison.Ordinal)) == 1,
            "saving a tweak under the built-in's name again replaces \"Reddit (Custom)\", never adds \"Reddit (Custom 2)\"");
        vm.PresetName = "YouTube Shorts";
        vm.SavePresetCommand.Execute(null);
        expect(vm.Presets.Any(p => p.Name == "YouTube Shorts (Custom)") && vm.Presets.Any(p => p.Name == "YouTube Shorts (Custom 2)"),
            "a \"(Custom)\" that came from elsewhere (the import above) is kept: a save from the built-in goes beside it as \"(Custom 2)\"");

        // A user's own preset is applied as it was saved: its Keep Aspect Ratio, and one side as that side (not a "p" size on the short side).
        var own = Empty();
        own.SelectedPreset = new EncodingPreset { Name = "Mine 1280", KeepAspectRatio = false, OutputWidth = "1280", OutputHeight = "" };
        expect(!own.KeepAspectRatio && (own.OutputWidth, own.OutputHeight) == ("1280", "") && (own.FrameWidth, own.FrameHeight) == (1280, 720),
            "a user's preset with Keep Aspect Ratio off and only Width 1280 keeps both: 1280 x 720, not 2276 x 1280");
        var file2 = PresetStore.FilePath;
        expect(File.Exists(file2) && !File.ReadAllText(file2).Contains("\"Category\"", StringComparison.Ordinal), "presets.json holds the user's presets only, never a built-in");
    }

    // ----- export-defaults -----

    private static void Defaults(Action<bool, string> expect, StringBuilder text)
    {
        foreach (var (w, h, fps, hdr, kbps) in new (int, int, double, bool, int)[]
                 {
                     (1280, 720, 30, false, 5000), (1920, 1080, 30, false, 8000), (1920, 1080, 60, false, 12000), (1080, 1920, 60, false, 12000),
                     (2560, 1440, 30, false, 16000), (2560, 1440, 60, false, 24000), (3840, 2160, 30, false, 35000), (3840, 2160, 60, false, 52500),
                     (1920, 1080, 60, true, 15000), (1920, 1080, 120, false, 16000),
                 })
        {
            var got = MainViewModel.AutoBitrateKbps(w, h, fps, hdr);
            expect(got == kbps, $"the automatic bitrate at {w} x {h}, {fps} fps{(hdr ? ", HDR" : "")}: {got} kbps (wanted {kbps})");
        }

        var vm = Empty();
        expect(vm.RateControl == Vbr && vm.RateControlChoice == "VBR" && vm.TargetBitrate == "", "a new export is VBR (stored Average Bitrate (ABR)) with no bitrate typed");
        var auto = MainViewModel.AutoBitrateKbps(vm.FrameWidth, vm.FrameHeight, vm.Document.FrameRate.Fps, false);
        expect(vm.EffectiveBitrateKbps == auto && vm.AutoBitrateText.Contains(auto.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            $"so the bitrate in force is the automatic one ({auto} kbps for {vm.FrameWidth} x {vm.FrameHeight}), and the box's hint says so");
        Choose(vm, "libx264");
        var args = ExportState(vm).VideoCodecArguments;
        text.Append("   x264 at the defaults: ").Append(args).Append('\n');
        expect(args.Contains("-preset veryslow", StringComparison.Ordinal) && args.Contains($"-b:v {auto}k", StringComparison.Ordinal) && !args.Contains("-crf", StringComparison.Ordinal),
            "x264 starts at veryslow, VBR at the automatic bitrate");
        vm.TargetBitrate = "9000";
        expect(vm.EffectiveBitrateKbps == 9000 && vm.AutoBitrateText == "" && ExportState(vm).VideoCodecArguments.Contains("-b:v 9000k", StringComparison.Ordinal), "a bitrate typed takes over");
        vm.TargetBitrate = "";
        expect(vm.EffectiveBitrateKbps == auto, "and emptied, the automatic one is back");
        var estimate = (string)typeof(MainViewModel).GetMethod("EstimateOutputSize", Hidden)!.Invoke(vm, [((int, int)?)(1920, 1080), 60.0, 60.0, new List<AudioTrack>(), false])!;
        expect(!estimate.Contains("CQ", StringComparison.Ordinal) && !estimate.Contains("Load a source", StringComparison.Ordinal),
            $"the Projected Output size at VBR with no bitrate typed is worked out from the automatic bitrate, not the CQ rule of thumb ({estimate})");
        vm.EncoderPreset = "fast";
        Choose(vm, "libx265");
        expect(vm.EncoderPreset == "fast", "a step chosen by hand on x264 stands on x265");
        Choose(vm, "hevc_nvenc");
        Choose(vm, "libx265");
        expect(vm.EncoderPreset == "veryslow", "x265 coming from NVENC starts at veryslow");

        foreach (var (name, step) in new[] { ("libx264", "veryslow"), ("libx265", "veryslow"), ("hevc_nvenc", "p7"), ("h264_amf", "high_quality"), ("hevc_qsv", "veryslow"),
                     ("h264_mf", null), ("libsvtav1", null), ("libvpx-vp9", null), ("prores_ks", null), ("copy", null) })
        {
            var option = name == "copy" ? EncoderOption.Copy : EncoderOption.Software.FirstOrDefault(e => e.Name == name) ?? EncoderOption.FromHardwareName(name, qualcommAdreno: false);
            expect(MainViewModel.BestStepOf(option) == step, $"{name} starts at {step ?? "no step (it has none here)"}");
        }

        Choose(vm, "h264_mf");
        var mf = ExportState(vm).VideoCodecArguments;
        text.Append("   Media Foundation at the defaults: ").Append(mf).Append('\n');
        expect(!mf.Contains("-preset", StringComparison.Ordinal), "Media Foundation is given no -preset (it has none)");
        expect(mf.Contains($"-c:v h264_mf {MediaFoundationEncoder.ExportArguments(true, false, auto, vm.Crf)}", StringComparison.Ordinal)
               && mf.Contains("-compression_level 100", StringComparison.Ordinal) && Regex.Matches(mf, @"(?<!\S)-quality\s").Count == 0,
            "and its slowest step (5.4C): the command carries MediaFoundationEncoder.ExportArguments as it stands, -compression_level 100 in it, no -quality in a bitrate mode");
        vm.RateControl = Cq;
        mf = ExportState(vm).VideoCodecArguments;
        expect(mf.Contains($"-rate_control quality -quality {MediaFoundationEncoder.QualityFromCrf(vm.Crf)} -compression_level 100 -hw_encoding 1", StringComparison.Ordinal)
               && Regex.Matches(mf, @"(?<!\S)-quality\s").Count == 1,
            "in constant quality: one -quality, the CQ level from the slider, then -compression_level 100 and hardware only");
        vm.RateControl = Vbr;
        expect(vm.WebOptimized && new AppSettings().DefaultWebOptimized, "Web Optimized is on to start with");
        expect(new EncodingPreset() is { RateControl: Vbr, TargetBitrate: "", EncoderPreset: "" }, "a preset that names nothing is VBR at the automatic bitrate and the encoder's best step");

        // The sound: FLAC beside PCM (no bitrate given to either), Normalize's -16 or -14 LUFS.
        expect(vm.AudioEncoders.Contains("flac") && AudioTrack.IsLossless("flac") && AudioTrack.IsLossless("pcm_s16le") && !AudioTrack.IsLossless("aac"), "FLAC is offered, lossless as PCM is");
        expect(vm.LoudnessTarget == -16 && vm.LoudnessTargets.Select(t => t.Key).SequenceEqual([-16, -14]) && ExportState(vm).LoudnessTargetLufs == -16
               && ExportSoundPlan.For(ExportState(vm), pictureOnly: true).LoudnessLufs == ExportSoundPlan.TargetLufs, "Normalize aims at -16 LUFS to start with (the default kept)");
        vm.LoudnessTarget = -14;
        expect(ExportState(vm).LoudnessTargetLufs == -14 && ExportSoundPlan.For(ExportState(vm), pictureOnly: true).LoudnessLufs == -14, "and at -14 when chosen, the export's sound plan with it");

        var state = new SequenceExportState { Timeline = new TimelineDocument(), SourceFrameRate = 60, CaptionLayer = new Layer { Kind = LayerKind.Captions }, VideoCodecArguments = "-c:v libx264 -crf 20", OutputPath = "D:/out/a.mkv" };
        var sound = new ExportSoundPlan([new ExportSoundTrack("", "flac", "192k", 2, [0]), new ExportSoundTrack("", "aac", "192k", 2, [1])], AudioBusLayout.AllOnOne, 48000, false);
        var command = EncoderCommand.Build(state, sound, GpuExportTiming.Of(2_000_000, FrameRate.Fps30, null), false).Arguments;
        expect(command.Contains("flac") && !command.Contains("-b:a:0") && command.Contains("-b:a:1"), "a FLAC track is encoded with no bitrate; an AAC one keeps its");

        // The Export GPU (5.4C, Q6 b): exports composite on it while it is set and still here, else on the Compositor GPU.
        GpuAdapter[] cards = [new(0, "Card A"), new(1, "Card B")];
        expect(GpuExport.ExportAdapter(new AppSettings { ExportGpuAdapter = 1, CompositorGpuAdapter = 0 }, cards) == 1
               && GpuExport.ExportAdapter(new AppSettings { ExportGpuAdapter = 2, CompositorGpuAdapter = 0 }, cards) == 0
               && GpuExport.ExportAdapter(new AppSettings { ExportGpuAdapter = -1, CompositorGpuAdapter = 1 }, cards) == 1
               && GpuExport.ExportAdapter(new AppSettings(), cards) == -1 && new AppSettings().HardwareDecodeAdapter == -1,
            "the Export GPU: card 1 when chosen; a card no longer here, or Auto, is the Compositor GPU (-1, its own Auto, to start with)");
        var gpuSource = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Services", "GpuExport.cs"));
        var start = gpuSource[gpuSource.IndexOf("public static CompositorClient? StartCompositor", StringComparison.Ordinal)..];
        start = start[..start.IndexOf("static CompositorClient? Start(int adapter", StringComparison.Ordinal)];
        var settingsXaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Dialogs", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Dialogs", "SettingsWindow.xaml.cs"));
        expect(start.Contains("ExportAdapter(settings, GpuAdapters.All)", StringComparison.Ordinal)
               && start.IndexOf("Start(settings.CompositorGpuAdapter", StringComparison.Ordinal) is > 0 and var fallback && fallback < start.IndexOf("Start(warp", StringComparison.Ordinal)
               && settingsXaml.Contains("AutomationProperties.Name=\"Export GPU\"", StringComparison.Ordinal) && !settingsCode.Contains("DecodeAdapterBox.ItemsSource", StringComparison.Ordinal),
            "StartCompositor (exports, off the window's thread) starts on ExportAdapter's card, then the Compositor GPU when that will not start, then WARP; Settings names the box Export GPU and fills it in one place (the GPU roles)");

        // The slowest step for the Squisher too (5.4C): its override starts at veryslow, its first preset is Discord (10 MB).
        expect(new AppSettings().SquisherEncoderPreset == "veryslow" && SquishPreset.Defaults()[0] is { Name: "Discord (10 MB)", TargetSizeMb: 10, AudioKbps: 96 },
            "the Squisher's override starts at veryslow (it was medium) and its first preset is Discord (10 MB), 10 MB");
        // AppSettings.Load on files of the test's own, in the test's data folder (HANDPEG_DATA_DIR): what was there is put back after.
        var file = AppSettings.FilePath;
        var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        var load = typeof(AppSettings).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes)!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var old = new AppSettings
            {
                ViewDefaultsVersion = 6, FirstRunComplete = true, HardwareDecodeAdapter = 1,
                SquisherPresets =
                [
                    new SquishPreset { Name = "Discord (Non-Nitro)", TargetSizeMb = 20, AudioKbps = 96, Icon = "gamepad" },
                    new SquishPreset { Name = "Discord (Nitro Basic)", TargetSizeMb = 50, AudioKbps = 128, Icon = "gamepad" },
                ],
            };
            File.WriteAllText(file, JsonSerializer.Serialize(old, AppSettingsJsonContext.Default.AppSettings));
            var read = (AppSettings)load.Invoke(null, null)!;
            old.SquisherPresets[0].TargetSizeMb = 18;
            File.WriteAllText(file, JsonSerializer.Serialize(old, AppSettingsJsonContext.Default.AppSettings));
            var kept = (AppSettings)load.Invoke(null, null)!;
            expect(read is { ViewDefaultsVersion: >= 7 } && read.SquisherPresets[0] is { Name: "Discord (10 MB)", TargetSizeMb: 10 } && read.SquisherPresets[1].Name == "Discord (Nitro Basic)"
                   && kept.SquisherPresets[0] is { Name: "Discord (Non-Nitro)", TargetSizeMb: 18 },
                "a settings file of before with the untouched Discord (Non-Nitro) of 20 MB reads as Discord (10 MB), 10 MB; one the user set to 18 MB stays theirs; the others keep their names");
            old.ViewDefaultsVersion = 8;
            File.WriteAllText(file, JsonSerializer.Serialize(old, AppSettingsJsonContext.Default.AppSettings));
            var chosenSince = (AppSettings)load.Invoke(null, null)!;
            expect(read is { ViewDefaultsVersion: >= 8, ExportGpuAdapter: -1 } && chosenSince.ExportGpuAdapter == 1,
                "an Export GPU saved before 5.4C (it meant something else) goes back to Auto (the Compositor GPU) once; one chosen since stays");
        }
        finally
        {
            if (original is not null)
                File.WriteAllBytes(file, original);
            else
                File.Delete(file);
        }
    }

    // ----- export-audio-only -----

    private static void AudioOnly(Action<bool, string> expect, StringBuilder text)
    {
        // A game recording with two sound streams (the game, the voice), the first set to be copied as it is: what a video export copies.
        var scene = new Scene("audio") { Main = Media.Mp4("D:/media/game.mp4", 10) };
        scene.AudioTracks.Add(new LegacyStream(0, "Game", "aac", "192k") { Passthrough = true });
        scene.AudioTracks.Add(new LegacyStream(1, "Voice", "aac", "160k"));
        var video = scene.Compile() with { MixedAudioCodec = "aac", AudioBitrate = "192k" };
        var videoPlan = ExportSoundPlan.For(video, pictureOnly: false);
        expect(!EncoderCommand.IsAudioOnly(video) && videoPlan.Tracks.Count == 2 && videoPlan.Tracks[0].IsCopy,
            "the same Sequence as an .mp4: two sound tracks, the game's copied as it is (the case an Audio Only file must not follow)");

        GpuExportCommand? Command(string extension, out SequenceExportState state, string bitrate = "192k", bool normalize = false)
        {
            state = video with { OutputPath = $"D:/out/a.{extension}", Container = extension, AudioBitrate = bitrate, NormalizeAudio = normalize };
            return GpuExport.CommandFor(state, out _);
        }

        foreach (var (extension, codec) in new[] { ("flac", "flac"), ("wav", "pcm_s16le"), ("mp3", "libmp3lame") })
        {
            var command = Command(extension, out var state, "128k");
            var args = command?.Arguments ?? [];
            text.Append("   .").Append(extension).Append(": ").Append(command?.CommandLine ?? "none").Append('\n');
            var inputs = args.Count(a => a == "-i");
            var rawFloat = args.Select((a, i) => (a, i)).Count(p => p.a == "-f" && p.i + 1 < args.Count && args[p.i + 1] == "f32le");
            expect(EncoderCommand.IsAudioOnly(state) && EncoderCommand.AudioOnlyCodec(extension) == codec && command is { Encoder: ExportEncoder.Sound, Width: 0, Height: 0, Sound.Count: 1 }
                   && command.OutputPath == $"D:/out/a.{extension}" && inputs == 1 && rawFloat == 1 && args.Contains($"{EncoderCommand.SoundToken}1}}")
                   && args.SkipWhile(a => a != "-c:a").Skip(1).FirstOrDefault() == codec && !args.Any(a => a.StartsWith("-c:a:", StringComparison.Ordinal)),
                $".{extension} is Audio Only: FFmpeg encodes the engine's one render ({codec}), one raw float input, no frames");
            expect(!args.Any(a => a is "pipe:0" or "rawvideo" or "-vf" or "-af" or "-filter_complex" or "-movflags" or "-color_range" or "-pix_fmt") && !args.Any(a => a.StartsWith("0:v", StringComparison.Ordinal)),
                $".{extension}: no pipe:0, no rawvideo, no picture mapped, no colour tags, no -movflags, and never a -vf, -af or -filter_complex");
            var bitrate = args.SkipWhile(a => a != "-b:a").Skip(1).FirstOrDefault();
            var sampleFormat = args.SkipWhile(a => a != "-sample_fmt").Skip(1).FirstOrDefault();
            expect(extension switch
                {
                    "flac" => bitrate is null && sampleFormat == "s16",
                    "wav" => bitrate is null && sampleFormat is null,
                    _ => bitrate == "128k" && sampleFormat is null,
                },
                extension switch
                {
                    "flac" => ".flac: 16 bits asked for outright (-sample_fmt s16), no bitrate (lossless)",
                    "wav" => ".wav: 16-bit PCM by its codec, no bitrate",
                    _ => ".mp3: LAME at the bitrate chosen (128k)",
                });
        }

        // One track whatever the Sequence holds: the two streams mixed on bus 0, the game's copy refused, Merge Audio Tracks off or on.
        Command("flac", out var flac);
        var plan = ExportSoundPlan.For(flac, pictureOnly: false, audioOnly: true);
        expect(plan.Tracks is [{ IsCopy: false, Codec: "flac", Channels: 2, Buses: [0] }] && plan.Layout.OrdinalBuses.Count == 2 && plan.Layout.OrdinalBuses.Values.All(b => b is [0])
               && ExportSoundPlan.For(flac, pictureOnly: false).Tracks.Count == 1 && ExportSoundPlan.For(flac with { MergeAudioTracks = true }, false, true).Tracks.Count == 1,
            "two stream places with Merge off, one copied as it is: one rendered FLAC track, both places on its bus, nothing copied (the two-argument plan sees the .flac too)");
        expect(ExportSoundPlan.For(flac with { AudioChannels = AudioChannelChoice.Mono }, false, true).Tracks is [{ Channels: 1, Layout: "mono" }],
            "Mono on the Audio tab: one mono track");
        var podcast = Command("mp3", out var mp3, "128k", normalize: true);
        var podcastPlan = ExportSoundPlan.For(mp3 with { LoudnessTargetLufs = -16 }, false, true);
        expect(podcast is not null && podcastPlan is { Normalize: true, LoudnessLufs: -16, Tracks: [{ Codec: "libmp3lame", Bitrate: "128k" }] },
            "the podcast's MP3 is normalized to -16 LUFS by the engine (its linear gain: no filter is written)");
        var silent = new Scene("silent") { Main = Media.Mp4("D:/media/silent.mp4", 5) with { HasAudio = false } }.Compile() with { OutputPath = "D:/out/silent.wav", Container = "wav" };
        expect(GpuExport.CommandFor(silent, out var why) is null && why == "the Sequence has nothing to hear", $"a Sequence with nothing to hear is refused in words ({why})");

        // The view model: the presets, the container's codec, the Export tab's video parts, the Copy encoder and the preview.
        var vm = Empty();
        vm.SelectedPreset = BuiltIn("Podcast / Voice (MP3, -16 LUFS)");
        var state2 = ExportState(vm);
        expect(vm is { Container: "mp3", AudioEncoder: "libmp3lame", AudioBitrate: "128k", NormalizeAudio: true, LoudnessTarget: -16, MergeAudioTracks: true, IsAudioOnlyExport: true, IsVideoExport: false }
               && EncoderCommand.IsAudioOnly(state2) && state2.LoudnessTargetLufs == -16,
            "Podcast / Voice (MP3, -16 LUFS) picked: mp3, LAME at 128k, mixed, normalized to -16 LUFS; the export is Audio Only");
        expect(vm.OutputInfoRows.FirstOrDefault()?.Value.StartsWith("Audio only (MP3)", StringComparison.Ordinal) == true,
            "Projected Output says it is audio only");
        vm.Container = "wav";
        expect(vm.AudioEncoder == "pcm_s16le" && vm.Containers.TakeLast(3).SequenceEqual(["flac", "wav", "mp3"]) && vm.Containers.Take(6).SequenceEqual(["mp4", "mkv", "webm", "mov", "gif", "webp"]),
            "choosing WAV sets 16-bit PCM; the containers end with flac, wav, mp3 (the Save dialog's filter in the same order)");
        Choose(vm, "copy");
        var copyState = ExportState(vm);
        var isCopy = (bool)typeof(MainViewModel).GetMethod("IsCopyExport", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [copyState])!;
        expect(copyState.CopyVideoEncoder && !isCopy, "the Copy encoder left selected never makes a .wav a stream copy");
        vm.IsExportQualityPreview = true;
        expect(!vm.IsExportQualityPreview && vm.CurrentLoopbackSettings(out var noPreview) is null && noPreview == "an Audio Only file has no picture",
            "the Export Quality Preview stays off for an Audio Only file (there is no picture to encode)");
        vm.Container = "mp4";
        expect(!vm.IsAudioOnlyExport && vm.IsVideoExport, "back to mp4: a video export again");

        // The routes: RunExportAsync sends an Audio Only file to GpuExport.RunAudioOnlyAsync (never to FfmpegRunner with its token, never
        // to the compositor); the Export tab folds its video parts by Visibility (no control bound twice: export-tab-columns).
        var root = RepositoryRoot();
        var command2 = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "MainViewModel.Command.cs"));
        var run = command2[command2.IndexOf("private async Task RunExportAsync", StringComparison.Ordinal)..];
        run = run[..run.IndexOf("\n    }", StringComparison.Ordinal)];
        var audioBranch = run.IndexOf("EncoderCommand.IsAudioOnly(state)", StringComparison.Ordinal);
        expect(audioBranch > 0 && audioBranch < run.IndexOf("FfmpegRunner.RunAsync", StringComparison.Ordinal) && run.Contains("GpuExport.RunAudioOnlyAsync", StringComparison.Ordinal),
            "RunExportAsync routes an Audio Only file to GpuExport.RunAudioOnlyAsync before anything else (the queue runs through it too)");
        var gpu = File.ReadAllText(Path.Combine(root, "src", "Services", "GpuExport.cs"));
        var audioRun = gpu[gpu.IndexOf("public static async Task RunAudioOnlyAsync", StringComparison.Ordinal)..];
        audioRun = audioRun[..audioRun.IndexOf("\n    }", StringComparison.Ordinal)];
        expect(audioRun.Contains("RenderSoundAsync", StringComparison.Ordinal) && !audioRun.Contains("StartCompositor", StringComparison.Ordinal) && !audioRun.Contains("Prepare(", StringComparison.Ordinal)
               && !audioRun.Contains("BurnedSubtitles", StringComparison.Ordinal),
            "RunAudioOnlyAsync renders the sound and runs FFmpeg: no compositor started, no scene prepared, no subtitles burned");
        var main = XDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MainWindow.xaml")));
        string? Attr(XElement e, string name) => (string?)e.Attribute(name);
        var folded = main.Descendants().Where(e => Attr(e, "Visibility") == "{Binding IsVideoExport, Converter={StaticResource BoolToVisibility}}").ToList();
        var codecRow = main.Descendants().FirstOrDefault(e => Attr(e, "AutomationProperties.Name") == "Video Encoder");
        var toggle = main.Descendants().FirstOrDefault(e => e.Name.LocalName == "ToggleButton" && Attr(e, "IsChecked") == "{Binding IsExportQualityPreview}");
        var card2 = main.Descendants().FirstOrDefault(e => Attr(e, "AutomationProperties.Name") == "Quality, Bitrate and Resolution");
        expect(codecRow is not null && folded.Contains(codecRow) && folded.Any(e => e.Descendants().Any(d => Attr(d, "IsChecked") == "{Binding WebOptimized}"))
               && folded.Any(e => e.Descendants().Any(d => Attr(d, "Text") == "{Binding HdrStatusText}"))
               && card2 is not null && folded.Any(e => e.Parent?.Parent == card2 && e.Descendants().Any(d => Attr(d, "Text") == "{Binding Crf, UpdateSourceTrigger=PropertyChanged}"))
               && card2.Descendants().Any(e => Attr(e, "Visibility") == "{Binding IsAudioOnlyExport, Converter={StaticResource BoolToVisibility}}")
               && toggle is not null && Attr(toggle, "IsEnabled") == "{Binding IsVideoExport}",
            "the Export tab folds the Video Codec row, Web Optimized and 10-bit, the HDR notes and card 2's quality, bitrate and size for an Audio Only file (a line says why), and greys out the Export Quality Preview");

        RealAudioOnly(expect);
    }

    // The three files made for real (where FFmpeg is here): a 1 kHz tone's Sequence through GpuExport.RunAudioOnlyAsync, each decoded again.
    private static void RealAudioOnly(Action<bool, string> expect)
    {
        const string flacLine = "a real FLAC and WAV of a tone's Sequence: as long as the Sequence (2.5 s, 120,000 samples) and the engine's own render to within 16-bit rounding";
        const string mp3Line = "Normalize at -16 LUFS for real: a FLAC measured at -16 LUFS within 0.1 LU (the engine's gain), the podcast MP3 (128k) as long as the Sequence (within LAME's padding) and within 0.75 LU of it (what the codec loses)";
        if (GpuExportTests.FfmpegCandidates().FirstOrDefault(File.Exists) is not { } ffmpeg)
        {
            Console.WriteLine("  note  no FFmpeg found (HANDPEG_FFMPEG, or %LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg or ffmpeg-arm64): the Audio Only files were not made");
            expect(true, flacLine);
            expect(true, mp3Line);
            return;
        }

        const int rate = 48000;
        var folder = Path.Combine(Path.GetTempPath(), $"handpeg-audio-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            // A stereo 16-bit WAV of a 1 kHz tone at a tenth of full scale, 3 s; the Sequence plays its first 2.5 s.
            var tone = Path.Combine(folder, "tone.wav");
            var frames = 3 * rate;
            using (var file = new BinaryWriter(File.Create(tone)))
            {
                file.Write("RIFF"u8);
                file.Write(36 + frames * 4);
                file.Write("WAVEfmt "u8);
                file.Write(16);
                file.Write((short)1);
                file.Write((short)2);
                file.Write(rate);
                file.Write(rate * 4);
                file.Write((short)4);
                file.Write((short)16);
                file.Write("data"u8);
                file.Write(frames * 4);
                for (var i = 0; i < frames; i++)
                {
                    var value = (short)Math.Round(3277 * Math.Sin(2 * Math.PI * 1000 * i / rate));
                    file.Write(value);
                    file.Write(value);
                }
            }

            var document = new TimelineDocument { Width = 1920, Height = 1080, FrameRate = FrameRate.Fps30 };
            var track = new TimelineTrackModel { Kind = TimelineTrackKind.Audio, TrackNumber = 1 };
            track.Clips.Add(new TimelineClip
            {
                ContentKind = LayerKind.Audio, SourcePath = tone, AudioStreamIndex = 0, Start = 0, End = 2.5, SourceDuration = 3,
                State = new LayerState { Kind = LayerKind.Audio, Name = "tone", ImagePath = tone, MediaDuration = 3, HasAudio = true },
            });
            document.Tracks.Add(track);
            var state = new SequenceExportState
            {
                Timeline = document, SourceFrameRate = 30, RequestedSize = (1920, 1080), OutputSize = (1920, 1080), FfmpegExecutable = ffmpeg,
                CaptionLayer = Layer.FromState(new LayerState { Kind = LayerKind.Captions, Name = "Subtitles", SizeWidth = 0.9, SizeHeight = 0.2 }, 1920, 1080, 1920, 1080),
            };

            float[] Make(string name, SequenceExportState with)
            {
                var output = Path.Combine(folder, name);
                Task.Run(() => GpuExport.RunAudioOnlyAsync(with with { OutputPath = output }, null, new Quiet(), CancellationToken.None)).GetAwaiter().GetResult();
                return Decode(ffmpeg, output, rate);
            }

            var engine = new float[(int)(2.5 * rate) * 2];
            AudioMixer.Render(AudioProgram.Build(state), 0, engine, rate);
            var flac = Make("a.flac", state with { Container = "flac" });
            var wav = Make("a.wav", state with { Container = "wav" });
            static double Worst(float[] a, float[] b) => a.Length != b.Length ? double.PositiveInfinity : a.Zip(b, (x, y) => Math.Abs(x - y)).Max();
            var (flacWorst, wavWorst) = (Worst(flac, engine) * 32768, Worst(wav, engine) * 32768);
            Console.WriteLine(FormattableString.Invariant($"  (audio only: FLAC {flac.Length / 2} samples, {flacWorst:0.00} LSB from the engine's render; WAV {wav.Length / 2}, {wavWorst:0.00} LSB)"));
            expect(flac.Length == 240_000 && wav.Length == 240_000 && flacWorst <= 1.5 && wavWorst <= 1.5, flacLine);

            var mp3 = Make("podcast.mp3", state with { Container = "mp3", AudioBitrate = "128k", NormalizeAudio = true, LoudnessTargetLufs = -16 });
            var loud = Make("loud.flac", state with { Container = "flac", NormalizeAudio = true, LoudnessTargetLufs = -16 });
            var meter = new LoudnessMeter(rate, 2);
            meter.Add(mp3, mp3.Length / 2);
            var lossless = new LoudnessMeter(rate, 2);
            lossless.Add(loud, loud.Length / 2);
            Console.WriteLine(FormattableString.Invariant($"  (audio only: the podcast MP3 is {mp3.Length / 2} samples, {meter.IntegratedLufs:0.00} LUFS; the same normalized as FLAC {lossless.IntegratedLufs:0.00} LUFS)"));
            expect(Math.Abs(lossless.IntegratedLufs + 16) <= 0.1 && Math.Abs(mp3.Length / 2 - 120_000) <= 1200 && Math.Abs(meter.IntegratedLufs + 16) <= 0.75, mp3Line);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // A file's first audio stream as FFmpeg decodes it: interleaved stereo float at the rate.
    private static float[] Decode(string ffmpeg, string path, int rate)
    {
        var raw = path + ".f32";
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg)
               {
                   ArgumentList = { "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-map", "0:a:0", "-ac", "2", "-ar", rate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "f32le", raw },
                   UseShellExecute = false, CreateNoWindow = true,
               })!)
        {
            process.WaitForExit(60_000);
        }

        if (!File.Exists(raw))
            return [];
        var bytes = File.ReadAllBytes(raw);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    private sealed class Quiet : IProgress<FfmpegProgress>
    {
        public void Report(FfmpegProgress value)
        {
        }
    }

    // ----- export-hdr-passthrough -----

    private static void Hdr(Action<bool, string> expect, StringBuilder text)
    {
        var x265 = EncoderOption.Software.First(e => e.Name == "libx265");
        var x264 = EncoderOption.Software.First(e => e.Name == "libx264");
        var nvenc = EncoderOption.FromHardwareName("hevc_nvenc", qualcommAdreno: false);
        foreach (var (what, why) in new (string, string?)[]
                 {
                     ("SDR project", MainViewModel.HdrBlocker(ProjectColorSpace.Sdr709, true, false, x265, true, true, false, null)),
                     ("PQ, HDR to SDR chosen", MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, false, false, x265, true, true, false, null)),
                     ("PQ, a GIF", MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, true, x265, true, true, false, null)),
                     ("PQ, Copy", MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, EncoderOption.Copy, true, true, false, null)),
                     ("PQ, x264 10-bit", MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, x264, true, true, false, null)),
                     ("PQ, x265 8-bit", MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, x265, false, true, false, null)),
                     ("PQ, NVENC HEVC without 10-bit on the card", MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, nvenc, true, false, false, null)),
                     ("PQ, x265 10-bit, Extra Options' -pix_fmt", MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, x265, true, true, true, null)),
                     ("PQ, x265 10-bit, an image on it", MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, x265, true, true, false, "an image clip is SDR")),
                     ("PQ, x265 10-bit, HDR pictures only", MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, x265, true, true, false, null)),
                     ("HLG, NVENC HEVC 10-bit", MainViewModel.HdrBlocker(ProjectColorSpace.Hlg, true, false, nvenc, true, true, false, null)),
                 })
        {
            text.Append("   ").Append(what).Append(": ").Append(why is null ? "kept HDR" : "tone-mapped (" + why + ")").Append('\n');
        }

        expect(MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, x265, true, true, false, null) is null
               && MainViewModel.HdrBlocker(ProjectColorSpace.Hlg, true, false, nvenc, true, true, false, null) is null
               && MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, x265, false, true, false, null) is { } eight && eight.Contains("10-bit", StringComparison.Ordinal)
               && MainViewModel.HdrBlocker(ProjectColorSpace.HdrPq, true, false, x264, true, true, false, null) is { } codec && codec.Contains("HEVC or AV1", StringComparison.Ordinal),
            "the rule (Q1): HDR under Match Project Settings, 10-bit on, an HEVC/AV1 encoder that does 10-bit here, HDR pictures only; 8-bit or H.264 is tone-mapped with the hint");

        var vm = Empty();
        vm.Document.ColorSpace = ProjectColorSpace.HdrPq;
        expect(ExportState(vm) is { TonemapToSdr: true, HdrTransfer: null } && vm.ProjectColorNote.Contains("tone-mapped", StringComparison.Ordinal),
            "an HDR project at the defaults is still tone-mapped to BT.709 SDR, and says why");
        Choose(vm, "libx265");
        vm.TenBit = true;
        var state = ExportState(vm);
        text.Append("   PQ, x265 10-bit: ").Append(vm.ProjectColorNote).Append('\n');
        expect(vm.IsHdrPassthrough && state is { TonemapToSdr: false, HdrTransfer: "smpte2084" } && state.VideoCodecArguments.Contains("-pix_fmt yuv420p10le", StringComparison.Ordinal)
               && vm.ProjectColorNote.Contains("HDR10 (PQ), 10-bit", StringComparison.Ordinal),
            "with x265 and 10-bit it is kept HDR: no tone-map, HdrTransfer smpte2084, yuv420p10le, and the note says HDR10 (PQ), 10-bit");
        expect(vm.LivePreviewState() is { TonemapToSdr: true, HdrTransfer: null, IsPreview: true }, "while the player (8-bit BT.709) still shows it tone-mapped");
        vm.Document.ColorSpace = ProjectColorSpace.Hlg;
        expect(ExportState(vm).HdrTransfer == "arib-std-b67", "an HLG project is tagged arib-std-b67");
        vm.Colorspace = "HDR to SDR (Tonemap)";
        expect(ExportState(vm) is { TonemapToSdr: true, HdrTransfer: null }, "HDR to SDR chosen tone-maps whatever the encoder");
        vm.Colorspace = Match;
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 9 };
        track.Clips.Add(new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/logo.png", Start = 0, End = 2 });
        vm.Document.Tracks.Add(track);
        expect(!vm.IsHdrPassthrough && ExportState(vm) is { TonemapToSdr: true, HdrTransfer: null } && vm.ProjectColorNote.Contains("an image clip is SDR", StringComparison.Ordinal),
            "an SDR picture on the Sequence (an image) blocks it: tone-mapped, and the note names it");

        // The encode's tail: P010 piped, BT.2020 tags in and out, 10 bits handed to the encoder; the same state without HdrTransfer as before.
        static SequenceExportState Of(string arguments, string? transfer) =>
            new() { Timeline = new TimelineDocument { Width = 3840, Height = 2160 }, SourceFrameRate = 60, CaptionLayer = new Layer { Kind = LayerKind.Captions }, VideoCodecArguments = arguments, OutputPath = "D:/out/hdr.mp4", HdrTransfer = transfer, WebOptimized = true };
        var none = new ExportSoundPlan([], AudioBusLayout.AllOnOne, 48000, false);
        var timing = GpuExportTiming.Of(2_000_000, FrameRate.Fps30, null);
        var sdr = EncoderCommand.Build(Of("-c:v libx265 -preset veryslow -crf 20 -pix_fmt yuv420p10le", null), none, timing, false);
        var hdr = EncoderCommand.Build(Of("-c:v libx265 -preset veryslow -crf 20 -pix_fmt yuv420p10le", "smpte2084"), none, timing, false);
        text.Append("   SDR: ").Append(sdr.CommandLine).Append('\n').Append("   HDR: ").Append(hdr.CommandLine).Append('\n');
        expect(sdr.CommandLine.Contains("-pix_fmt yuv420p -video_size", StringComparison.Ordinal) && Count(sdr.CommandLine, "-colorspace bt709 -color_primaries bt709 -color_trc bt709") == 2 && !sdr.PipesP010,
            "without HdrTransfer the command is as before: I420 piped, BT.709 tagged in and out");
        expect(hdr.PipesP010 && hdr.FrameBytes == 2 * CompositorProtocol.Nv12Bytes(3840, 2160) && hdr.CommandLine.Contains("-pix_fmt p010le -video_size", StringComparison.Ordinal)
               && Count(hdr.CommandLine, "-color_range tv -colorspace bt2020nc -color_primaries bt2020 -color_trc smpte2084") == 2 && !hdr.CommandLine.Contains("bt709", StringComparison.Ordinal),
            "with it: P010 piped (twice NV12's bytes, FlagReadbackP010), tagged BT.2020nc / BT.2020 / smpte2084 on the pipe and on the stream, nothing BT.709");
        expect(hdr.CommandLine.Replace("p010le", "yuv420p").Replace("bt2020nc", "bt709").Replace("bt2020", "bt709").Replace("smpte2084", "bt709") == sdr.CommandLine,
            "and nothing else differs");
        var hlgNvenc = EncoderCommand.Build(Of("-c:v hevc_nvenc -preset p7 -rc vbr -cq 20 -b:v 0", "arib-std-b67"), none, timing, false).Arguments;
        var bare = EncoderCommand.Build(Of("-c:v libsvtav1 -crf 28", "smpte2084"), none, timing, false).Arguments;
        expect(Count(string.Join(" ", hlgNvenc), "-pix_fmt p010le") == 2 && string.Join(" ", hlgNvenc).Contains("-color_trc arib-std-b67", StringComparison.Ordinal)
               && string.Join(" ", bare).Contains("-crf 28 -pix_fmt yuv420p10le", StringComparison.Ordinal),
            "options that name no pixel format are handed 10 bits: p010le to a hardware encoder (HLG tagged arib-std-b67), yuv420p10le to a software one");

        // Auto-Captions with no text cues are transcribed at export and drawn all the same: SDR white in a PQ canvas would be ~10,000 nits.
        var captioned = Empty();
        captioned.Document.ColorSpace = ProjectColorSpace.HdrPq;
        Choose(captioned, "libx265");
        captioned.TenBit = true;
        captioned.AutoCaptions = true;
        expect(!captioned.IsHdrPassthrough && ExportState(captioned) is { TonemapToSdr: true, HdrTransfer: null } && captioned.ProjectColorNote.Contains("the captions are SDR", StringComparison.Ordinal),
            "Auto-Captions with no text cues (transcribed at export) block it too: tone-mapped, and the note says the captions are SDR");

        // An HDR file in an SDR project is tone-mapped only under HDR to SDR (Tonemap): the word under the encoder says so, or where to turn it on.
        var sdrProject = Empty();
        sdrProject.IsSourceHdr = true;
        var hint = sdrProject.HdrStatusText;
        sdrProject.Colorspace = "HDR to SDR (Tonemap)";
        expect(hint.Contains("choose HDR to SDR (Tonemap)", StringComparison.Ordinal) && sdrProject.HdrStatusText.Contains("it is tone-mapped to BT.709", StringComparison.Ordinal),
            "an HDR source in an SDR project under Match Project Settings points to HDR to SDR (Tonemap); with it chosen, says it is tone-mapped");
    }

    private static int Count(string text, string what) => (text.Length - text.Replace(what, "", StringComparison.Ordinal).Length) / what.Length;

    // ----- encoder-probe-vendor-filter -----

    private static void VendorFilter(Action<bool, string> expect, StringBuilder text)
    {
        GpuAdapter Card(uint vendor, string name) => new(0, name) { VendorId = vendor };
        var amd = new[] { Card(0x1002, "AMD Radeon RX 7900 XTX") };
        var nvidiaIntel = new[] { Card(0x10DE, "NVIDIA GeForce GTX 1070"), Card(0x8086, "Intel Arc A310") };
        var adreno = new[] { Card(0x4D4F4351, "Qualcomm(R) Adreno(TM) X1-85 GPU") };
        foreach (var (cards, label, problem, present) in new (GpuAdapter[], string, string, bool)[]
                 {
                     (amd, "AMD", "h264_nvenc: Cannot load nvcuda.dll", false), (amd, "AMD", "h264_amf: driver too old", true), (amd, "AMD", "h264_qsv: no device", false),
                     (amd, "AMD", "h264_mf: no encoder", true), (nvidiaIntel, "NVIDIA + Intel", "h264_nvenc: OpenEncodeSessionEx failed", true),
                     (nvidiaIntel, "NVIDIA + Intel", "h264_qsv: unsupported", true), (nvidiaIntel, "NVIDIA + Intel", "h264_amf: Cannot load amfrt64.dll", false),
                     (adreno, "Adreno", "h264_nvenc: Cannot load nvcuda.dll", false), (adreno, "Adreno", "h264_mf: busy", true), ([], "no card listed", "h264_nvenc: Cannot load nvcuda.dll", true),
                 })
        {
            expect(EncoderProber.VendorPresent(problem, cards) == present, $"{label}: \"{problem}\" is {(present ? "said" : "left unsaid")}");
        }

        // Before the cards are listed (the first frame's path) nothing is filtered, and nothing lists them; once listed, the absent vendors go.
        var found = typeof(GpuAdapters).GetField("_found", BindingFlags.NonPublic | BindingFlags.Static)!;
        var kept = found.GetValue(null);
        try
        {
            found.SetValue(null, null);
            List<string> problems = ["h264_nvenc: Cannot load nvcuda.dll", "h264_amf: driver too old"];
            var early = EncoderProber.ProblemsForCardsHere(problems);
            expect(early.SequenceEqual(problems) && !GpuAdapters.IsListed, "not listed yet: every problem is kept, and asking listed no card (no DXGI before the first frame, Law 1)");
            found.SetValue(null, (IReadOnlyList<GpuAdapter>)amd);
            expect(GpuAdapters.IsListed && EncoderProber.ProblemsForCardsHere(problems).SequenceEqual(["h264_amf: driver too old"]),
                "listed (an AMD card): the NVENC problem is left out, AMD's is said");
        }
        finally
        {
            found.SetValue(null, kept);
        }

        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ViewModels", "MainViewModel.cs"));
        var apply = source[source.IndexOf("private void ApplyHardwareEncoders", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("\n    }", StringComparison.Ordinal)];
        expect(!apply.Contains("GpuAdapters.All", StringComparison.Ordinal) && source.Contains("EncoderProber.ProblemsForCardsHere(_hardwareProblems)", StringComparison.Ordinal),
            "ApplyHardwareEncoders (which runs before the first frame) lists no card: the status line filters only what is listed, and the recheck after the first frame lists them off the window's thread");
    }

    // ----- export-tab-columns -----

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static void Columns(Action<bool, string> expect, StringBuilder text)
    {
        var root = RepositoryRoot();
        var mainText = File.ReadAllText(Path.Combine(root, "src", "MainWindow.xaml"));
        var main = XDocument.Parse(mainText);
        string? Attr(XElement e, XName name) => (string?)e.Attribute(name);
        var export = main.Descendants().First(e => Attr(e, X + "Name") == "ExportPanel");
        var grid = export.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == "ExportColumns");
        var cards = grid?.Elements().Where(e => e.Name.LocalName == "Border").ToList() ?? [];
        expect(grid is not null && cards.Count == 3 && cards.All(c => Attr(c, "Style") == "{StaticResource ExportCard}")
               && cards.Select(c => Attr(c, "AutomationProperties.Name")).SequenceEqual(["Preset and Format", "Quality, Bitrate and Resolution", "Audio and Output"])
               && cards.Select(c => Attr(c, "Grid.Column") ?? "0").SequenceEqual(["0", "1", "2"]),
            "the Export tab's middle is ExportColumns: three ExportCard cards, Preset & Format, Quality, Bitrate & Resolution, Audio & Output, in its three columns");

        // Which binding is in which card (a control's own value: SelectedItem, SelectedValue, IsChecked, Text, ItemsSource of a list it shows).
        static IEnumerable<string> Bound(XElement e) => new[] { "SelectedItem", "SelectedValue", "IsChecked", "Text", "ItemsSource" }
            .Select(a => (string?)e.Attribute(a)).OfType<string>().Select(v => Regex.Match(v, @"^\{Binding\s+(?:Path=)?([A-Za-z.]+)\s*[,}]")).Where(m => m.Success).Select(m => m.Groups[1].Value);
        List<string> In(XElement card) => card.Descendants().SelectMany(Bound).ToList();
        var homes = new (int Card, string[] Bindings)[]
        {
            (0, ["Containers", "Container", "VideoEncoders", "VideoEncoder", "WebOptimized", "TenBit", "CopyBypassWarning", "ProjectColorNote", "HdrStatusText", "HardwareEncoderStatusText"]),
            (1, ["RateControlChoice", "Crf", "TargetBitrate", "TargetFileSize", "EncoderPreset", "Framerate", "CustomFramerate", "ResolutionPreset", "OutputWidth", "OutputHeight",
                 "KeepAspectRatio", "UseVerticalResolution", "Colorspace", "AutoBitrateText", "TargetSizeHint", "Tune", "NvencTune", "Profile", "Level", "NvencMultipass", "AmfUsage",
                 "Lookahead", "NvencSpatialAq", "NvencTemporalAq", "PixelAspectRatio", "CustomPixelAspectRatio", "ExtraVideoArguments", "ChapterMarkers", "ChaptersAtCuts",
                 "ChaptersAtMarkers", "SponsorBlockMode"]),
            (2, ["AudioEncoder", "AudioBitrate", "AudioSampleRate", "AudioChannels", "MergeAudioTracks", "NormalizeAudio", "LoudnessTarget", "SubtitleTracks",
                 "IsExportQualityPreview", "ExportQualityPreviewStatus", "OutputInfoRows"]),
        };
        foreach (var (card, bindings) in homes)
        {
            var here = cards.Count == 3 ? In(cards[card]) : [];
            var missing = bindings.Where(b => !here.Contains(b)).ToList();
            expect(missing.Count == 0, $"card {card + 1} holds {string.Join(", ", bindings)}{(missing.Count > 0 ? $" (missing: {string.Join(", ", missing)})" : "")}");
        }

        // Each export control once in the whole window (CLAUDE.md 10.1): its binding is on one control only.
        var everywhere = main.Descendants().SelectMany(Bound).ToList();
        var twice = homes.SelectMany(h => h.Bindings).Where(b => b is not ("Containers" or "VideoEncoders" or "SubtitleTracks") && everywhere.Count(x => x == b) != 1).ToList();
        expect(twice.Count == 0, $"each of them is bound by exactly one control in MainWindow.xaml{(twice.Count > 0 ? $" (not once: {string.Join(", ", twice)})" : "")}");
        var advanced = cards.Count == 3 ? cards[1].Descendants().FirstOrDefault(e => e.Name.LocalName == "Expander") : null;
        expect(advanced is not null && Attr(advanced, "Header") == "Advanced Codec & Chapter Options" && Attr(advanced, "IsExpanded") == "False"
               && new[] { "Tune", "Profile", "Level", "ExtraVideoArguments", "ChapterMarkers", "SponsorBlockMode" }.All(In(advanced).Contains),
            "the rare options are in card 2's folded \"Advanced Codec & Chapter Options\" drawer (tune, profile, level, extra options, chapters, SponsorBlock)");
        var preview = cards.Count == 3 ? cards[2].Descendants().FirstOrDefault(e => e.Name.LocalName == "ToggleButton" && Attr(e, "IsChecked") == "{Binding IsExportQualityPreview}") : null;
        expect(preview is not null && Attr(preview, "AutomationProperties.Name") == "Live Encoded Preview" && preview.Descendants().Any(e => Attr(e, "Text") == "\uE890")
               && cards[2].Descendants().Any(e => Attr(e, X + "Name") == "ExportBarSlot") && !mainText.Contains("LivePreview", StringComparison.Ordinal),
            "card 3 has the Live Encoded Preview toggle (its glyph and words) and the DestinationBar's slot; nothing is called LivePreview");
        expect(cards.Count == 3 && cards[0].Descendants().Any(e => e.Name.LocalName == "ContentControl" && Attr(e, "ContentTemplate") == "{StaticResource PresetBar}")
               && main.Descendants().Count(e => Attr(e, "AutomationProperties.Name") == "Export Presets") == 1
               && main.Descendants().Any(e => Attr(e, "AutomationProperties.Name") == "Save Current as Preset" && ((string?)e.Attribute("Content"))?.StartsWith("+ Save Current as Preset", StringComparison.Ordinal) == true)
               && main.Descendants().Any(e => (string?)e.Attribute("Content") == "Import / Export Preset..."),
            "the Export Presets selector is in card 1 (or above the tabs), with + Save Current as Preset... and Import / Export Preset...");
        var selector = main.Descendants().First(e => Attr(e, "AutomationProperties.Name") == "Export Presets");
        expect(Attr(selector, "ItemsSource") == "{Binding PresetsView}" && selector.Descendants().Any(e => e.Name.LocalName == "GroupStyle") && Attr(selector, "IsSynchronizedWithCurrentItem") == "False",
            "the selector lists the grouped view, a header for each group, never following the view's current item (made at idle, it would apply the first preset)");

        // The card style (CLAUDE.md 10.3) and its brushes, frozen, in App.xaml and every theme.
        var style = main.Descendants().First(e => e.Name.LocalName == "Style" && Attr(e, X + "Key") == "ExportCard");
        var setters = style.Elements().ToDictionary(e => Attr(e, "Property") ?? "", e => Attr(e, "Value") ?? "");
        expect(setters["Background"] == "{DynamicResource InspectorCardBrush}" && setters["BorderBrush"] == "{DynamicResource InspectorCardBorderBrush}" && setters["BorderThickness"] == "1"
               && setters["CornerRadius"] == "6" && setters["Padding"] == "12", "an ExportCard is the inspector card: its two brushes, a 1 px edge, 6 px corners, 12 px inside");
        var app = File.ReadAllText(Path.Combine(root, "src", "App.xaml"));
        var themes = File.ReadAllText(Path.Combine(root, "src", "Services", "ThemeManager.cs"));
        expect(app.Contains("<SolidColorBrush x:Key=\"InspectorCardBrush\" po:Freeze=\"True\" Color=\"#1E1E22\" />", StringComparison.Ordinal)
               && app.Contains("<SolidColorBrush x:Key=\"InspectorCardBorderBrush\" po:Freeze=\"True\" Color=\"#2A2A30\" />", StringComparison.Ordinal)
               && themes.Contains("[\"InspectorCardBrush\"]", StringComparison.Ordinal) && themes.Contains("[\"InspectorCardBorderBrush\"]", StringComparison.Ordinal),
            "the brushes are #1E1E22 and #2A2A30, frozen, in App.xaml, and every theme sets them (ThemeManager's look defaults)");
        expect(MainWindow.ExportColumnCount(1400) == 3 && MainWindow.ExportColumnCount(924) == 3 && MainWindow.ExportColumnCount(923) == 1 && MainWindow.ExportColumnCount(320) == 1,
            "the cards stand side by side while each has 300 px (924 px with the gaps), one over the other below");

        // Gone: the Video tab, the Easy | Advanced switch and its cards, the dead -hwaccel switch, the crop cross; the Audio tab's copy of the sound row.
        expect(!main.Descendants().Any(e => e.Name.LocalName == "TabItem" && Attr(e, "Header") == "Video") && AppSettings.DefaultHiddenTabs.Count == 0,
            "the Video tab is retired (its controls are these cards), and nothing is hidden to start with");
        expect(!Regex.IsMatch(mainText, "EasyExport|Easy Social|AdvancedExport|ExportNoSections|HardwareDecoding|hwaccel|ResetCropCommand|Interactive Crop|IsInteractiveCropActive|Crop Top|Crop Left|Crop Right|Crop Bottom|Encoding Presets"),
            "MainWindow.xaml names no Easy Social Export, no Hardware Decoding (-hwaccel), no crop cross (Reset Crop, Interactive Crop), no Encoding Presets");
        var audio = main.Descendants().First(e => e.Name.LocalName == "TabItem" && Attr(e, "Header") == "Audio");
        var audioBound = audio.Descendants().SelectMany(Bound).ToList();
        expect(!audioBound.Intersect(["AudioEncoder", "AudioBitrate", "AudioSampleRate", "AudioChannels", "MergeAudioTracks", "NormalizeAudio"]).Any()
               && audio.Descendants().Count(e => ((string?)e.Attribute("Text"))?.StartsWith("{Binding DuckAmountDb", StringComparison.Ordinal) == true) == 2,
            "the Audio tab no longer repeats the codec, bitrate, rate, channels, mix and normalize; its Duck Amount stays (inline and folded)");
        var tracks = File.ReadAllText(Path.Combine(root, "src", "MainWindow.Tracks.cs"));
        expect(tracks.Contains("Header = \"Interactive Crop\"", StringComparison.Ordinal) && tracks.Contains("\"Reset Crop\"", StringComparison.Ordinal)
               && typeof(MainViewModel).GetMethod("CropInteractively") is not null && typeof(MainViewModel).GetMethod("ResetClipCrop") is not null,
            "Interactive Crop and Reset Crop are on a clip's menu (Transform, Sizing & Position > Crop), on the clip");
        expect(typeof(AppSettings).GetProperty("UseEasySocialExport") is null && typeof(AppSettings).GetProperty("DefaultHardwareDecoding") is null
               && typeof(EncodingPreset).GetProperty("HardwareDecoding") is null && typeof(SequenceExportState).GetProperty("HardwareDecodeArguments") is null
               && typeof(GpuAdapters).GetProperty("DecodeArguments") is null && typeof(MainViewModel).GetProperty("HardwareDecoding") is null,
            "and their state is gone: no UseEasySocialExport, no DefaultHardwareDecoding, no HardwareDecoding on a preset or the view model, no -hwaccel arguments");
        var settings = File.ReadAllText(Path.Combine(root, "src", "Dialogs", "SettingsWindow.xaml"));
        expect(!settings.Contains("UseEasySocialExport", StringComparison.Ordinal) && !settings.Contains("DefaultHardwareDecoding", StringComparison.Ordinal),
            "Settings no longer offers Easy Social Export or a Hardware decoding default");
    }

    /// <summary>The repository's folder (where HandPegApp.csproj is), found upwards from the built program.</summary>
    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }
}
