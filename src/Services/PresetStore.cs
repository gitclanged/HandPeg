using System.IO;
using System.Text.Json;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// Reads and writes presets.json in the settings folder (see <see cref="AppPaths"/>): the user's own Export Presets ("My Presets").
/// The built-in ones are in code (<see cref="ExportPresetCatalog"/>, 5.4B) and never written here.
/// </summary>
public static class PresetStore
{

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, TypeInfoResolver = AppSettingsJsonContext.Default };

    public static string FilePath => AppPaths.PresetsFile;

    private static Task<List<EncodingPreset>>? _early;

    /// <summary>Starts reading the presets away from the calling thread, for <see cref="TakeEarly"/> to hand over.</summary>
    public static void LoadEarly() => _early = Task.Run(Load);

    /// <summary>The presets that were read early, once; read now when none were.</summary>
    public static List<EncodingPreset> TakeEarly() =>
        Interlocked.Exchange(ref _early, null) is { } early ? early.GetAwaiter().GetResult() : Load();

    /// <summary>
    /// The user's saved presets (none on a first launch: the built-ins are <see cref="ExportPresetCatalog.BuiltIns"/>). Read on a thread
    /// of its own at launch (<see cref="LoadEarly"/>), so what it brings up to date is done off the window's thread (Law 1): the built-ins
    /// of 5.4A and before, written into the file then, leave it while they are as HandPeg made them (a changed one stays, the user's
    /// own); a preset named as a built-in is now is renamed "(Custom)"; and a Smart Rule or the default preset naming a built-in by its
    /// old name, or a preset just renamed, is given its new one (<see cref="AppSettings.RenamePresetReferences"/>, saved at once).
    /// </summary>
    public static List<EncodingPreset> Load()
    {
        ReadFailed = false;
        List<EncodingPreset> presets;
        switch (TryRead(FilePath, out var read, out var error))
        {
            case ReadOutcome.Read:
                presets = read!;
                break;

            case ReadOutcome.Unreadable:
                // The file is there but cannot be opened: run with the built-ins and never write over it this session (Save refuses).
                AppLog.Write($"Presets: {FilePath} could not be read; this session uses the built-in presets and does not write the file", error!);
                ReadFailed = true;
                return [];

            case ReadOutcome.Damaged:
                var aside = AtomicFile.SetAside(FilePath);
                AppLog.Write($"Presets: {FilePath} is damaged and was set aside as {Path.GetFileName(aside) ?? "(could not be moved)"}", error!);
                presets = Previous();
                break;

            default:
                presets = File.Exists(FilePath + AtomicFile.PreviousSuffix) ? Previous() : [];
                break;
        }

        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changed = Migrate(presets, renamed);

        // The presets file is written back below, so the old name is not there to follow next time: the settings keep the new one.
        // Update is safe off the window's thread (one lock) and saves on a background write, so nothing here writes on the UI thread.
        var references = ReferenceRenames(renamed);
        AppSettings.UpdateIf(settings => settings.RenamePresetReferences(references, name => presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))));

        if (changed)
        {
            try
            {
                Save(presets);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The settings folder cannot be written to: the presets still work for this session, and are brought up to date again next time.
            }
        }

        return presets;
    }

    /// <summary>
    /// Brings a list read from presets.json up to 5.4B (see <see cref="Load"/>): an old built-in still as HandPeg made it goes, a
    /// preset named as a built-in is renamed (old name to new into <paramref name="renamed"/>, for the references to follow), and nothing
    /// the file holds is marked built-in. Whether anything changed (to write back).
    /// </summary>
    internal static bool Migrate(List<EncodingPreset> presets, Dictionary<string, string>? renamed = null)
    {
        var legacy = LegacyBuiltIns();
        var removed = presets.RemoveAll(p => legacy.Any(old => old.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && SameExport(old, p)) && UntouchedByUser(p));
        var changed = removed > 0;
        foreach (var preset in presets)
        {
            if (preset.Category is not null)
                (preset.Category, changed) = (null, true);
            if (ExportPresetCatalog.IsBuiltInName(preset.Name))
            {
                var oldName = preset.Name;
                (preset.Name, changed) = (UniqueName(preset.Name, presets), true);
                renamed?.TryAdd(oldName.Trim(), preset.Name);
            }
        }

        return changed;
    }

    /// <summary>
    /// The renames a Smart Rule or the default preset follows: the built-ins' (<see cref="ExportPresetCatalog.RenamedBuiltIns"/>), then
    /// the user's own presets <see cref="Migrate"/> renamed "(Custom)", which win, so a reference to one never lands on the built-in that
    /// now has its old name.
    /// </summary>
    internal static Dictionary<string, string> ReferenceRenames(IReadOnlyDictionary<string, string> migrated)
    {
        var references = new Dictionary<string, string>(ExportPresetCatalog.RenamedBuiltIns, StringComparer.OrdinalIgnoreCase);
        foreach (var (old, now) in migrated)
            references[old] = now;
        return references;
    }

    /// <summary>A name for a user's preset that no built-in and no other preset of <paramref name="presets"/> has: the name, else "Name (Custom)", "Name (Custom 2)"...</summary>
    public static string UniqueName(string name, IEnumerable<EncodingPreset> presets)
    {
        var taken = presets.Select(p => p.Name).ToList();
        bool Free(string candidate) => !ExportPresetCatalog.IsBuiltInName(candidate) && !taken.Contains(candidate, StringComparer.OrdinalIgnoreCase);
        var trimmed = name.Trim();
        if (Free(trimmed))
            return trimmed;
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{trimmed} (Custom)" : $"{trimmed} (Custom {n})";
            if (Free(candidate))
                return candidate;
        }
    }

    // Whether a stored preset still has the export fields an old built-in was made with (its layout fields were never set by one).
    private static bool SameExport(EncodingPreset old, EncodingPreset stored)
    {
        static string Rate(string value) => value == "Same as source" ? "Match Project Settings" : value;
        return old.OutputWidth == stored.OutputWidth && old.OutputHeight == stored.OutputHeight && (old.UseVerticalResolution ?? false) == (stored.UseVerticalResolution ?? false)
               && old.VideoEncoder == stored.VideoEncoder && old.EncoderPreset == stored.EncoderPreset && old.SpeedFirst == stored.SpeedFirst && old.Tune == stored.Tune && old.Profile == stored.Profile
               && old.Level == stored.Level && old.RateControl == stored.RateControl && old.Quality == stored.Quality && old.TargetBitrate == stored.TargetBitrate
               && Rate(old.Framerate) == Rate(stored.Framerate) && old.ExtraVideoArguments == stored.ExtraVideoArguments && old.AudioEncoder == stored.AudioEncoder
               && old.AudioBitrate == stored.AudioBitrate && old.TargetFileSize == stored.TargetFileSize && old.Container == stored.Container;
    }

    // Whether everything a saved preset holds and an old built-in never set is still as a new preset has it: a user who saved their own
    // layout, sound, look or captions over an old built-in's name keeps it. One field at a time ("Same as source" is the old spelling
    // of "Match Project Settings"), not the whole JSON: other defaults changed in 5.4B, and an untouched built-in must still match.
    private static bool UntouchedByUser(EncodingPreset stored)
    {
        var fresh = new EncodingPreset();
        static string Match(string value) => value == "Same as source" ? "Match Project Settings" : value;
        static bool None<T>(List<T>? list) => list is null or { Count: 0 };
        var picture = stored.KeepAspectRatio == fresh.KeepAspectRatio && Match(stored.PixelAspectRatio) == fresh.PixelAspectRatio
                      && stored.CustomPixelAspectRatio == fresh.CustomPixelAspectRatio && Match(stored.Colorspace) == fresh.Colorspace
                      && stored.TenBit == fresh.TenBit && stored.Deinterlace == fresh.Deinterlace && stored.Denoise == fresh.Denoise && stored.LutPath == fresh.LutPath;
        var tuning = stored.NvencTune == fresh.NvencTune && stored.NvencMultipass == fresh.NvencMultipass && stored.NvencSpatialAq == fresh.NvencSpatialAq
                     && stored.NvencTemporalAq == fresh.NvencTemporalAq && stored.AmfUsage == fresh.AmfUsage && stored.QsvIcq == fresh.QsvIcq && stored.Lookahead == fresh.Lookahead;
        var sound = stored.AudioSampleRate == fresh.AudioSampleRate && stored.AudioChannels == fresh.AudioChannels && stored.MergeAudioTracks == fresh.MergeAudioTracks
                    && stored.NormalizeAudio == fresh.NormalizeAudio && stored.DuckAmountDb == fresh.DuckAmountDb && None(stored.AudioTracks);
        var layout = None(stored.Layers) && stored.MainLayer is null && stored.BackgroundHidden == fresh.BackgroundHidden && stored.BlurRadius == fresh.BlurRadius
                     && stored.BlurPasses == fresh.BlurPasses && stored.BackgroundDim == fresh.BackgroundDim && stored.BackgroundZoom == fresh.BackgroundZoom
                     && None(stored.BackgroundEffects) && stored.CenterZoom == fresh.CenterZoom && stored.CenterOffsetX == fresh.CenterOffsetX
                     && stored.CenterOffsetY == fresh.CenterOffsetY && stored.CropPercent is null or { Length: 0 }
                     && (stored.CropTop, stored.CropBottom, stored.CropLeft, stored.CropRight) == (0, 0, 0, 0);
        var captions = stored.AutoCaptions == fresh.AutoCaptions && stored.CaptionLayer is null && stored.WhisperPrompt == fresh.WhisperPrompt
                       && stored.WhisperLanguage == fresh.WhisperLanguage;
        var look = None(stored.SequenceEffects) && stored.ColorContrast == fresh.ColorContrast && stored.ColorBrightness == fresh.ColorBrightness
                   && stored.ColorSaturation == fresh.ColorSaturation && stored.ColorGamma == fresh.ColorGamma && stored.ColorHue == fresh.ColorHue
                   && stored.ColorRed == fresh.ColorRed && stored.ColorGreen == fresh.ColorGreen && stored.ColorBlue == fresh.ColorBlue
                   && stored.SharpenStrength == fresh.SharpenStrength && stored.FadeIn == fresh.FadeIn && stored.FadeOut == fresh.FadeOut;
        return picture && tuning && sound && layout && captions && look;
    }

    /// <summary>Writes the user's own presets (a built-in one is left out: it is HandPeg's, in code).</summary>
    public static void Save(IEnumerable<EncodingPreset> presets)
    {
        if (ReadFailed)
            throw new IOException("presets.json could not be read when HandPeg started, so it is not written over.");
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(presets.Where(p => !p.IsBuiltIn), JsonOptions), durable: true, keepPrevious: true);
    }

    /// <summary>True when presets.json existed at start but could not be opened: <see cref="Save"/> refuses to write over it this session.</summary>
    internal static bool ReadFailed { get; private set; }

    private enum ReadOutcome { Read, Missing, Unreadable, Damaged }

    /// <summary>Reads one presets file; open failures are retried briefly, anything wrong with the content is Damaged.</summary>
    private static ReadOutcome TryRead(string path, out List<EncodingPreset>? presets, out Exception? error)
    {
        (presets, error) = (null, null);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();

                var list = JsonSerializer.Deserialize(text, AppSettingsJsonContext.Default.ListEncodingPreset)
                           ?? throw new JsonException("The presets file holds null.");
                presets = [.. list.Where(p => p is not null)];
                return ReadOutcome.Read;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return ReadOutcome.Missing;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex;
                return ReadOutcome.Unreadable;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = ex;
                return ReadOutcome.Damaged;
            }
        }
    }

    /// <summary>The copy the last save left beside the file (presets.json.prev), or none.</summary>
    private static List<EncodingPreset> Previous()
    {
        var previous = FilePath + AtomicFile.PreviousSuffix;
        if (TryRead(previous, out var presets, out var error) == ReadOutcome.Read)
        {
            AppLog.Write($"Presets: restored from {Path.GetFileName(previous)}");
            return presets!;
        }

        if (error is not null)
            AppLog.Write($"Presets: {Path.GetFileName(previous)} could not be used either", error);
        return [];
    }

    /// <summary>
    /// The presets HandPeg 5.4A and before wrote into presets.json at its first launch, as it wrote them (the rate control and bitrate
    /// those versions defaulted to spelled out): the ones still as they were are taken out of the file (<see cref="Migrate"/>).
    /// </summary>
    internal static List<EncodingPreset> LegacyBuiltIns() => LegacyDefaults().Select(p =>
    {
        // Only the two size-aimed ones set a rate control of their own; the rest had the constant quality of the time.
        if (p.Name is not ("TikTok (Vertical)" or "Discord (8MB)"))
            p.RateControl = "Constant Quality (CQ)";
        if (p.TargetBitrate.Length == 0)
            p.TargetBitrate = "5000";
        return p;
    }).ToList();

    // The 5.4A built-ins (CreateDefaults then); LegacyBuiltIns puts back the rate control and bitrate those versions defaulted to.
    private static List<EncodingPreset> LegacyDefaults() =>
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
            Name = "TikTok (Vertical)", UseVerticalResolution = true, OutputWidth = "1080", OutputHeight = "1920",
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
