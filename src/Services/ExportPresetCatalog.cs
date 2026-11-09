using HandPegApp.Models;

namespace HandPegApp.Services;

// ----- Export Presets: the built-in ones (Pass 5.4B) -----
// One preset model (EncodingPreset) and one list: the built-ins here, in code, grouped by Category ("Platform & Social", "Studio &
// Archive"), then the user's own from presets.json ("My Presets"). Picking one applies its export fields only (MainViewModel.ApplyExportPreset):
// size, rate, encoder, quality, sound, container. Game Presets own the layout, the looks and the captions. Built-ins are never written
// to presets.json (PresetStore.Save leaves them out), so an update changes them for everyone; a user's preset of the same name is
// renamed as it is read (PresetStore.Load).
//
// An empty EncoderPreset is the encoder's best step (MainViewModel.BestStepOf: veryslow for x264 and x265, p7 NVENC, high_quality AMF,
// veryslow QuickSync; Media Foundation's -compression_level 100, 5.4C; none for SVT-AV1 and VP9): every built-in names none since 5.4C
// (the slowest step everywhere, Small File 720p too). 5.4D: Fast Proxy is SpeedFirst instead, the encoder's fastest step whichever it
// resolves to (MainViewModel.FastestStepOf), a flag rather than a step name. PreferredCodec names the codec(s) a hardware encoder is looked
// for first ("hevc,h264": HEVC, then H.264), VideoEncoder the software one when this PC has none of them. An empty TargetBitrate is the
// automatic one (MainViewModel.AutoBitrateKbps: from the output's size and rate). A FLAC, WAV or MP3 Container is an Audio Only export
// (5.4C, EncoderCommand.IsAudioOnly): the Sequence's sound alone, no picture drawn.

/// <summary>The Export Presets HandPeg comes with, and the names the built-ins of 5.4A and before had.</summary>
public static class ExportPresetCatalog
{
    private const string Vbr = "Average Bitrate (ABR)", Cq = "Constant Quality (CQ)", Match = "Match Project Settings";

    /// <summary>The prefix of the three Master Quality presets (the user's words: "Match Project — Master Quality").</summary>
    public const string MasterQuality = "Match Project — Master Quality";

    /// <summary>The built-in Export Presets, in the order the selector lists them (each group's own order).</summary>
    public static IReadOnlyList<EncodingPreset> BuiltIns { get; } = Make();

    /// <summary>
    /// What a built-in of 5.4A and before is called now, by its old name (a Smart Rule or the default preset naming one is renamed
    /// as it is read, PresetStore.Load). The built-ins whose names stayed (Fast Proxy, the editing codecs) are not here.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RenamedBuiltIns { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Remux / Cut Only"] = "Lossless Stream Copy",
        ["YouTube 4K"] = "YouTube 4K60",
        ["YouTube 1080p"] = "YouTube 1080p60",
        ["TikTok (Vertical)"] = "TikTok (PC Web)",
        ["Discord (8MB)"] = "Discord (10 MB)",
        ["High Quality H.264"] = MasterQuality + " (H.264)",
        ["Archive H.265"] = MasterQuality + " (HEVC)",
    };

    /// <summary>Whether a name is a built-in's (case aside): a user's preset may not take it.</summary>
    public static bool IsBuiltInName(string name) => BuiltIns.Any(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static List<EncodingPreset> Make()
    {
        const string platform = EncodingPreset.PlatformCategory, studio = EncodingPreset.StudioCategory;

        // A vertical 9:16 picture at 1080 x 1920 (the short-form platforms' own size).
        static EncodingPreset Vertical(string name, string detail) => new()
        {
            Name = name, Detail = detail, Category = EncodingPreset.PlatformCategory, UseVerticalResolution = true, OutputWidth = "1080", OutputHeight = "1920",
            Container = "mp4", WebOptimized = true, Profile = "high",
        };

        // A chat's size limit: the bitrate worked out from the output's length to land a little under it (the container's own bytes).
        static EncodingPreset Chat(int limitMb, int shortSide, string framerate, string audioBitrate) => new()
        {
            Name = $"Discord ({limitMb} MB)", Category = EncodingPreset.PlatformCategory,
            Detail = (shortSide > 0 ? $"{shortSide}p at most" : "The Sequence's size") + $", {(framerate == Match ? "the project's rate (60 at most)" : framerate + " fps")}. "
                     + $"H.264, the bitrate worked out from the length to stay under {limitMb} MB. AAC {audioBitrate}.",
            PreferredCodec = "h264", VideoEncoder = "libx264", Profile = "high", RateControl = Vbr, ShortSideCap = shortSide,
            TargetFileSize = (limitMb * 0.94).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
            Framerate = framerate, FramerateCap = framerate == Match ? 60 : 0, AudioEncoder = "aac", AudioBitrate = audioBitrate, Container = "mp4", WebOptimized = true,
        };

        return
        [
            // ----- Platform & Social -----
            // TikTok by upload client (D-Q3): the web uploader keeps a high bitrate, the phone apps re-encode and want less; iOS takes HEVC.
            With(Vertical("TikTok (PC Web)", "9:16, 1080 x 1920, the project's rate (60 at most). H.264, VBR 16 Mbps, AAC 192k."), p =>
            {
                (p.PreferredCodec, p.VideoEncoder, p.RateControl, p.TargetBitrate, p.FramerateCap) = ("h264", "libx264", Vbr, "16000", 60);
                (p.AudioEncoder, p.AudioBitrate) = ("aac", "192k");
            }),
            With(Vertical("TikTok (iOS)", "9:16, 1080 x 1920, 60 fps. HEVC on a hardware encoder (else H.264), VBR 12 Mbps, AAC 192k."), p =>
            {
                (p.PreferredCodec, p.VideoEncoder, p.RateControl, p.TargetBitrate, p.Framerate) = ("hevc,h264", "libx264", Vbr, "12000", "60");
                (p.AudioEncoder, p.AudioBitrate) = ("aac", "192k");
            }),
            With(Vertical("TikTok (Android)", "9:16, 1080 x 1920, 60 fps. H.264, VBR 10 Mbps, AAC 128k."), p =>
            {
                (p.PreferredCodec, p.VideoEncoder, p.RateControl, p.TargetBitrate, p.Framerate) = ("h264", "libx264", Vbr, "10000", "60");
                (p.AudioEncoder, p.AudioBitrate) = ("aac", "128k");
            }),
            With(Vertical("YouTube Shorts", "9:16, 1080 x 1920, the project's rate (60 at most). H.264, VBR at the automatic bitrate, AAC 192k."), p =>
            {
                (p.PreferredCodec, p.VideoEncoder, p.RateControl, p.FramerateCap) = ("h264", "libx264", Vbr, 60);
                (p.AudioEncoder, p.AudioBitrate) = ("aac", "192k");
            }),
            new()
            {
                Name = "YouTube 1080p60", Category = platform, Detail = "1080p at the project's rate (60 at most). H.264, VBR at the automatic bitrate (12 Mbps at 60 fps), AAC 256k.",
                OutputHeight = "1080", Framerate = Match, FramerateCap = 60, PreferredCodec = "h264", VideoEncoder = "libx264", Profile = "high", RateControl = Vbr,
                AudioEncoder = "aac", AudioBitrate = "256k", Container = "mp4", WebOptimized = true,
            },
            new()
            {
                Name = "YouTube 1440p60 (VP9 Trick)", Category = platform,
                Detail = "1440p at the project's rate (60 at most): an upload this size is served in the platform's better codec, even to 1080p viewers. HEVC or H.264, VBR (24 Mbps at 60 fps), AAC 320k.",
                OutputHeight = "1440", Framerate = Match, FramerateCap = 60, PreferredCodec = "hevc,h264", VideoEncoder = "libx264", Profile = "high", RateControl = Vbr,
                AudioEncoder = "aac", AudioBitrate = "320k", Container = "mp4", WebOptimized = true,
            },
            new()
            {
                Name = "YouTube 4K60", Category = platform, Detail = "2160p at the project's rate (60 at most). HEVC or H.264, VBR at the automatic bitrate (53 Mbps at 60 fps), AAC 320k.",
                OutputHeight = "2160", Framerate = Match, FramerateCap = 60, PreferredCodec = "hevc,h264", VideoEncoder = "libx264", Profile = "high", RateControl = Vbr,
                AudioEncoder = "aac", AudioBitrate = "320k", Container = "mp4", WebOptimized = true,
            },
            With(Vertical("Instagram Reels", "9:16, 1080 x 1920, the project's rate (60 at most). H.264, VBR 10 Mbps, AAC 128k."), p =>
            {
                (p.PreferredCodec, p.VideoEncoder, p.RateControl, p.TargetBitrate, p.FramerateCap) = ("h264", "libx264", Vbr, "10000", 60);
                (p.AudioEncoder, p.AudioBitrate) = ("aac", "128k");
            }),
            Chat(10, 720, "30", "96k"),
            Chat(50, 1080, Match, "128k"),
            Chat(500, 0, Match, "160k"),
            new()
            {
                Name = "X / Twitter", Category = platform, Detail = "1080p at most, the project's rate (60 at most). H.264, VBR at the automatic bitrate, AAC 128k.",
                ShortSideCap = 1080, Framerate = Match, FramerateCap = 60, PreferredCodec = "h264", VideoEncoder = "libx264", Profile = "high", RateControl = Vbr,
                AudioEncoder = "aac", AudioBitrate = "128k", Container = "mp4", WebOptimized = true,
            },
            new()
            {
                Name = "Reddit", Category = platform, Detail = "1080p at most, the project's rate (60 at most). H.264, VBR at the automatic bitrate, AAC 192k.",
                ShortSideCap = 1080, Framerate = Match, FramerateCap = 60, PreferredCodec = "h264", VideoEncoder = "libx264", Profile = "high", RateControl = Vbr,
                AudioEncoder = "aac", AudioBitrate = "192k", Container = "mp4", WebOptimized = true,
            },

            // ----- Studio & Archive -----
            // The project's own size and rate, on this PC's hardware encoder of the codec at its slowest step (5.4C: NVENC p7, QuickSync
            // veryslow, AMF high_quality, Media Foundation -compression_level 100), else the software encoder at its best (x264 and x265
            // veryslow). HEVC and AV1 in 10 bits: an HDR project exported with them keeps its HDR (MainViewModel.HdrPassthroughBlocker).
            new()
            {
                Name = MasterQuality + " (H.264)", Category = studio,
                Detail = "The project's size and rate. H.264 on this PC's hardware encoder at its slowest step (NVENC p7, QuickSync veryslow, AMF high_quality), else x264 veryslow; constant quality 18, AAC 320k.",
                PreferredCodec = "h264", VideoEncoder = "libx264", Profile = "high", RateControl = Cq, Quality = 18, AudioEncoder = "aac", AudioBitrate = "320k", Container = "mp4", WebOptimized = true,
            },
            new()
            {
                Name = MasterQuality + " (HEVC)", Category = studio,
                Detail = "The project's size and rate. HEVC on this PC's hardware encoder at its slowest step, else x265 veryslow; constant quality 20, 10-bit (an HDR project stays HDR), AAC 320k.",
                PreferredCodec = "hevc", VideoEncoder = "libx265", RateControl = Cq, Quality = 20, TenBit = true, AudioEncoder = "aac", AudioBitrate = "320k", Container = "mp4", WebOptimized = true,
            },
            new()
            {
                Name = MasterQuality + " (AV1)", Category = studio,
                Detail = "The project's size and rate. AV1 on this PC's hardware encoder at its slowest step, else SVT-AV1; constant quality 28, 10-bit (an HDR project stays HDR), AAC 320k.",
                PreferredCodec = "av1", VideoEncoder = "libsvtav1", RateControl = Cq, Quality = 28, TenBit = true, AudioEncoder = "aac", AudioBitrate = "320k", Container = "mp4", WebOptimized = true,
            },
            new()
            {
                Name = "Lossless Stream Copy", Category = studio, Detail = "No encoding: the file the Sequence plays is copied packet for packet, cut at its I-frames.",
                VideoEncoder = "copy", AudioEncoder = "Copy (Stream Copy)",
            },
            new()
            {
                Name = "Animated GIF", Category = studio, Detail = "480p at most, 15 fps, one palette, dithered. No sound.",
                ShortSideCap = 480, Framerate = "Custom", CustomFramerate = "15", Container = "gif",
            },
            new()
            {
                Name = "Animated WebP", Category = studio, Detail = "720p at most, 24 fps, looping. No sound.",
                ShortSideCap = 720, Framerate = "24", Container = "webp",
            },
            new()
            {
                Name = "Mezzanine Archive (ProRes 422 HQ)", Category = studio, Detail = "ProRes 422 HQ in .mov with PCM sound, at the project's size and rate: to edit or keep.",
                VideoEncoder = "prores_ks", ExtraVideoArguments = "-profile:v 3", AudioEncoder = "pcm_s16le", Container = "mov",
            },

            // The sound alone (5.4C, the user's Q5): rendered by the sound engine, every track mixed into one, encoded by FFmpeg; no picture
            // is drawn and no compositor started (GpuExport.RunAudioOnlyAsync). The podcast's -16 LUFS is a linear gain held by the peaks
            // (ExportSoundPlan.NormalizingGain): very peaky sound can land a little under it, which the log says.
            new()
            {
                Name = "Audio Only (FLAC)", Category = studio, Detail = "The Sequence's sound alone, every track mixed into one: lossless FLAC (16-bit) at the project's rate. No picture is drawn.",
                AudioEncoder = "flac", MergeAudioTracks = true, Container = "flac",
            },
            new()
            {
                Name = "Audio Only (WAV)", Category = studio, Detail = "The Sequence's sound alone, every track mixed into one: 16-bit PCM in a .wav at the project's rate. No picture is drawn.",
                AudioEncoder = "pcm_s16le", MergeAudioTracks = true, Container = "wav",
            },
            new()
            {
                Name = "Podcast / Voice (MP3, -16 LUFS)", Category = studio, Detail = "The Sequence's sound alone, mixed into one and normalized to -16 LUFS: MP3 at 128k. No picture is drawn.",
                AudioEncoder = "libmp3lame", AudioBitrate = "128k", MergeAudioTracks = true, NormalizeAudio = true, LoudnessTarget = -16, Container = "mp3",
            },

            // Kept from before (nothing lost): editing proxies, an intra-frame H.264, a quick look, a small file.
            new()
            {
                Name = "Apple ProRes 422 Proxy", Category = studio, Detail = "An editing proxy: light to decode, so a timeline scrubs smoothly. PCM sound, .mov.",
                VideoEncoder = "prores_ks", ExtraVideoArguments = "-profile:v 0", AudioEncoder = "pcm_s16le", Container = "mov",
            },
            new()
            {
                Name = "DNxHR LB (Low Bandwidth)", Category = studio, Detail = "An editing proxy in DNxHR LB. PCM sound, .mov.",
                VideoEncoder = "dnxhd", ExtraVideoArguments = "-profile:v dnxhr_lb", AudioEncoder = "pcm_s16le", Container = "mov",
            },
            new()
            {
                Name = "H.264 ALL-I Intra-Frame", Category = studio, Detail = "Every frame an I-frame: instant seeking in any editor. Constant quality 18, the sound copied.",
                VideoEncoder = "libx264", Tune = "fastdecode", RateControl = Cq, Quality = 18, ExtraVideoArguments = "-g 1 -keyint_min 1", AudioEncoder = "Copy (Stream Copy)",
            },
            // 5.4C: a hardware encoder of the codec first. 5.4D (Q2 b): the proxy at the encoder's fastest step (SpeedFirst, the one
            // exception to the slowest step everywhere: a proxy is for speed); the small file stays at the slowest, smaller for it.
            new()
            {
                Name = "Fast Proxy", Category = studio, Detail = "720p, H.264 on this PC's hardware encoder (else x264), at the encoder's fastest step, constant quality 28: small and quick, to look at or send for a check.",
                OutputHeight = "720", PreferredCodec = "h264", VideoEncoder = "libx264", RateControl = Cq, Quality = 28, SpeedFirst = true,
                AudioEncoder = "aac", AudioBitrate = "128k", Container = "mp4", WebOptimized = true,
            },
            new()
            {
                Name = "Small File 720p", Category = studio, Detail = "720p, HEVC on this PC's hardware encoder (else x265), at its slowest step, constant quality 26, Opus 96k in .mkv.",
                OutputHeight = "720", PreferredCodec = "hevc", VideoEncoder = "libx265", RateControl = Cq, Quality = 26, AudioEncoder = "libopus", AudioBitrate = "96k", Container = "mkv",
            },
        ];
    }

    private static EncodingPreset With(EncodingPreset preset, Action<EncodingPreset> set)
    {
        set(preset);
        return preset;
    }
}
