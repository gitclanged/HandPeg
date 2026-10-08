using System.IO;
using System.Text.Json;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>Reads and writes presets.json next to the application.</summary>
public static class PresetStore
{
    private const string TikTokPresetName = "TikTok 60fps Strict";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "presets.json");

    /// <summary>The saved presets. The first launch creates the file with the built-in ones.</summary>
    public static List<EncodingPreset> Load()
    {
        var presets = new List<EncodingPreset>();
        if (File.Exists(FilePath))
        {
            try
            {
                presets = JsonSerializer.Deserialize<List<EncodingPreset>>(File.ReadAllText(FilePath)) ?? [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Leave a damaged file alone for the user to repair; run with the built-in presets meanwhile.
                return CreateDefaults();
            }
        }

        // Add built-in presets the file does not have yet: all of them on first launch,
        // and the new ones after an update. A preset the user saved under the same name is kept.
        var missing = CreateDefaults()
            .Where(builtIn => !presets.Any(p => p.Name.Equals(builtIn.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        // The vertical framer used to be fixed at 1080 x 1920. Now that its size can be set, the TikTok
        // preset has to name that size itself, or a smaller source would give a smaller frame.
        var tikTok = presets.FirstOrDefault(p =>
            p.Name == TikTokPresetName && p.FrameEngine
            && string.IsNullOrWhiteSpace(p.OutputWidth) && string.IsNullOrWhiteSpace(p.OutputHeight));
        if (tikTok is not null)
            (tikTok.OutputWidth, tikTok.OutputHeight) = ("1080", "1920");

        if (missing.Count > 0 || tikTok is not null)
        {
            presets.AddRange(missing);
            try
            {
                Save(presets);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read-only install folder: the presets still work for this session.
            }
        }

        return presets;
    }

    public static void Save(IEnumerable<EncodingPreset> presets) =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(presets, JsonOptions));

    private static List<EncodingPreset> CreateDefaults() =>
    [
        new()
        {
            Name = "Software HQ 1080p60",
            OutputHeight = "1080",
            VideoEncoder = "libx264",
            EncoderPreset = "slow",
            Profile = "high",
            Quality = 18,
            Framerate = "60",
            AudioBitrate = "192k",
        },
        new()
        {
            Name = "AMF Hardware 1440p",
            OutputHeight = "1440",
            VideoEncoder = "hevc_amf",
            Quality = 22,
            AudioBitrate = "192k",
        },
        new()
        {
            Name = "QuickSync AV1",
            VideoEncoder = "av1_qsv",
            Quality = 26,
            AudioEncoder = "libopus",
            AudioBitrate = "128k",
        },

        // Vertical 1080x1920 at 60 fps, kept inside what the upload pipelines accept without re-processing.
        new()
        {
            Name = TikTokPresetName,
            FrameEngine = true,
            UseVerticalResolution = true,
            OutputWidth = "1080",
            OutputHeight = "1920",
            VideoEncoder = "libx264",
            Profile = "high",
            Level = "4.2",
            RateControl = "Average Bitrate (ABR)",
            TargetBitrate = "15000",
            Framerate = "60",
            ExtraVideoArguments = "-pix_fmt yuv420p",
            AudioEncoder = "aac",
            AudioBitrate = "192k",
            Container = "mp4",
        },

        // Editing proxies: light to decode, so an NLE timeline scrubs smoothly.
        new()
        {
            Name = "Apple ProRes 422 Proxy",
            VideoEncoder = "prores_ks",
            ExtraVideoArguments = "-profile:v 0",
            AudioEncoder = "pcm_s16le",
            Container = "mov",
        },
        new()
        {
            Name = "DNxHR LB (Low Bandwidth)",
            VideoEncoder = "dnxhd",
            ExtraVideoArguments = "-profile:v dnxhr_lb",
            AudioEncoder = "pcm_s16le",
            Container = "mov",
        },
        new()
        {
            Name = "H.264 ALL-I Intra-Frame",
            VideoEncoder = "libx264",
            Tune = "fastdecode",
            Quality = 18,
            ExtraVideoArguments = "-g 1 -keyint_min 1",
            AudioEncoder = "Copy (Stream Copy)",
        },
    ];
}
