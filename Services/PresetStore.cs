using System.IO;
using System.Text.Json;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>Reads and writes presets.json in the settings folder (see <see cref="AppPaths"/>).</summary>
public static class PresetStore
{

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath => AppPaths.PresetsFile;

    private static Task<List<EncodingPreset>>? _early;

    /// <summary>Starts reading the presets away from the calling thread, for <see cref="TakeEarly"/> to hand over.</summary>
    public static void LoadEarly() => _early = Task.Run(Load);

    /// <summary>The presets that were read early, once; read now when none were.</summary>
    public static List<EncodingPreset> TakeEarly() =>
        Interlocked.Exchange(ref _early, null) is { } early ? early.GetAwaiter().GetResult() : Load();

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
        if (missing.Count > 0)
        {
            presets.AddRange(missing);
            try
            {
                Save(presets);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The settings folder cannot be written to: the presets still work for this session.
            }
        }

        return presets;
    }

    public static void Save(IEnumerable<EncodingPreset> presets)
    {
        Directory.CreateDirectory(AppPaths.Settings);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(presets, JsonOptions));
    }

    /// <summary>
    /// The presets HandPeg comes with: a dozen starting points, from no encoding at all to delivery formats
    /// for the places videos go, to editing proxies. All use software encoders, which every machine has; a
    /// hardware encoder is one click away on the Video tab.
    /// </summary>
    private static List<EncodingPreset> CreateDefaults() =>
    [
        // No encoding: the streams are copied as they are. For cutting, joining and changing the container.
        new() { Name = "Remux / Cut Only", VideoEncoder = "copy", AudioEncoder = "Copy (Stream Copy)" },

        // What YouTube asks for: H.264 High, 4:2:0, a closed GOP of half a second's frames or so, AAC at 48 kHz.
        new()
        {
            Name = "YouTube 4K", OutputHeight = "2160", VideoEncoder = "libx264", EncoderPreset = "slow", Profile = "high",
            Quality = 17, ExtraVideoArguments = "-pix_fmt yuv420p -bf 2 -g 30", AudioEncoder = "aac", AudioBitrate = "320k", Container = "mp4",
        },
        new()
        {
            Name = "YouTube 1080p", OutputHeight = "1080", VideoEncoder = "libx264", EncoderPreset = "slow", Profile = "high",
            Quality = 18, ExtraVideoArguments = "-pix_fmt yuv420p -bf 2 -g 30", AudioEncoder = "aac", AudioBitrate = "256k", Container = "mp4",
        },

        // Vertical 1080 x 1920 at 60 fps, kept inside what the upload pipelines accept without re-processing.
        new()
        {
            Name = "TikTok (Vertical)", FrameEngine = true, UseVerticalResolution = true, OutputWidth = "1080", OutputHeight = "1920",
            VideoEncoder = "libx264", Profile = "high", Level = "4.2", RateControl = "Average Bitrate (ABR)", TargetBitrate = "15000",
            Framerate = "60", ExtraVideoArguments = "-pix_fmt yuv420p", AudioEncoder = "aac", AudioBitrate = "192k", Container = "mp4",
        },

        // Quick and small, to look at or to send for a check: not for keeping.
        new()
        {
            Name = "Fast Proxy", OutputHeight = "720", VideoEncoder = "libx264", EncoderPreset = "ultrafast", Quality = 28,
            ExtraVideoArguments = "-pix_fmt yuv420p", AudioEncoder = "aac", AudioBitrate = "128k", Container = "mp4",
        },

        // Under Discord's free upload limit: the bitrate is worked out from the length of the video to land on 8 MB.
        new()
        {
            Name = "Discord (8MB)", OutputHeight = "720", VideoEncoder = "libx264", EncoderPreset = "slow", RateControl = "Average Bitrate (ABR)",
            TargetFileSize = "7.8", Framerate = "30", ExtraVideoArguments = "-pix_fmt yuv420p", AudioEncoder = "aac", AudioBitrate = "96k", Container = "mp4",
        },

        new()
        {
            Name = "High Quality H.264", VideoEncoder = "libx264", EncoderPreset = "slow", Profile = "high", Quality = 18,
            AudioEncoder = "aac", AudioBitrate = "192k",
        },
        new()
        {
            Name = "Archive H.265", VideoEncoder = "libx265", EncoderPreset = "slow", Quality = 20, AudioEncoder = "aac", AudioBitrate = "192k",
        },
        new()
        {
            Name = "Small File 720p", OutputHeight = "720", VideoEncoder = "libx265", EncoderPreset = "medium", Quality = 26,
            AudioEncoder = "libopus", AudioBitrate = "96k", Container = "mkv",
        },

        // Editing proxies: light to decode, so an NLE timeline scrubs smoothly.
        new()
        {
            Name = "Apple ProRes 422 Proxy", VideoEncoder = "prores_ks", ExtraVideoArguments = "-profile:v 0", AudioEncoder = "pcm_s16le", Container = "mov",
        },
        new()
        {
            Name = "DNxHR LB (Low Bandwidth)", VideoEncoder = "dnxhd", ExtraVideoArguments = "-profile:v dnxhr_lb", AudioEncoder = "pcm_s16le", Container = "mov",
        },
        new()
        {
            Name = "H.264 ALL-I Intra-Frame", VideoEncoder = "libx264", Tune = "fastdecode", Quality = 18,
            ExtraVideoArguments = "-g 1 -keyint_min 1", AudioEncoder = "Copy (Stream Copy)",
        },
    ];
}
